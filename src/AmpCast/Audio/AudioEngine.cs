using System;
using System.Runtime.InteropServices;
using System.Threading;
using NAudio.CoreAudioApi;
using NAudio.Wave;

namespace AmpCast.Audio;

/// <summary>
/// AmpCast audio engine (the Discord side).
///
/// Signal flow:
///   Guitar → interface ─ASIO→ [AmpCast ASIO driver, inside the DAW] → DAW → headphones
///                                         │ (copy of DAW master, shared memory)
///                                         ▼
///   Microphone ─WASAPI→ ring ─┐   SharedDawBus ─┐
///                             ▼                 ▼
///                   AdaptiveStreamReader   AdaptiveStreamReader   (resample + drift)
///                             └──── mix · gains · broadcast mute · limiter ────┐
///                                                                              ▼
///                                              WASAPI render → virtual cable → Discord
///
/// The latency-critical guitar monitoring never passes through .NET, so a GC
/// pause can't click your headphones. The Discord path is still written
/// allocation-free: buffers are allocated in Start(), never in callbacks.
/// </summary>
public sealed class AudioEngine : IDisposable
{
    public const int RequiredSampleRate = 48000;
    private const int MaxBlockFrames = 2048;
    private const double DawTargetMs = 30;
    private const double MicTargetMs = 30;
    private static readonly Guid FloatSubFormat = new("00000003-0000-0010-8000-00aa00389b71");
    private static readonly Guid PcmSubFormat = new("00000001-0000-0010-8000-00aa00389b71");

    private readonly SharedDawBus _bus = new();
    private readonly AdaptiveStreamReader _dawReader = new(2);
    private readonly AdaptiveStreamReader _micReader = new(1);
    private readonly float[] _dawBlock = new float[MaxBlockFrames * 2];
    private readonly float[] _micBlock = new float[MaxBlockFrames];
    private readonly object _sync = new();

    private MMDevice? _micDevice;
    private MMDevice? _outDevice;
    private WasapiCapture? _capture;
    private WasapiOut? _output;
    private MixProvider? _mixProvider;
    private FloatRing? _micRing;
    private float[] _captureScratch = Array.Empty<float>();
    private WaveFormat? _captureFormat;
    private bool _captureIsFloat;

    // Written by the UI thread, read by the audio thread.
    private volatile float _micGainTarget = 1f;
    private volatile float _dawGainTarget = 1f;
    private volatile bool _broadcast;

    // Render-thread state.
    private float _micGain, _dawGain, _castGain;
    private float _smoothing;
    private int _outChannels = 2;

    public MeterBank Meters { get; } = new();

    public int MicSampleRate { get; private set; }
    public int OutputSampleRate { get; private set; }
    public string MicName { get; private set; } = string.Empty;
    public string OutputName { get; private set; } = string.Empty;
    public bool IsRunning => _output is not null;
    public string? LastError { get; private set; }

    /// <summary>Raised (on a worker thread) when a device disappears or fails.</summary>
    public event Action<string>? DeviceFailed;

    public float MicGain { get => _micGainTarget; set => _micGainTarget = Math.Max(0f, value); }
    public float DawGain { get => _dawGainTarget; set => _dawGainTarget = Math.Max(0f, value); }
    public bool Broadcast { get => _broadcast; set => _broadcast = value; }

    public bool DawConnected => _bus.IsAlive;
    public string DawHostName => _bus.HostName;
    public double DawSampleRate => _bus.SampleRate;
    public int DawBufferSize => _bus.BufferSize;
    public double DawBufferedMs => _dawReader.BufferedMs;
    public double DawDriftPpm => _dawReader.CorrectionPpm;
    public int Dropouts => _dawReader.Underruns + _micReader.Underruns;

