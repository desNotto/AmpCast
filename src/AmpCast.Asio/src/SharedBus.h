// AmpCast — lock-free shared-memory bus: DAW (inside the driver) -> AmpCast app.
//
// One writer (the ASIO driver running inside the DAW process), one reader
// (AmpCast.exe). The layout MUST match src/AmpCast/Audio/SharedDawBus.cs.
#pragma once

#include <windows.h>
#include <stddef.h>
#include <stdint.h>

namespace ampcast {

constexpr wchar_t  kBusName[]          = L"Local\\AmpCast_DawBus";
constexpr wchar_t  kResetEventName[]   = L"Local\\AmpCast_DriverReset";
constexpr uint32_t kBusMagic           = 0x31424341;   // "ACB1"
constexpr uint32_t kBusVersion         = 1;
constexpr uint32_t kBusChannels        = 2;            // interleaved stereo float32
constexpr uint32_t kBusCapacityFrames  = 32768;        // power of two, ~0.68 s @ 48 kHz
constexpr uint32_t kBusHeaderSize      = 256;
constexpr size_t   kBusTotalSize       = kBusHeaderSize + size_t(kBusCapacityFrames) * kBusChannels * sizeof(float);

struct BusHeader {
    uint32_t        magic;           //   0
    uint32_t        version;         //   4
    uint32_t        channels;        //   8
    uint32_t        capacityFrames;  //  12
    double          sampleRate;      //  16
    int32_t         bufferSize;      //  24  ASIO buffer size used by the DAW
    volatile LONG   ownerPid;        //  28  process that currently writes
    volatile LONG64 writePos;        //  32  total frames written (monotonic)
    volatile LONG64 heartbeatMs;     //  40  GetTickCount64() of the last write
    volatile LONG   streaming;       //  48  1 between start() and stop()
    uint32_t        reserved0;       //  52
    wchar_t         hostName[64];    //  56  DAW executable name
    // Stereo screen-share output (driver plays the DAW mix as ordinary Windows
    // audio from inside the DAW process, so Discord's "share window" hears it).
    volatile LONG   shareMode;       // 184  app -> driver: 1 = on
    volatile float  shareGain;       // 188  app -> driver: linear gain (0 = muted)
    volatile LONG   shareSerial;     // 192  app -> driver: bumped when the device changes
    volatile LONG   shareState;      // 196  driver -> app: ShareState / negative HRESULT
    volatile float  shareBufferedMs; // 200  driver -> app: diagnostics
    uint8_t         reserved1[52];   // 204
};
static_assert(sizeof(BusHeader) == kBusHeaderSize, "BusHeader must be 256 bytes");
static_assert(offsetof(BusHeader, writePos) == 32, "layout mismatch");
static_assert(offsetof(BusHeader, hostName) == 56, "layout mismatch");
static_assert(offsetof(BusHeader, shareMode) == 184, "layout mismatch");
static_assert(offsetof(BusHeader, shareBufferedMs) == 200, "layout mismatch");

// BusHeader::shareState values (negative = HRESULT of the failure).
enum ShareState : LONG { kShareOff = 0, kShareRunning = 1, kShareNoDevice = 2, kShareWaiting = 3 };

class SharedBus {
public:
    SharedBus() = default;
    ~SharedBus() { Close(); }
    SharedBus(const SharedBus&) = delete;
    SharedBus& operator=(const SharedBus&) = delete;

    bool Open();
    void Close();
    bool IsOpen() const { return m_header != nullptr; }

    // Only one DAW process may feed the bus at a time.
    bool TryAcquire();
    void Release();

    void SetFormat(double sampleRate, long bufferSize);
    void SetStreaming(bool streaming);

    // Real-time safe: two memcpy + one interlocked store. No allocations, no locks.
    void Write(const float* interleavedStereo, uint32_t frames);

    BusHeader*   Header() const { return m_header; }
    const float* Data() const { return m_data; }

private:
    HANDLE     m_mapping = nullptr;
    BusHeader* m_header  = nullptr;
    float*     m_data    = nullptr;
    bool       m_owner   = false;
};

} // namespace ampcast
