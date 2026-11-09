// AmpCast — stereo output for Discord screen sharing.
//
// Discord's voice channel downmixes every microphone to mono, but the audio of
// a *shared window* stays stereo. Discord captures it per process (the shared
// process and its children), whatever device that process plays to. The DAW
// itself talks to the interface through ASIO, which Discord cannot capture.
//
// So, when the user enables stereo mode in AmpCast, the driver — which already
// runs inside the DAW process — plays the DAW mix (the same tap that feeds
// AmpCast.exe) as ordinary WASAPI audio to a device nobody listens to (the
// virtual cable). Sharing the DAW window in Discord then carries the DAW in
// stereo. It runs on its own thread, reading the shared ring; the DAW's audio
// callback is untouched.
#pragma once

#include "SharedBus.h"

#include <vector>

namespace ampcast {

// Windowed-sinc resampler with clock-drift control that reads the bus ring by
// absolute frame position. C++ twin of src/AmpCast/Audio/AdaptiveStreamReader.cs.
class BusResampler {
public:
    BusResampler();

    void Reset();

    // Writes exactly `frames` interleaved stereo frames at `outRate`.
    // Returns false (and writes silence) while there is nothing to play.
    bool Read(BusHeader* header, const float* ring, double outRate, float* dst, uint32_t frames);

    double BufferedMs() const { return m_bufferedMs; }

private:
    static constexpr int    kTaps = 16;
    static constexpr int    kBefore = kTaps / 2 - 1;
    static constexpr int    kAfter = kTaps / 2;
    static constexpr int    kPhases = 512;
    static constexpr int    kChunk = 1024;
    static constexpr double kMaxStep = 8.0;
    static constexpr double kMaxCorrection = 0.001;
    static constexpr double kCorrectionGain = 0.05;
    static constexpr double kTargetMs = 30.0;

    void     BuildKernel(double cutoff);
    void     JumpTo(LONG64 position);
    uint32_t Convert(const float* ring, LONG64 write, double step, float* dst, uint32_t frames);

    std::vector<float> m_kernel;
    std::vector<float> m_window;
    double m_sourceRate = 0;
    double m_kernelCutoff = -1;
    LONG64 m_readPos = 0;
    double m_fraction = 0;
    bool   m_primed = false;
    double m_averageFill = 0;
    long   m_target = 0;
    long   m_sourceBlock = 0;
    double m_bufferedMs = 0;
};

class ShareRenderer {
public:
    ShareRenderer() = default;
    ~ShareRenderer() { Stop(); }
    ShareRenderer(const ShareRenderer&) = delete;
    ShareRenderer& operator=(const ShareRenderer&) = delete;

    void Start(SharedBus* bus);   // no-op if already running
    void Stop();

private:
    static DWORD WINAPI ThreadProc(void* param);
    void Run();

    SharedBus* m_bus = nullptr;
    HANDLE     m_thread = nullptr;
    HANDLE     m_stopEvent = nullptr;
};

} // namespace ampcast
