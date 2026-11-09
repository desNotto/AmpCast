#include "AmpCastAsio.h"

#include <objbase.h>
#include <sddl.h>
#include <stdio.h>
#include <string.h>
#include <wchar.h>

std::atomic<AmpCastAsio*> AmpCastAsio::s_active{ nullptr };

namespace {

constexpr ASIOSampleRate kRequiredRate = 48000.0;

int BytesPerSample(ASIOSampleType type)
{
    switch (type) {
    case ASIOSTInt16LSB: case ASIOSTInt16MSB:     return 2;
    case ASIOSTInt24LSB: case ASIOSTInt24MSB:     return 3;
    case ASIOSTFloat64LSB: case ASIOSTFloat64MSB: return 8;
    default:                                      return 4;
    }
}

// Adds one ASIO channel buffer into an interleaved stereo float buffer.
// The switch sits outside the loops: the inner loops are tight and branch-free.
void AccumulateToFloat(const void* src, ASIOSampleType type, long frames, float* dst, int channel)
{
    float* d = dst + channel;
    switch (type) {
    case ASIOSTFloat32LSB: {
        const float* s = static_cast<const float*>(src);
        for (long i = 0; i < frames; ++i) d[i * 2] += s[i];
        break;
    }
    case ASIOSTFloat64LSB: {
        const double* s = static_cast<const double*>(src);
        for (long i = 0; i < frames; ++i) d[i * 2] += static_cast<float>(s[i]);
        break;
    }
    case ASIOSTInt32LSB: {
        const int32_t* s = static_cast<const int32_t*>(src);
        constexpr float k = 1.0f / 2147483648.0f;
        for (long i = 0; i < frames; ++i) d[i * 2] += static_cast<float>(s[i]) * k;
        break;
    }
    case ASIOSTInt24LSB: {
        const uint8_t* s = static_cast<const uint8_t*>(src);
        constexpr float k = 1.0f / 2147483648.0f;
        for (long i = 0; i < frames; ++i, s += 3) {
            const int32_t v = static_cast<int32_t>((uint32_t(s[0]) << 8) | (uint32_t(s[1]) << 16) | (uint32_t(s[2]) << 24));
            d[i * 2] += static_cast<float>(v) * k;
        }
        break;
    }
    case ASIOSTInt16LSB: {
        const int16_t* s = static_cast<const int16_t*>(src);
        constexpr float k = 1.0f / 32768.0f;
        for (long i = 0; i < frames; ++i) d[i * 2] += static_cast<float>(s[i]) * k;
        break;
    }
    case ASIOSTInt32LSB16:
    case ASIOSTInt32LSB18:
    case ASIOSTInt32LSB20:
    case ASIOSTInt32LSB24: {
        const int bits = type == ASIOSTInt32LSB16 ? 16 : type == ASIOSTInt32LSB18 ? 18 : type == ASIOSTInt32LSB20 ? 20 : 24;
        const float k = 1.0f / static_cast<float>(1u << (bits - 1));
        const int32_t* s = static_cast<const int32_t*>(src);
        for (long i = 0; i < frames; ++i) d[i * 2] += static_cast<float>(s[i]) * k;
        break;
    }
    default:
        break;   // big-endian formats do not exist on Windows hardware
    }
}

DWORD ReadDword(HKEY key, const wchar_t* name, DWORD fallback)
{
    DWORD value = 0, size = sizeof(value), type = 0;
    if (RegQueryValueExW(key, name, nullptr, &type, reinterpret_cast<BYTE*>(&value), &size) == ERROR_SUCCESS && type == REG_DWORD)
        return value;
    return fallback;
}

} // namespace

// ---------------------------------------------------------------- lifetime

AmpCastAsio::AmpCastAsio()
{
    InterlockedIncrement(&g_objectCount);
}