    /// <summary>(Re)starts capture and render. Safe to call again after a device change.</summary>
    public void Start(string? micDeviceId, string? outputDeviceId)
    {
        lock (_sync)
        {
            StopCore();
            LastError = null;
            using var enumerator = new MMDeviceEnumerator();

            try
            {
                _outDevice = FindDevice(enumerator, outputDeviceId, DataFlow.Render);
                if (_outDevice is null)
                {
                    LastError = "Не найдено устройство вывода для Discord (виртуальный кабель).";
                    return;
                }
                StartOutput(_outDevice);
            }
            catch (Exception ex)
            {
                LastError = "Выход в Discord: " + ex.Message;
                StopCore();
                return;
            }

            try
            {
                _micDevice = FindDevice(enumerator, micDeviceId, DataFlow.Capture);
                if (_micDevice is not null)
                    StartCapture(_micDevice);
                else
                    _micReader.Configure(null, RequiredSampleRate, OutputSampleRate, MicTargetMs);
            }
            catch (Exception ex)
            {
                // Keep broadcasting the DAW even if the microphone fails.
                LastError = "Микрофон: " + ex.Message;
                DisposeCapture();
                _micReader.Configure(null, RequiredSampleRate, OutputSampleRate, MicTargetMs);
            }

            _output!.Play();
        }
    }

    public void Stop()
    {
        lock (_sync) StopCore();
    }

    private static MMDevice? FindDevice(MMDeviceEnumerator enumerator, string? id, DataFlow flow)
    {
        if (string.IsNullOrEmpty(id)) return null;
        foreach (var device in enumerator.EnumerateAudioEndPoints(flow, DeviceState.Active))
        {
            if (device.ID == id) return device;
            device.Dispose();
        }
        return null;
    }

    // ------------------------------------------------------------------ output

    private void StartOutput(MMDevice device)
    {
        WaveFormat mix = device.AudioClient.MixFormat;
        WaveFormat format = IsFloat32(mix) ? mix : WaveFormat.CreateIeeeFloatWaveFormat(mix.SampleRate, Math.Min(2, mix.Channels));

        OutputSampleRate = format.SampleRate;
        OutputName = device.FriendlyName;
        _outChannels = format.Channels;
        _smoothing = 1f - MathF.Exp(-1f / (0.015f * OutputSampleRate));   // ~15 ms gain glide
        _micGain = _micGainTarget;
        _dawGain = _dawGainTarget;
        _castGain = _broadcast ? 1f : 0f;

        _dawReader.Configure(_bus, _bus.SampleRate > 8000 ? _bus.SampleRate : RequiredSampleRate, OutputSampleRate, DawTargetMs);

        _mixProvider = new MixProvider(this, format);
        _output = new WasapiOut(device, AudioClientShareMode.Shared, true, 30);
        _output.PlaybackStopped += OnPlaybackStopped;
        _output.Init(_mixProvider);
    }

    private void OnPlaybackStopped(object? sender, StoppedEventArgs e)
    {
        if (e.Exception is not null)
            DeviceFailed?.Invoke("Выход в Discord остановлен: " + e.Exception.Message);
    }

    // ------------------------------------------------------------------ microphone

    private void StartCapture(MMDevice device)
    {
        _capture = new WasapiCapture(device, true, 20) { ShareMode = AudioClientShareMode.Shared };
        _captureFormat = _capture.WaveFormat;
        _captureIsFloat = IsFloat32(_captureFormat);
        if (!_captureIsFloat && !IsPcm(_captureFormat))
            throw new NotSupportedException("неподдерживаемый формат " + _captureFormat);

        MicSampleRate = _captureFormat.SampleRate;
        MicName = device.FriendlyName;
        _micRing = new FloatRing(MicSampleRate, 1);                 // 1 s of mono audio
        _captureScratch = new float[MicSampleRate / 2];              // 0.5 s per callback max
        _micReader.Configure(_micRing, MicSampleRate, OutputSampleRate, MicTargetMs);

        _capture.DataAvailable += OnMicData;
        _capture.RecordingStopped += OnRecordingStopped;
        _capture.StartRecording();
    }

    private void OnMicData(object? sender, WaveInEventArgs e)
    {
        Mmcss.EnsureProAudio();
        WaveFormat? format = _captureFormat;
        FloatRing? ring = _micRing;
        if (format is null || ring is null) return;

        int channels = format.Channels;
        int bytesPerSample = format.BitsPerSample / 8;
        int frames = e.BytesRecorded / format.BlockAlign;
        float[] scratch = _captureScratch;
        int frame = 0;

        while (frame < frames)
        {
            int count = Math.Min(frames - frame, scratch.Length);
            ReadOnlySpan<byte> bytes = e.Buffer.AsSpan(frame * format.BlockAlign, count * format.BlockAlign);

            if (_captureIsFloat)
            {
                ReadOnlySpan<float> s = MemoryMarshal.Cast<byte, float>(bytes);
                if (channels == 1)
                    s.CopyTo(scratch);
                else
                {
                    float scale = 1f / channels;
                    for (int i = 0, k = 0; i < count; i++)
                    {
                        float sum = 0;
                        for (int c = 0; c < channels; c++) sum += s[k++];
                        scratch[i] = sum * scale;
                    }
                }
            }
            else
            {
                ConvertPcmToMono(bytes, count, channels, bytesPerSample, scratch);
            }

            ring.Write(scratch.AsSpan(0, count));
            frame += count;
        }
    }

