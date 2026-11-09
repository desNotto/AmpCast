using System;

namespace AmpCast.Audio;

/// <summary>
/// Pulls audio from a ring written by another clock (DAW interface, USB mic)
/// and delivers it at the output device rate.
///
/// Two independent clocks always drift (e.g. 48000.0 vs 48000.3 Hz). Without
/// compensation the ring slowly overflows or runs dry, which is heard as a
/// click every few minutes. The reader keeps the fill level near a target by
/// nudging the resampling ratio by up to ±0.1 % (inaudible), and also performs
/// real sample-rate conversion (44.1 → 48 kHz etc.).
///
/// The resampler is a 16-tap windowed-sinc interpolator that reads straight
/// from the ring by absolute frame position. It is continuous across blocks for
/// any ratio (NAudio's WdlResampler switches its IIR filter between input and
/// output when the ratio crosses 1.0, which is exactly the drift-correction case,
/// and re-allocates its buffer every block). Allocation-free: all buffers are
/// created in the constructor.
/// </summary>
internal sealed class AdaptiveStreamReader
{
    private const double MaxCorrection = 0.001;     // ±1000 ppm
    private const double CorrectionGain = 0.05;     // per second of excess buffering

    private const int Taps = 16;                    // kernel length (8 frames each side)
    private const int Before = Taps / 2 - 1;        // frames needed before the read position
    private const int After = Taps / 2;             // frames needed after it
    private const int Phases = 512;                 // kernel table resolution
    private const int ChunkFrames = 1024;           // output frames converted per pass
    private const double MaxStep = 8.0;             // highest supported source/output ratio

    private readonly int _channels;
    private readonly float[] _kernel = new float[(Phases + 1) * Taps];
    private readonly float[] _window;

    private IFrameSource? _source;
    private double _sourceRate = 48000;
    private double _outputRate = 48000;
    private double _kernelCutoff = -1;
    private double _targetMs = 30;
    private long _readPosition;                     // integer part of the source position
    private double _fraction;                       // fractional part, [0, 1)
    private bool _primed;
    private double _averageFill;
    private int _target;
    private int _sourceBlockFrames;

    public AdaptiveStreamReader(int channels)
    {
        _channels = channels;
        _window = new float[((int)(ChunkFrames * (MaxStep * (1 + MaxCorrection))) + Taps + 4) * channels];
        BuildKernel();
    }

    public double SourceRate => _sourceRate;
    public double CorrectionPpm { get; private set; }
    public double BufferedMs { get; private set; }
    public int Underruns { get; private set; }
    public int Overruns { get; private set; }

    /// <summary>
    /// How many frames the writer delivers at once (the DAW's ASIO buffer, the mic
    /// capture period). The ring fills in bursts of this size, so the reader must keep
    /// at least one whole burst buffered or it runs dry between bursts. Set from the
    /// audio thread.
    /// </summary>
    public int SourceBlockFrames
    {
        get => _sourceBlockFrames;
        set
        {
            if (value == _sourceBlockFrames) return;
            _sourceBlockFrames = Math.Max(0, value);
            _target = 0;   // re-derive the target for the new block size
        }
    }

    public void Configure(IFrameSource? source, double sourceRate, double outputRate, double targetMs)
    {
        _source = source;
        _sourceRate = sourceRate;
        _outputRate = outputRate;
        _targetMs = targetMs;
        Restart();
    }

    public void SetSourceRate(double sourceRate)
    {
        _sourceRate = sourceRate;
        Restart();
    }

    /// <summary>Outputs silence and re-primes (used while the source is not running).</summary>
    public void Silence(float[] destination, int frames)
    {
        Array.Clear(destination, 0, frames * _channels);
        _primed = false;
        BufferedMs = 0;
    }

    /// <summary>Writes exactly <paramref name="frames"/> output frames. Returns false if it produced silence.</summary>
    public bool Read(float[] destination, int frames)
    {
        IFrameSource? source = _source;
        if (source is null)
        {
            Silence(destination, frames);
            return false;
        }

        long write = source.WritePosition;
        long available = write - _readPosition;

        double nominalStep = _sourceRate / _outputRate;
        int blockSource = (int)Math.Ceiling(frames * nominalStep) + Taps;
        // Lowest fill happens right before a writer burst lands: it must still cover
        // our own read, plus margin. Keep the largest target seen so the setpoint does
        // not wobble with the output period.
        int needTarget = Math.Max((int)(_sourceRate * _targetMs / 1000.0),
                                  _sourceBlockFrames + blockSource * 2 + (int)(_sourceRate * 0.003));
        if (needTarget > _target) _target = needTarget;
        int target = _target;

        if (available < 0 || available > target * 3 + blockSource || available > source.CapacityFrames - blockSource * 2)
        {
            // Writer restarted, or we fell far behind: jump to the target latency.
            if (_primed) Overruns++;
            JumpTo(write - target);
            available = target;
        }

        if (!_primed)
        {
            if (available < target)
            {
                Array.Clear(destination, 0, frames * _channels);
                BufferedMs = available * 1000.0 / _sourceRate;
                return false;
            }
            _primed = true;
            JumpTo(write - target);
            available = target;
        }

        // Drift control: more buffered than wanted -> consume slightly faster.
        _averageFill += (available - _averageFill) * 0.05;
        double excessSeconds = (_averageFill - target) / _sourceRate;
        double targetMs = target * 1000.0 / _sourceRate;
        double correction = Math.Clamp(excessSeconds * CorrectionGain * 1000.0 / Math.Max(targetMs, 1.0), -MaxCorrection, MaxCorrection);
        CorrectionPpm = correction * 1e6;
        BufferedMs = _averageFill * 1000.0 / _sourceRate;

        double step = nominalStep * (1.0 + correction);
        int done = 0;
        while (done < frames)
        {
            int chunk = Math.Min(ChunkFrames, frames - done);
            int produced = Convert(source, write, step, destination, done, chunk);
            done += produced;
            if (produced < chunk)
            {
                Array.Clear(destination, done * _channels, (frames - done) * _channels);
                Underruns++;
                _primed = false;   // wait for the target fill before playing again
                break;
            }
        }
        return true;
    }