AmpCastAsio::~AmpCastAsio()
{
    StopWatch();
    m_share.Stop();
    if (m_hw) {
        if (m_buffersCreated) {
            m_hw->stop();
            disposeBuffers();
        }
        m_hw->Release();
        m_hw = nullptr;
    }
    m_bus.Close();
    InterlockedDecrement(&g_objectCount);
}

HRESULT STDMETHODCALLTYPE AmpCastAsio::QueryInterface(REFIID riid, void** ppv)
{
    if (!ppv)
        return E_POINTER;
    // ASIO hosts pass the driver CLSID as the interface id.
    if (IsEqualIID(riid, IID_IUnknown) || IsEqualIID(riid, CLSID_AmpCastAsio)) {
        *ppv = static_cast<IASIO*>(this);
        AddRef();
        return S_OK;
    }
    *ppv = nullptr;
    return E_NOINTERFACE;
}

ULONG STDMETHODCALLTYPE AmpCastAsio::AddRef()
{
    return static_cast<ULONG>(InterlockedIncrement(&m_refCount));
}

ULONG STDMETHODCALLTYPE AmpCastAsio::Release()
{
    const LONG count = InterlockedDecrement(&m_refCount);
    if (count == 0)
        delete this;
    return static_cast<ULONG>(count);
}

// ---------------------------------------------------------------- config

void AmpCastAsio::SetError(const char* text)
{
    strncpy_s(m_error, text, _TRUNCATE);
}

void AmpCastAsio::LoadConfig()
{
    m_deviceName[0] = L'\0';
    m_lock48k = true;
    m_tapChannel = 0;

    HKEY key = nullptr;
    if (RegOpenKeyExW(HKEY_CURRENT_USER, kConfigKey, 0, KEY_READ, &key) != ERROR_SUCCESS)
        return;

    DWORD type = 0, size = sizeof(m_deviceName) - sizeof(wchar_t);
    if (RegQueryValueExW(key, L"AsioDevice", nullptr, &type, reinterpret_cast<BYTE*>(m_deviceName), &size) != ERROR_SUCCESS || type != REG_SZ)
        m_deviceName[0] = L'\0';
    m_deviceName[_countof(m_deviceName) - 1] = L'\0';

    m_lock48k    = ReadDword(key, L"Lock48k", 1) != 0;
    m_tapChannel = static_cast<long>(ReadDword(key, L"TapChannel", 0));
    RegCloseKey(key);
}

// Finds the real interface in HKLM\SOFTWARE\ASIO (registry view of the
// current process, so a 32-bit DAW gets the 32-bit hardware driver).
bool AmpCastAsio::ResolveHardwareDriver(CLSID& clsid, wchar_t* name, size_t nameChars)
{
    HKEY root = nullptr;
    if (RegOpenKeyExW(HKEY_LOCAL_MACHINE, L"SOFTWARE\\ASIO", 0, KEY_READ, &root) != ERROR_SUCCESS)
        return false;

    bool found = false;
    bool haveFallback = false;
    CLSID fallbackClsid{};
    wchar_t fallbackName[128] = {};

    for (DWORD i = 0;; ++i) {
        wchar_t sub[128];
        DWORD subLen = _countof(sub);
        if (RegEnumKeyExW(root, i, sub, &subLen, nullptr, nullptr, nullptr, nullptr) != ERROR_SUCCESS)
            break;
        if (_wcsicmp(sub, kDriverNameW) == 0)
            continue;

        wchar_t clsidText[64] = {};
        DWORD size = sizeof(clsidText) - sizeof(wchar_t), type = 0;
        HKEY drv = nullptr;
        if (RegOpenKeyExW(root, sub, 0, KEY_READ, &drv) != ERROR_SUCCESS)
            continue;
        const LSTATUS st = RegQueryValueExW(drv, L"CLSID", nullptr, &type, reinterpret_cast<BYTE*>(clsidText), &size);
        RegCloseKey(drv);
        if (st != ERROR_SUCCESS || type != REG_SZ)
            continue;

        CLSID candidate{};
        if (FAILED(CLSIDFromString(clsidText, &candidate)) || IsEqualCLSID(candidate, CLSID_AmpCastAsio))
            continue;   // never wrap ourselves

        if (m_deviceName[0] != L'\0' && _wcsicmp(sub, m_deviceName) == 0) {
            clsid = candidate;
            wcsncpy_s(name, nameChars, sub, _TRUNCATE);
            found = true;
            break;
        }
        if (!haveFallback) {
            haveFallback = true;
            fallbackClsid = candidate;
            wcsncpy_s(fallbackName, sub, _TRUNCATE);
        }
    }
    RegCloseKey(root);

    if (!found && haveFallback && m_deviceName[0] == L'\0') {
        // Nothing chosen in AmpCast yet: use the first real interface.
        clsid = fallbackClsid;
        wcsncpy_s(name, nameChars, fallbackName, _TRUNCATE);
        found = true;
    }
    return found;
}