    private static void ConvertPcmToMono(ReadOnlySpan<byte> bytes, int frames, int channels, int bytesPerSample, float[] dst)
    {
        float scale = 1f / channels;
        int p = 0;
        for (int i = 0; i < frames; i++)
        {
            float sum = 0;
            for (int c = 0; c < channels; c++, p += bytesPerSample)
            {
                sum += bytesPerSample switch
                {
                    2 => (short)(bytes[p] | bytes[p + 1] << 8) / 32768f,
                    3 => (bytes[p] << 8 | bytes[p + 1] << 16 | bytes[p + 2] << 24) / 2147483648f,
                    4 => (bytes[p] | bytes[p + 1] << 8 | bytes[p + 2] << 16 | bytes[p + 3] << 24) / 2147483648f,
                    _ => 0f,
                };
            }
            dst[i] = sum * scale;
        }
    }

    private void OnRecordingStopped(object? sender, StoppedEventArgs e)
    {
        if (e.Exception is not null)
            DeviceFailed?.Invoke("Микрофон остановлен: " + e.Exception.Message);
    }

    // ------------------------------------------------------------------ render (audio thread)

    private void Render(Span<float> output, int frames)
    {
        // DAW: follow the DAW sample rate, play silence while no DAW is streaming.
        double dawRate = _bus.SampleRate;
        if (_bus.IsAlive)
        {
            if (dawRate > 8000 && Math.Abs(dawRate - _dawReader.SourceRate) > 0.5)
                _dawReader.SetSourceRate(dawRate);
            _dawReader.Read(_dawBlock, frames);
        }
        else
        {
            _dawReader.Silence(_dawBlock, frames);
        }

        if (_micRing is not null) _micReader.Read(_micBlock, frames);
        else Array.Clear(_micBlock, 0, frames);

        float k = _smoothing;
        float micTarget = _micGainTarget, dawTarget = _dawGainTarget, castTarget = _broadcast ? 1f : 0f;
        float micGain = _micGain, dawGain = _dawGain, castGain = _castGain;
        float peakDawL = 0, peakDawR = 0, peakMic = 0, peakOutL = 0, peakOutR = 0;
        bool clipped = false;
        int channels = _outChannels;
        float[] daw = _dawBlock, mic = _micBlock;

        for (int i = 0, o = 0; i < frames; i++, o += channels)
        {
            micGain += (micTarget - micGain) * k;
            dawGain += (dawTarget - dawGain) * k;
            castGain += (castTarget - castGain) * k;

            float dl = daw[2 * i] * dawGain;
            float dr = daw[2 * i + 1] * dawGain;
            float m = mic[i] * micGain;

            float l = (dl + m) * castGain;
            float r = (dr + m) * castGain;
            if (l > 1f || l < -1f || r > 1f || r < -1f) clipped = true;
            l = SoftClip(l);
            r = SoftClip(r);

            peakDawL = MathF.Max(peakDawL, MathF.Abs(dl));
            peakDawR = MathF.Max(peakDawR, MathF.Abs(dr));
            peakMic = MathF.Max(peakMic, MathF.Abs(m));
            peakOutL = MathF.Max(peakOutL, MathF.Abs(l));
            peakOutR = MathF.Max(peakOutR, MathF.Abs(r));

            if (channels == 1)
            {
                output[o] = 0.5f * (l + r);
            }
            else
            {
                output[o] = l;
                output[o + 1] = r;
                for (int c = 2; c < channels; c++) output[o + c] = 0f;
            }
        }

        _micGain = micGain;
        _dawGain = dawGain;
        _castGain = castGain;

        Meters.Push(ref Meters.DawL, peakDawL);
        Meters.Push(ref Meters.DawR, peakDawR);
        Meters.Push(ref Meters.Mic, peakMic);
        Meters.Push(ref Meters.OutL, peakOutL);
        Meters.Push(ref Meters.OutR, peakOutR);
        if (clipped) Volatile.Write(ref Meters.Clip, 1);
    }