    /// <summary>
    /// Interpolates up to <paramref name="frames"/> output frames starting at the
    /// current source position. Stops early (returns fewer) when the source runs dry.
    /// </summary>
    private int Convert(IFrameSource source, long write, double step, float[] destination, int offset, int frames)
    {
        // Source frames this chunk touches: [start, end).
        long start = _readPosition - Before;
        double lastPosition = _fraction + (frames - 1) * step;
        long end = _readPosition + (long)lastPosition + After + 1;
        if (end > write)
        {
            // Not enough data for the whole chunk: convert only what is available.
            double room = (write - After - 1 - _readPosition) - _fraction;
            frames = room < 0 ? 0 : Math.Min(frames, (int)(room / step) + 1);
            if (frames == 0) return 0;
            lastPosition = _fraction + (frames - 1) * step;
            end = _readPosition + (long)lastPosition + After + 1;
        }

        int count = (int)(end - start);
        source.Read(start, count, _window, 0);

        int ch = _channels;
        float[] kernel = _kernel;
        float[] window = _window;
        double position = _fraction;
        for (int i = 0; i < frames; i++)
        {
            int whole = (int)position;
            double frac = position - whole;
            double phase = frac * Phases;
            int p = (int)phase;
            float mix = (float)(phase - p);
            int k0 = p * Taps, k1 = k0 + Taps;
            int baseFrame = whole;                 // window frame of tap 0 (= read position - Before + whole)
            int o = (offset + i) * ch;
            for (int c = 0; c < ch; c++)
            {
                float acc = 0f;
                int w = baseFrame * ch + c;
                for (int t = 0; t < Taps; t++, w += ch)
                {
                    float h = kernel[k0 + t] + (kernel[k1 + t] - kernel[k0 + t]) * mix;
                    acc += window[w] * h;
                }
                destination[o + c] = acc;
            }
            position += step;
        }

        long advance = (long)position;
        _readPosition += advance;
        _fraction = position - advance;
        return frames;
    }

    private void JumpTo(long position)
    {
        _readPosition = position;
        _fraction = 0;
        _averageFill = _target;
    }

    private void Restart()
    {
        _primed = false;
        _target = 0;
        _fraction = 0;
        CorrectionPpm = 0;
        BuildKernel();
    }

    /// <summary>
    /// Blackman-windowed sinc table. Cut-off follows the nominal ratio (lower when
    /// downsampling, to prevent aliasing), never the tiny drift correction, so the
    /// table is rebuilt only when the source rate really changes. No allocation.
    /// </summary>
    private void BuildKernel()
    {
        double cutoff = Math.Min(1.0, _outputRate / _sourceRate) * 0.92;
        if (Math.Abs(cutoff - _kernelCutoff) < 1e-9) return;
        _kernelCutoff = cutoff;

        for (int p = 0; p <= Phases; p++)
        {
            double frac = (double)p / Phases;
            double sum = 0;
            for (int t = 0; t < Taps; t++)
            {
                // tap t holds source frame (readPosition - Before + t); distance from the output point:
                double x = (t - Before) - frac;
                double sinc = Math.Abs(x) < 1e-12 ? 1.0 : Math.Sin(Math.PI * cutoff * x) / (Math.PI * cutoff * x);
                double u = x / After;                          // -1..1 across the kernel
                double blackman = Math.Abs(u) >= 1 ? 0 : 0.42 + 0.5 * Math.Cos(Math.PI * u) + 0.08 * Math.Cos(2 * Math.PI * u);
                double h = cutoff * sinc * blackman;
                _kernel[p * Taps + t] = (float)h;
                sum += h;
            }
            for (int t = 0; t < Taps; t++)                     // unity gain at DC for every phase
                _kernel[p * Taps + t] = (float)(_kernel[p * Taps + t] / sum);
        }
    }
}