bool AmpCastAsio::IsSampleRateAllowed(ASIOSampleRate rate) const
{
    return !m_lock48k || (rate > kRequiredRate - 0.5 && rate < kRequiredRate + 0.5);
}

// ---------------------------------------------------------------- IASIO

ASIOBool AmpCastAsio::init(void* sysHandle)
{
    if (m_hw)
        return ASIOTrue;

    LoadConfig();

    CLSID hwClsid{};
    wchar_t hwName[128] = {};
    if (!ResolveHardwareDriver(hwClsid, hwName, _countof(hwName))) {
        SetError(m_deviceName[0] ? "AmpCast: selected audio interface not found. Open AmpCast and choose it again."
                                 : "AmpCast: no ASIO audio interface installed.");
        return ASIOFalse;
    }

    IASIO* hw = nullptr;
    HRESULT hr = CoCreateInstance(hwClsid, nullptr, CLSCTX_INPROC_SERVER, hwClsid, reinterpret_cast<void**>(&hw));
    if (FAILED(hr) || !hw) {
        SetError("AmpCast: cannot load the audio interface ASIO driver (32/64-bit mismatch?).");
        return ASIOFalse;
    }

    if (!hw->init(sysHandle)) {
        char hwError[128] = {};
        hw->getErrorMessage(hwError);
        char text[256];
        sprintf_s(text, "AmpCast: interface error: %s", hwError[0] ? hwError : "init failed (is it used by another app?)");
        SetError(text);
        hw->Release();
        return ASIOFalse;
    }

    if (m_lock48k) {
        if (hw->canSampleRate(kRequiredRate) != ASE_OK) {
            SetError("AmpCast: the audio interface does not support 48 kHz.");
            hw->Release();
            return ASIOFalse;
        }
        ASIOSampleRate current = 0;
        if (hw->getSampleRate(&current) != ASE_OK || !IsSampleRateAllowed(current))
            hw->setSampleRate(kRequiredRate);
    }

    m_hw = hw;
    if (m_hw->getChannels(&m_hwInputs, &m_hwOutputs) != ASE_OK) {
        m_hwInputs = 0;
        m_hwOutputs = 0;
    }

    ASIOChannelInfo info{};
    info.channel = 0;
    info.isInput = m_hwOutputs > 0 ? ASIOFalse : ASIOTrue;
    if ((m_hwOutputs > 0 || m_hwInputs > 0) && m_hw->getChannelInfo(&info) == ASE_OK)
        m_virtualType = info.type;

    m_bus.Open();
    StartWatch();
    m_error[0] = '\0';
    return ASIOTrue;
}

void AmpCastAsio::getDriverName(char* name)
{
    if (name)
        strcpy_s(name, 32, kDriverNameA);
}

long AmpCastAsio::getDriverVersion()
{
    return 1;
}

void AmpCastAsio::getErrorMessage(char* string)
{
    if (string)
        strncpy_s(string, 124, m_error, _TRUNCATE);
}

