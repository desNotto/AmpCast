// AmpCast ASIO — a pass-through ("wrapper") ASIO driver.
//
// The DAW opens "AmpCast ASIO". The driver opens the user's real ASIO
// interface inside the same process and hands the DAW the *hardware* buffer
// pointers, so guitar input -> DAW -> headphones runs with exactly the
// latency of the real interface (no extra copy, no extra buffer).
// After every DAW callback the driver copies the DAW master (outputs 1/2,
// plus the optional "AmpCast Discord L/R" outputs) into a shared-memory ring
// that AmpCast.exe mixes with the microphone and sends to Discord.
#pragma once

#include "asio_abi.h"
#include "SharedBus.h"

#include <atomic>
#include <vector>

// {7C3B1A52-4E9D-4F7B-9A1E-AC0A5CA57001}
static const CLSID CLSID_AmpCastAsio =
    { 0x7c3b1a52, 0x4e9d, 0x4f7b, { 0x9a, 0x1e, 0xac, 0x0a, 0x5c, 0xa5, 0x70, 0x01 } };

constexpr char    kDriverNameA[]  = "AmpCast ASIO";
constexpr wchar_t kDriverNameW[]  = L"AmpCast ASIO";
constexpr wchar_t kConfigKey[]    = L"Software\\desNotto\\AmpCast";
constexpr long    kVirtualOutputs = 2;   // "AmpCast Discord L/R": goes to Discord only

extern LONG g_objectCount;

class AmpCastAsio final : public IASIO {
public:
    AmpCastAsio();
    ~AmpCastAsio();

    // IUnknown
    HRESULT STDMETHODCALLTYPE QueryInterface(REFIID riid, void** ppv) override;
    ULONG   STDMETHODCALLTYPE AddRef() override;
    ULONG   STDMETHODCALLTYPE Release() override;

    // IASIO
    ASIOBool  init(void* sysHandle) override;
    void      getDriverName(char* name) override;
    long      getDriverVersion() override;
    void      getErrorMessage(char* string) override;
    ASIOError start() override;
    ASIOError stop() override;
    ASIOError getChannels(long* numInputChannels, long* numOutputChannels) override;
    ASIOError getLatencies(long* inputLatency, long* outputLatency) override;
    ASIOError getBufferSize(long* minSize, long* maxSize, long* preferredSize, long* granularity) override;
    ASIOError canSampleRate(ASIOSampleRate sampleRate) override;
    ASIOError getSampleRate(ASIOSampleRate* sampleRate) override;
    ASIOError setSampleRate(ASIOSampleRate sampleRate) override;
    ASIOError getClockSources(ASIOClockSource* clocks, long* numSources) override;
    ASIOError setClockSource(long reference) override;
    ASIOError getSamplePosition(ASIOSamples* sPos, ASIOTimeStamp* tStamp) override;
    ASIOError getChannelInfo(ASIOChannelInfo* info) override;
    ASIOError createBuffers(ASIOBufferInfo* bufferInfos, long numChannels, long bufferSize, ASIOCallbacks* callbacks) override;
    ASIOError disposeBuffers() override;
    ASIOError controlPanel() override;
    ASIOError future(long selector, void* opt) override;
    ASIOError outputReady() override;

private:
    struct TapSource {
        void*          buffers[2];
        ASIOSampleType type;
        int            busChannel;   // 0 = left, 1 = right
    };

    // Callbacks given to the hardware driver. They forward to the DAW.
    static void      __cdecl OnBufferSwitch(long index, ASIOBool directProcess);
    static void      __cdecl OnSampleRateDidChange(ASIOSampleRate rate);
    static long      __cdecl OnAsioMessage(long selector, long value, void* message, double* opt);
    static ASIOTime* __cdecl OnBufferSwitchTimeInfo(ASIOTime* params, long index, ASIOBool directProcess);

    static DWORD WINAPI WatchThreadProc(void* param);

    void LoadConfig();
    bool ResolveHardwareDriver(CLSID& clsid, wchar_t* name, size_t nameChars);
    bool IsSampleRateAllowed(ASIOSampleRate rate) const;
    void Tap(long index);
    void RequestReset();
    void StartWatch();
    void StopWatch();
    void SetError(const char* text);

    static std::atomic<AmpCastAsio*> s_active;

    LONG           m_refCount = 1;
    IASIO*         m_hw = nullptr;
    char           m_error[128] = {};
    wchar_t        m_deviceName[128] = {};
    bool           m_lock48k = true;
    long           m_tapChannel = 0;

    long           m_hwInputs = 0;
    long           m_hwOutputs = 0;
    ASIOSampleType m_virtualType = ASIOSTFloat32LSB;

    ASIOCallbacks* m_host = nullptr;
    ASIOCallbacks  m_hwCallbacks = {};
    bool           m_buffersCreated = false;
    long           m_bufferSize = 0;
    bool           m_virtualActive[kVirtualOutputs] = {};

    std::vector<unsigned char> m_virtualMemory;   // DAW-only outputs "AmpCast Discord L/R"
    std::vector<float>         m_scratch;         // interleaved stereo, one buffer long
    TapSource                  m_taps[8] = {};
    int                        m_tapCount = 0;

    ampcast::SharedBus m_bus;
    bool               m_busOwner = false;

    HANDLE m_resetEvent  = nullptr;
    HANDLE m_stopEvent   = nullptr;
    HANDLE m_watchThread = nullptr;
};
