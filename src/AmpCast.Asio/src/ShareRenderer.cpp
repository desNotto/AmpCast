#include "ShareRenderer.h"

#include <audioclient.h>
#include <avrt.h>
#include <mmdeviceapi.h>

#include <algorithm>
#include <cmath>
#include <string.h>

namespace ampcast {

namespace {

constexpr wchar_t kConfigKey[]   = L"Software\\desNotto\\AmpCast";
constexpr wchar_t kShareDevice[] = L"ShareDevice";
constexpr double  kOutputRate    = 48000.0;
constexpr REFERENCE_TIME kBufferHns = 400000;   // 40 ms WASAPI buffer
constexpr uint32_t kMaxWriteFrames = 960;       // write at most 20 ms per wake-up (keeps latency low)

constexpr double kPi = 3.14159265358979323846;

LONG64 AtomicRead64(volatile LONG64* p) { return InterlockedCompareExchange64(p, 0, 0); }

template <typename T> void SafeRelease(T*& p)
{
    if (p) { p->Release(); p = nullptr; }
}

} // namespace

// ============================================================ BusResampler

BusResampler::BusResampler()
    : m_kernel(size_t(kPhases + 1) * kTaps),
      m_window((size_t(kChunk * kMaxStep * (1 + kMaxCorrection)) + kTaps + 4) * kBusChannels)
{
}

void BusResampler::Reset()
{
    m_primed = false;
    m_target = 0;
    m_fraction = 0;
    m_bufferedMs = 0;
}

void BusResampler::JumpTo(LONG64 position)
{
    m_readPos = position;
    m_fraction = 0;
    m_averageFill = m_target;
}

bool BusResampler::Read(BusHeader* h, const float* ring, double outRate, float* dst, uint32_t frames)
{
    double sourceRate = h->sampleRate;
    if (!(sourceRate > 8000.0 && sourceRate < 400000.0))
        sourceRate = 48000.0;
    if (sourceRate != m_sourceRate) {
        m_sourceRate = sourceRate;
        BuildKernel(std::min(1.0, outRate / sourceRate) * 0.92);
        Reset();
    }
    const long block = h->bufferSize;
    if (block != m_sourceBlock) {
        m_sourceBlock = block > 0 ? block : 0;
        m_target = 0;
    }

    const bool alive = h->streaming != 0 &&
                       static_cast<LONG64>(GetTickCount64()) - AtomicRead64(&h->heartbeatMs) < 400;
    if (!alive) {
        memset(dst, 0, size_t(frames) * kBusChannels * sizeof(float));
        m_primed = false;
        m_bufferedMs = 0;
        return false;
    }

    const LONG64 write = AtomicRead64(&h->writePos);
    LONG64 available = write - m_readPos;

    const double nominalStep = m_sourceRate / outRate;
    const long blockSource = static_cast<long>(std::ceil(frames * nominalStep)) + kTaps;
    // The ring fills in bursts of the DAW buffer: keep a whole burst plus our own reads buffered.
    const long need = std::max(static_cast<long>(m_sourceRate * kTargetMs / 1000.0),
                               m_sourceBlock + blockSource * 2 + static_cast<long>(m_sourceRate * 0.003));
    if (need > m_target) m_target = need;
    const long target = m_target;

    if (available < 0 || available > LONG64(target) * 3 + blockSource ||
        available > LONG64(kBusCapacityFrames) - blockSource * 2) {
        JumpTo(write - target);       // writer restarted or we fell far behind
        available = target;
    }

    if (!m_primed) {
        if (available < target) {
            memset(dst, 0, size_t(frames) * kBusChannels * sizeof(float));
            m_bufferedMs = available * 1000.0 / m_sourceRate;
            return false;
        }
        m_primed = true;
        JumpTo(write - target);
        available = target;
    }

    // Drift control: more buffered than wanted -> consume slightly faster.
    m_averageFill += (available - m_averageFill) * 0.05;
    const double excessSeconds = (m_averageFill - target) / m_sourceRate;
    const double targetMs = target * 1000.0 / m_sourceRate;
    const double correction = std::clamp(excessSeconds * kCorrectionGain * 1000.0 / std::max(targetMs, 1.0),
                                         -kMaxCorrection, kMaxCorrection);
    m_bufferedMs = m_averageFill * 1000.0 / m_sourceRate;

    const double step = nominalStep * (1.0 + correction);
    uint32_t done = 0;
    while (done < frames) {
        const uint32_t chunk = std::min<uint32_t>(kChunk, frames - done);
        const uint32_t produced = Convert(ring, write, step, dst + size_t(done) * kBusChannels, chunk);
        done += produced;
        if (produced < chunk) {
            memset(dst + size_t(done) * kBusChannels, 0, size_t(frames - done) * kBusChannels * sizeof(float));
            m_primed = false;     // underrun: wait for the target fill again
            break;
        }
    }
    return true;
}

uint32_t BusResampler::Convert(const float* ring, LONG64 write, double step, float* dst, uint32_t frames)
{
    const LONG64 start = m_readPos - kBefore;
    double last = m_fraction + (frames - 1) * step;
    LONG64 end = m_readPos + static_cast<LONG64>(last) + kAfter + 1;
    if (end > write) {
        const double room = double(write - kAfter - 1 - m_readPos) - m_fraction;
        if (room < 0) return 0;
        frames = std::min<uint32_t>(frames, static_cast<uint32_t>(room / step) + 1);
        last = m_fraction + (frames - 1) * step;
        end = m_readPos + static_cast<LONG64>(last) + kAfter + 1;
    }

    // Copy the source window out of the ring (handles wrap-around).
    const uint32_t count = static_cast<uint32_t>(end - start);
    const uint32_t mask = kBusCapacityFrames - 1;
    const uint32_t index = static_cast<uint32_t>(start) & mask;
    const uint32_t first = std::min(count, kBusCapacityFrames - index);
    memcpy(m_window.data(), ring + size_t(index) * kBusChannels, size_t(first) * kBusChannels * sizeof(float));
    if (first < count)
        memcpy(m_window.data() + size_t(first) * kBusChannels, ring, size_t(count - first) * kBusChannels * sizeof(float));

    const float* kernel = m_kernel.data();
    const float* window = m_window.data();
    double position = m_fraction;
    for (uint32_t i = 0; i < frames; ++i) {
        const int whole = static_cast<int>(position);
        const double phase = (position - whole) * kPhases;
        const int p = static_cast<int>(phase);
        const float mix = static_cast<float>(phase - p);
        const float* k0 = kernel + size_t(p) * kTaps;
        const float* k1 = k0 + kTaps;
        const float* w = window + size_t(whole) * kBusChannels;
        float l = 0.f, r = 0.f;
        for (int t = 0; t < kTaps; ++t) {
            const float c = k0[t] + (k1[t] - k0[t]) * mix;
            l += w[t * 2] * c;
            r += w[t * 2 + 1] * c;
        }
        dst[i * 2] = l;
        dst[i * 2 + 1] = r;
        position += step;
    }

    const LONG64 advance = static_cast<LONG64>(position);
    m_readPos += advance;
    m_fraction = position - advance;
    return frames;
}

void BusResampler::BuildKernel(double cutoff)
{
    if (std::fabs(cutoff - m_kernelCutoff) < 1e-9) return;
    m_kernelCutoff = cutoff;
    for (int p = 0; p <= kPhases; ++p) {
        const double frac = double(p) / kPhases;
        double sum = 0;
        float* row = m_kernel.data() + size_t(p) * kTaps;
        double h[kTaps];
        for (int t = 0; t < kTaps; ++t) {
            const double x = (t - kBefore) - frac;
            const double sinc = std::fabs(x) < 1e-12 ? 1.0 : std::sin(kPi * cutoff * x) / (kPi * cutoff * x);
            const double u = x / kAfter;
            const double blackman = std::fabs(u) >= 1 ? 0 : 0.42 + 0.5 * std::cos(kPi * u) + 0.08 * std::cos(2 * kPi * u);
            h[t] = cutoff * sinc * blackman;
            sum += h[t];
        }
        for (int t = 0; t < kTaps; ++t)
            row[t] = static_cast<float>(h[t] / sum);
    }
}

// ============================================================ ShareRenderer

void ShareRenderer::Start(SharedBus* bus)
{
    if (m_thread || !bus || !bus->IsOpen())
        return;
    m_bus = bus;
    m_stopEvent = CreateEventW(nullptr, TRUE, FALSE, nullptr);
    m_thread = CreateThread(nullptr, 0, &ThreadProc, this, 0, nullptr);
    if (!m_thread) {
        CloseHandle(m_stopEvent);
        m_stopEvent = nullptr;
    }
}

void ShareRenderer::Stop()
{
    if (!m_thread)
        return;
    SetEvent(m_stopEvent);
    WaitForSingleObject(m_thread, 3000);
    CloseHandle(m_thread);
    CloseHandle(m_stopEvent);
    m_thread = nullptr;
    m_stopEvent = nullptr;
    if (m_bus && m_bus->Header())
        InterlockedExchange(&m_bus->Header()->shareState, kShareOff);
}

DWORD WINAPI ShareRenderer::ThreadProc(void* param)
{
    static_cast<ShareRenderer*>(param)->Run();
    return 0;
}

void ShareRenderer::Run()
{
    const HRESULT coInit = CoInitializeEx(nullptr, COINIT_MULTITHREADED);
    DWORD taskIndex = 0;
    HANDLE mmcss = AvSetMmThreadCharacteristicsW(L"Audio", &taskIndex);

    BusHeader* h = m_bus->Header();
    const float* ring = m_bus->Data();

    IMMDeviceEnumerator* enumerator = nullptr;
    CoCreateInstance(__uuidof(MMDeviceEnumerator), nullptr, CLSCTX_ALL, IID_PPV_ARGS(&enumerator));

    IAudioClient*       client = nullptr;
    IAudioRenderClient* render = nullptr;
    HANDLE   audioEvent = CreateEventW(nullptr, FALSE, FALSE, nullptr);
    UINT32   bufferFrames = 0;
    LONG     openedSerial = -1;
    ULONGLONG retryAt = 0;
    float    gain = 0.f;
    const float glide = 1.f - std::exp(-1.f / (0.015f * static_cast<float>(kOutputRate)));   // ~15 ms
    BusResampler resampler;

    auto setState = [h](LONG s) { InterlockedExchange(&h->shareState, s); };
    auto close = [&]() {
        if (client) client->Stop();
        SafeRelease(render);
        SafeRelease(client);
    };
    auto open = [&]() -> HRESULT {
        wchar_t id[512] = {};
        DWORD bytes = sizeof(id);
        if (RegGetValueW(HKEY_CURRENT_USER, kConfigKey, kShareDevice, RRF_RT_REG_SZ, nullptr, id, &bytes) != ERROR_SUCCESS || !id[0])
            return HRESULT_FROM_WIN32(ERROR_NOT_FOUND);
        if (!enumerator)
            return E_FAIL;
        IMMDevice* device = nullptr;
        HRESULT hr = enumerator->GetDevice(id, &device);
        if (FAILED(hr)) return HRESULT_FROM_WIN32(ERROR_NOT_FOUND);
        hr = device->Activate(__uuidof(IAudioClient), CLSCTX_ALL, nullptr, reinterpret_cast<void**>(&client));
        device->Release();
        if (FAILED(hr)) return hr;

        WAVEFORMATEX wf{};
        wf.wFormatTag = WAVE_FORMAT_IEEE_FLOAT;
        wf.nChannels = 2;
        wf.nSamplesPerSec = static_cast<DWORD>(kOutputRate);
        wf.wBitsPerSample = 32;
        wf.nBlockAlign = 8;
        wf.nAvgBytesPerSec = wf.nSamplesPerSec * wf.nBlockAlign;
        hr = client->Initialize(AUDCLNT_SHAREMODE_SHARED,
                                AUDCLNT_STREAMFLAGS_EVENTCALLBACK | AUDCLNT_STREAMFLAGS_NOPERSIST |
                                AUDCLNT_STREAMFLAGS_AUTOCONVERTPCM | AUDCLNT_STREAMFLAGS_SRC_DEFAULT_QUALITY,
                                kBufferHns, 0, &wf, nullptr);
        if (SUCCEEDED(hr)) hr = client->SetEventHandle(audioEvent);
        if (SUCCEEDED(hr)) hr = client->GetBufferSize(&bufferFrames);
        if (SUCCEEDED(hr)) hr = client->GetService(IID_PPV_ARGS(&render));
        if (SUCCEEDED(hr)) hr = client->Start();
        if (FAILED(hr)) close();
        return hr;
    };

    HANDLE waits[2] = { m_stopEvent, audioEvent };
    for (;;) {
        if (WaitForSingleObject(m_stopEvent, 0) == WAIT_OBJECT_0)
            break;

        if (h->shareMode == 0) {
            if (client) close();
            setState(kShareOff);
            if (WaitForSingleObject(m_stopEvent, 100) == WAIT_OBJECT_0) break;
            continue;
        }

        const LONG serial = h->shareSerial;
        if (client && serial != openedSerial)
            close();                                  // the user picked another device
        if (!client) {
            if (GetTickCount64() < retryAt) {
                if (WaitForSingleObject(m_stopEvent, 100) == WAIT_OBJECT_0) break;
                continue;
            }
            const HRESULT hr = open();
            openedSerial = serial;
            if (FAILED(hr)) {
                setState(hr == HRESULT_FROM_WIN32(ERROR_NOT_FOUND) ? LONG(kShareNoDevice) : LONG(hr));
                retryAt = GetTickCount64() + 2000;
                continue;
            }
            resampler.Reset();
            gain = 0.f;
        }

        const DWORD w = WaitForMultipleObjects(2, waits, FALSE, 200);
        if (w == WAIT_OBJECT_0)
            break;

        UINT32 padding = 0;
        HRESULT hr = client->GetCurrentPadding(&padding);
        if (FAILED(hr)) { close(); setState(hr); retryAt = GetTickCount64() + 1000; continue; }
        const UINT32 frames = std::min<UINT32>(bufferFrames - padding, kMaxWriteFrames);
        if (frames == 0)
            continue;

        BYTE* data = nullptr;
        hr = render->GetBuffer(frames, &data);
        if (FAILED(hr)) { close(); setState(hr); retryAt = GetTickCount64() + 1000; continue; }

        float* out = reinterpret_cast<float*>(data);
        const bool playing = resampler.Read(h, ring, kOutputRate, out, frames);
        float target = h->shareGain;
        if (!(target >= 0.f)) target = 0.f;           // also catches NaN
        if (target > 4.f) target = 4.f;
        for (UINT32 i = 0; i < frames; ++i) {
            gain += (target - gain) * glide;
            out[i * 2] *= gain;
            out[i * 2 + 1] *= gain;
        }
        render->ReleaseBuffer(frames, 0);

        setState(playing ? kShareRunning : kShareWaiting);
        h->shareBufferedMs = static_cast<float>(resampler.BufferedMs());
    }

    close();
    setState(kShareOff);
    SafeRelease(enumerator);
    if (audioEvent) CloseHandle(audioEvent);
    if (mmcss) AvRevertMmThreadCharacteristics(mmcss);
    if (SUCCEEDED(coInit)) CoUninitialize();
}

} // namespace ampcast