ASIOError AmpCastAsio::start()
{
    if (!m_hw || !m_buffersCreated)
        return ASE_InvalidMode;
    const ASIOError e = m_hw->start();
    if (e == ASE_OK && m_busOwner) {
        m_bus.SetStreaming(true);
        m_share.Start(&m_bus);      // idles until stereo mode is switched on in AmpCast
    }
    return e;
}

ASIOError AmpCastAsio::stop()
{
    if (!m_hw)
        return ASE_NotPresent;
    const ASIOError e = m_hw->stop();
    m_share.Stop();
    if (m_busOwner)
        m_bus.SetStreaming(false);
    return e;
}

ASIOError AmpCastAsio::getChannels(long* numInputChannels, long* numOutputChannels)
{
    if (!m_hw)
        return ASE_NotPresent;
    if (numInputChannels)  *numInputChannels  = m_hwInputs;
    if (numOutputChannels) *numOutputChannels = m_hwOutputs + kVirtualOutputs;
    return ASE_OK;
}

ASIOError AmpCastAsio::getLatencies(long* inputLatency, long* outputLatency)
{
    return m_hw ? m_hw->getLatencies(inputLatency, outputLatency) : ASE_NotPresent;
}

ASIOError AmpCastAsio::getBufferSize(long* minSize, long* maxSize, long* preferredSize, long* granularity)
{
    return m_hw ? m_hw->getBufferSize(minSize, maxSize, preferredSize, granularity) : ASE_NotPresent;
}

ASIOError AmpCastAsio::canSampleRate(ASIOSampleRate sampleRate)
{
    if (!m_hw)
        return ASE_NotPresent;
    if (!IsSampleRateAllowed(sampleRate))
        return ASE_NoClock;   // AmpCast is locked to 48 kHz (Discord + plugins standard)
    return m_hw->canSampleRate(sampleRate);
}

ASIOError AmpCastAsio::getSampleRate(ASIOSampleRate* sampleRate)
{
    return m_hw ? m_hw->getSampleRate(sampleRate) : ASE_NotPresent;
}

ASIOError AmpCastAsio::setSampleRate(ASIOSampleRate sampleRate)
{
    if (!m_hw)
        return ASE_NotPresent;
    if (!IsSampleRateAllowed(sampleRate)) {
        SetError("AmpCast works at 48000 Hz only. Set your DAW project to 48 kHz.");
        return ASE_NoClock;
    }
    const ASIOError e = m_hw->setSampleRate(sampleRate);
    if (e == ASE_OK && m_busOwner)
        m_bus.SetFormat(sampleRate, m_bufferSize);
    return e;
}

ASIOError AmpCastAsio::getClockSources(ASIOClockSource* clocks, long* numSources)
{
    return m_hw ? m_hw->getClockSources(clocks, numSources) : ASE_NotPresent;
}

ASIOError AmpCastAsio::setClockSource(long reference)
{
    return m_hw ? m_hw->setClockSource(reference) : ASE_NotPresent;
}

ASIOError AmpCastAsio::getSamplePosition(ASIOSamples* sPos, ASIOTimeStamp* tStamp)
{
    return m_hw ? m_hw->getSamplePosition(sPos, tStamp) : ASE_NotPresent;
}

ASIOError AmpCastAsio::getChannelInfo(ASIOChannelInfo* info)
{
    if (!m_hw)
        return ASE_NotPresent;
    if (!info)
        return ASE_InvalidParameter;

    if (!info->isInput && info->channel >= m_hwOutputs) {
        const long v = info->channel - m_hwOutputs;
        if (v >= kVirtualOutputs)
            return ASE_InvalidParameter;
        info->isActive     = m_virtualActive[v] ? ASIOTrue : ASIOFalse;
        info->channelGroup = 0;
        info->type         = m_virtualType;
        strcpy_s(info->name, v == 0 ? "AmpCast Discord L" : "AmpCast Discord R");
        return ASE_OK;
    }
    if (info->channel < 0 || (info->isInput && info->channel >= m_hwInputs))
        return ASE_InvalidParameter;
    return m_hw->getChannelInfo(info);
}