    /// <summary>Transparent below -1 dBFS, smooth saturation above: Discord never gets hard clipping.</summary>
    private static float SoftClip(float x)
    {
        float a = MathF.Abs(x);
        if (a <= 0.9f) return x;
        float y = 0.9f + 0.1f * MathF.Tanh((a - 0.9f) * 10f);
        return x < 0 ? -y : y;
    }

    private sealed class MixProvider : IWaveProvider
    {
        private readonly AudioEngine _engine;

        public MixProvider(AudioEngine engine, WaveFormat format)
        {
            _engine = engine;
            WaveFormat = format;
        }

        public WaveFormat WaveFormat { get; }

        public int Read(byte[] buffer, int offset, int count)
        {
            Mmcss.EnsureProAudio();
            Span<float> samples = MemoryMarshal.Cast<byte, float>(buffer.AsSpan(offset, count));
            int channels = WaveFormat.Channels;
            int totalFrames = samples.Length / channels;
            int done = 0;
            while (done < totalFrames)
            {
                int n = Math.Min(MaxBlockFrames, totalFrames - done);
                _engine.Render(samples.Slice(done * channels, n * channels), n);
                done += n;
            }
            return count;
        }
    }

    // ------------------------------------------------------------------ helpers

    private static bool IsFloat32(WaveFormat f) =>
        f.BitsPerSample == 32 &&
        (f.Encoding == WaveFormatEncoding.IeeeFloat || (f is WaveFormatExtensible x && x.SubFormat == FloatSubFormat));

    private static bool IsPcm(WaveFormat f) =>
        f.BitsPerSample is 16 or 24 or 32 &&
        (f.Encoding == WaveFormatEncoding.Pcm || (f is WaveFormatExtensible x && x.SubFormat == PcmSubFormat));

    private void StopCore()
    {
        if (_output is not null)
        {
            _output.PlaybackStopped -= OnPlaybackStopped;
            try { _output.Stop(); } catch { /* device already gone */ }
            _output.Dispose();
            _output = null;
        }
        DisposeCapture();
        _mixProvider = null;
        _outDevice?.Dispose();
        _outDevice = null;
        MicName = string.Empty;
        OutputName = string.Empty;
        MicSampleRate = 0;
        OutputSampleRate = 0;
        Meters.Reset();
    }

    private void DisposeCapture()
    {
        if (_capture is not null)
        {
            _capture.DataAvailable -= OnMicData;
            _capture.RecordingStopped -= OnRecordingStopped;
            try { _capture.StopRecording(); } catch { /* device already gone */ }
            _capture.Dispose();
            _capture = null;
        }
        _micDevice?.Dispose();
        _micDevice = null;
        _micRing = null;
        _captureFormat = null;
    }

    public void Dispose()
    {
        Stop();
        _bus.Dispose();
    }
}

/// <summary>Peak meters shared between the audio thread (Push) and the UI (Take).</summary>
public sealed class MeterBank
{
    public float DawL, DawR, Mic, OutL, OutR;
    public int Clip;

    internal void Push(ref float slot, float value)
    {
        // Benign race: the UI only needs the max since its last read.
        if (value > Volatile.Read(ref slot)) Volatile.Write(ref slot, value);
    }

    public static float Take(ref float slot) => Interlocked.Exchange(ref slot, 0f);

    internal void Reset()
    {
        DawL = DawR = Mic = OutL = OutR = 0;
        Clip = 0;
    }
}

/// <summary>Registers audio threads with the Windows "Pro Audio" MMCSS class (real-time priority).</summary>
internal static class Mmcss
{
    [ThreadStatic] private static bool t_registered;

    [DllImport("avrt.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr AvSetMmThreadCharacteristicsW(string taskName, ref uint taskIndex);

    public static void EnsureProAudio()
    {
        if (t_registered) return;
        t_registered = true;
        try
        {
            uint index = 0;
            AvSetMmThreadCharacteristicsW("Pro Audio", ref index);
        }
        catch
        {
            // MMCSS unavailable: keep the default priority.
        }
    }
}