ASIOError AmpCastAsio::createBuffers(ASIOBufferInfo* infos, long numChannels, long bufferSize, ASIOCallbacks* callbacks)
{
    if (!m_hw)
        return ASE_NotPresent;
    if (!infos || numChannels <= 0 || bufferSize <= 0 || !callbacks)
        return ASE_InvalidParameter;
    if (m_buffersCreated)
        disposeBuffers();

    AmpCastAsio* expected = nullptr;
    if (!s_active.compare_exchange_strong(expected, this) && expected != this)
        return ASE_InvalidMode;   // ASIO allows one active driver instance per process

    // Split the request: real channels go to the hardware, the two
    // "AmpCast Discord" outputs get buffers owned by us.
    std::vector<ASIOBufferInfo> hw;
    std::vector<int> map(static_cast<size_t>(numChannels), -1);
    hw.reserve(static_cast<size_t>(numChannels) + 1);
    bool virtualRequested[kVirtualOutputs] = {};

    for (long i = 0; i < numChannels; ++i) {
        const ASIOBufferInfo& in = infos[i];
        if (!in.isInput && in.channelNum >= m_hwOutputs) {
            const long v = in.channelNum - m_hwOutputs;
            if (v < 0 || v >= kVirtualOutputs) { s_active = nullptr; return ASE_InvalidParameter; }
            virtualRequested[v] = true;
            continue;
        }
        if (in.channelNum < 0 || (in.isInput && in.channelNum >= m_hwInputs)) { s_active = nullptr; return ASE_InvalidParameter; }
        map[static_cast<size_t>(i)] = static_cast<int>(hw.size());
        ASIOBufferInfo b{};
        b.isInput = in.isInput;
        b.channelNum = in.channelNum;
        hw.push_back(b);
    }
    if (hw.empty()) {
        // The DAW asked only for the Discord outputs: we still need the
        // hardware running as the clock, so open one silent output.
        if (m_hwOutputs <= 0) { s_active = nullptr; return ASE_InvalidMode; }
        ASIOBufferInfo b{};
        b.isInput = ASIOFalse;
        b.channelNum = 0;
        hw.push_back(b);
    }

    const size_t bytesPerBuffer = static_cast<size_t>(bufferSize) * BytesPerSample(m_virtualType);
    m_virtualMemory.assign(bytesPerBuffer * kVirtualOutputs * 2, 0);
    m_scratch.assign(static_cast<size_t>(bufferSize) * 2, 0.0f);

    m_host = callbacks;
    m_hwCallbacks.bufferSwitch         = &AmpCastAsio::OnBufferSwitch;
    m_hwCallbacks.sampleRateDidChange  = &AmpCastAsio::OnSampleRateDidChange;
    m_hwCallbacks.asioMessage          = &AmpCastAsio::OnAsioMessage;
    m_hwCallbacks.bufferSwitchTimeInfo = &AmpCastAsio::OnBufferSwitchTimeInfo;

    const ASIOError e = m_hw->createBuffers(hw.data(), static_cast<long>(hw.size()), bufferSize, &m_hwCallbacks);
    if (e != ASE_OK) {
        m_host = nullptr;
        s_active = nullptr;
        SetError("AmpCast: the audio interface rejected this buffer size.");
        return e;
    }

    // Give the DAW the hardware buffer pointers directly (zero-copy, zero added latency).
    m_tapCount = 0;
    for (long i = 0; i < numChannels; ++i) {
        ASIOBufferInfo& out = infos[i];
        const int hwIndex = map[static_cast<size_t>(i)];
        if (hwIndex >= 0) {
            out.buffers[0] = hw[static_cast<size_t>(hwIndex)].buffers[0];
            out.buffers[1] = hw[static_cast<size_t>(hwIndex)].buffers[1];
            if (!out.isInput && (out.channelNum == m_tapChannel || out.channelNum == m_tapChannel + 1) && m_tapCount < 8) {
                ASIOChannelInfo ci{};
                ci.channel = out.channelNum;
                ci.isInput = ASIOFalse;
                m_hw->getChannelInfo(&ci);
                TapSource& t = m_taps[m_tapCount++];
                t.buffers[0] = out.buffers[0];
                t.buffers[1] = out.buffers[1];
                t.type       = ci.type;
                t.busChannel = out.channelNum == m_tapChannel ? 0 : 1;
            }
        } else {
            const long v = out.channelNum - m_hwOutputs;
            unsigned char* base = m_virtualMemory.data() + bytesPerBuffer * 2 * static_cast<size_t>(v);
            out.buffers[0] = base;
            out.buffers[1] = base + bytesPerBuffer;
            if (m_tapCount < 8) {
                TapSource& t = m_taps[m_tapCount++];
                t.buffers[0] = out.buffers[0];
                t.buffers[1] = out.buffers[1];
                t.type       = m_virtualType;
                t.busChannel = static_cast<int>(v);
            }
        }
    }
    for (long v = 0; v < kVirtualOutputs; ++v)
        m_virtualActive[v] = virtualRequested[v];

    m_bufferSize = bufferSize;
    m_buffersCreated = true;

    m_busOwner = m_bus.TryAcquire();
    if (m_busOwner) {
        ASIOSampleRate rate = 0;
        m_hw->getSampleRate(&rate);
        m_bus.SetFormat(rate, bufferSize);
    }
    return ASE_OK;
}

ASIOError AmpCastAsio::disposeBuffers()
{
    if (!m_hw || !m_buffersCreated)
        return ASE_InvalidMode;
    const ASIOError e = m_hw->disposeBuffers();
    m_share.Stop();
    if (m_busOwner) {
        m_bus.Release();
        m_busOwner = false;
    }
    m_buffersCreated = false;
    m_tapCount = 0;
    for (long v = 0; v < kVirtualOutputs; ++v)
        m_virtualActive[v] = false;
    m_host = nullptr;
    AmpCastAsio* expected = this;
    s_active.compare_exchange_strong(expected, nullptr);
    return e;
}

ASIOError AmpCastAsio::controlPanel()
{
    // Buffer size (64/128) is set in the real interface's own panel.
    return m_hw ? m_hw->controlPanel() : ASE_NotPresent;
}

ASIOError AmpCastAsio::future(long selector, void* opt)
{
    return m_hw ? m_hw->future(selector, opt) : ASE_NotPresent;
}

ASIOError AmpCastAsio::outputReady()
{
    return m_hw ? m_hw->outputReady() : ASE_NotPresent;
}

// ---------------------------------------------------------------- real-time path

void AmpCastAsio::Tap(long index)
{
    if (!m_busOwner || m_tapCount == 0)
        return;
    float* scratch = m_scratch.data();
    const long frames = m_bufferSize;
    memset(scratch, 0, sizeof(float) * 2 * static_cast<size_t>(frames));
    for (int i = 0; i < m_tapCount; ++i) {
        const TapSource& t = m_taps[i];
        AccumulateToFloat(t.buffers[index & 1], t.type, frames, scratch, t.busChannel);
    }
    m_bus.Write(scratch, static_cast<uint32_t>(frames));
}

void __cdecl AmpCastAsio::OnBufferSwitch(long index, ASIOBool directProcess)
{
    AmpCastAsio* self = s_active.load(std::memory_order_acquire);
    if (!self || !self->m_host)
        return;
    self->m_host->bufferSwitch(index, directProcess);   // DAW reads guitar, writes headphones
    self->Tap(index);                                    // copy of the DAW master -> Discord
}

ASIOTime* __cdecl AmpCastAsio::OnBufferSwitchTimeInfo(ASIOTime* params, long index, ASIOBool directProcess)
{
    AmpCastAsio* self = s_active.load(std::memory_order_acquire);
    if (!self || !self->m_host)
        return params;
    ASIOTime* result = params;
    if (self->m_host->bufferSwitchTimeInfo)
        result = self->m_host->bufferSwitchTimeInfo(params, index, directProcess);
    else
        self->m_host->bufferSwitch(index, directProcess);
    self->Tap(index);
    return result;
}

void __cdecl AmpCastAsio::OnSampleRateDidChange(ASIOSampleRate rate)
{
    AmpCastAsio* self = s_active.load(std::memory_order_acquire);
    if (!self || !self->m_host)
        return;
    if (self->m_busOwner)
        self->m_bus.SetFormat(rate, self->m_bufferSize);
    if (self->m_host->sampleRateDidChange)
        self->m_host->sampleRateDidChange(rate);
}

long __cdecl AmpCastAsio::OnAsioMessage(long selector, long value, void* message, double* opt)
{
    // The hardware driver asks the host about its capabilities (time info,
    // reset requests, ...). We are transparent: forward everything to the DAW.
    AmpCastAsio* self = s_active.load(std::memory_order_acquire);
    if (!self || !self->m_host || !self->m_host->asioMessage)
        return 0;
    return self->m_host->asioMessage(selector, value, message, opt);
}

// ---------------------------------------------------------------- reset watcher

// AmpCast.exe signals Local\AmpCast_DriverReset when the user picks another
// interface; we ask the DAW to re-open the driver so the change applies live.
void AmpCastAsio::StartWatch()
{
    if (m_watchThread)
        return;

    SECURITY_ATTRIBUTES sa{ sizeof(sa), nullptr, FALSE };
    PSECURITY_DESCRIPTOR sd = nullptr;
    if (ConvertStringSecurityDescriptorToSecurityDescriptorW(L"D:(A;;GA;;;WD)(A;;GA;;;SY)S:(ML;;NW;;;LW)", SDDL_REVISION_1, &sd, nullptr))
        sa.lpSecurityDescriptor = sd;
    m_resetEvent = CreateEventW(sd ? &sa : nullptr, FALSE, FALSE, ampcast::kResetEventName);
    if (sd)
        LocalFree(sd);

    m_stopEvent = CreateEventW(nullptr, TRUE, FALSE, nullptr);
    if (!m_resetEvent || !m_stopEvent)
        return;
    // Drop a stale signal left over from before this DAW opened the driver.
    WaitForSingleObject(m_resetEvent, 0);
    m_watchThread = CreateThread(nullptr, 0, &AmpCastAsio::WatchThreadProc, this, 0, nullptr);
}

void AmpCastAsio::StopWatch()
{
    if (m_watchThread) {
        SetEvent(m_stopEvent);
        WaitForSingleObject(m_watchThread, 2000);
        CloseHandle(m_watchThread);
        m_watchThread = nullptr;
    }
    if (m_stopEvent)  { CloseHandle(m_stopEvent);  m_stopEvent = nullptr; }
    if (m_resetEvent) { CloseHandle(m_resetEvent); m_resetEvent = nullptr; }
}

DWORD WINAPI AmpCastAsio::WatchThreadProc(void* param)
{
    AmpCastAsio* self = static_cast<AmpCastAsio*>(param);
    HANDLE handles[2] = { self->m_stopEvent, self->m_resetEvent };
    for (;;) {
        const DWORD r = WaitForMultipleObjects(2, handles, FALSE, INFINITE);
        if (r != WAIT_OBJECT_0 + 1)
            break;
        self->RequestReset();
    }
    return 0;
}

void AmpCastAsio::RequestReset()
{
    ASIOCallbacks* host = m_host;
    if (!host || !host->asioMessage)
        return;
    if (host->asioMessage(kAsioSelectorSupported, kAsioResetRequest, nullptr, nullptr) == 1)
        host->asioMessage(kAsioResetRequest, 0, nullptr, nullptr);
}
