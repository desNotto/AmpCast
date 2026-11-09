using System;
using NAudio.Dsp;

namespace AmpCast.Audio;

/// <summary>
/// Pulls audio from a ring written by another clock (DAW interface, USB mic)
/// and delivers it at the output device rate.
///
/// Two independent clocks always drift (e.g. 48000.0 vs 48000.3 Hz). Without
/// compensation the ring slowly overflows or runs dry, which is heard as a
/// click every few minutes. The reader keeps the fill level near a target by
/// nudging the resampling ratio by up to ±0.1 % (inaudible), and also performs
/// real sample-rate conversion (44.1 → 48 kHz etc.) with the same resampler.
///
/// Allocation-free after the first few blocks (WdlResampler grows its internal
/// buffer once, then reuses it).
/// </summary>
internal sealed class AdaptiveStreamReader
{
    private const double MaxCorrection = 0.001;     // ±1000 ppm
    private const double CorrectionGain = 0.05;     // per second of excess buffering

    private readonly WdlResampler _resampler = new();
    private readonly int _channels;

    private IFrameSource? _source;
    private double _sourceRate = 48000;
    private double _outputRate = 48000;
    private double _targetMs = 30;
    private long _readPosition;
    private bool _primed;
    private double _averageFill;

    public AdaptiveStreamReader(int channels)
    {
        _channels = channels;
        _resampler.SetMode(true, 2, false);   // linear interpolation + 2 IIR anti-alias stages
        _resampler.SetFilterParms();
        _resampler.SetFeedMode(false);        // output driven
    }

    public double SourceRate => _sourceRate;
    public double CorrectionPpm { get; private set; }
    public double BufferedMs { get; private set; }
    public int Underruns { get; private set; }
    public int Overruns { get; private set; }

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

        int blockSource = (int)Math.Ceiling(frames * _sourceRate / _outputRate) + 4;
        int target = Math.Max((int)(_sourceRate * _targetMs / 1000.0), blockSource * 2 + 64);

        if (available < 0 || available > target * 3 + blockSource || available > source.CapacityFrames - blockSource * 2)
        {
            // Writer restarted, or we fell far behind: jump to the target latency.
            if (_primed) Overruns++;
            _readPosition = write - target;
            available = target;
            _averageFill = target;
            _resampler.Reset();
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
            _readPosition = write - target;
            available = target;
            _averageFill = target;
            _resampler.Reset();
        }

        // Drift control: more buffered than wanted -> consume slightly faster.
        _averageFill += (available - _averageFill) * 0.05;
        double excessSeconds = (_averageFill - target) / _sourceRate;
        double correction = Math.Clamp(excessSeconds * CorrectionGain * 1000.0 / Math.Max(_targetMs, 1.0), -MaxCorrection, MaxCorrection);
        CorrectionPpm = correction * 1e6;
        BufferedMs = _averageFill * 1000.0 / _sourceRate;

        _resampler.SetRates(_sourceRate * (1.0 + correction), _outputRate);
        int needed = _resampler.ResamplePrepare(frames, _channels, out float[] input, out int inputOffset);

        int got = (int)Math.Min(needed, Math.Max(0, write - _readPosition));
        if (got > 0)
        {
            source.Read(_readPosition, got, input, inputOffset);
            _readPosition += got;
        }
        if (got < needed)
        {
            Array.Clear(input, inputOffset + got * _channels, (needed - got) * _channels);
            Underruns++;
            _primed = false;   // wait for the target fill before playing again
        }

        int produced = _resampler.ResampleOut(destination, 0, needed, frames, _channels);
        if (produced < frames)
            Array.Clear(destination, produced * _channels, (frames - produced) * _channels);
        return true;
    }

    private void Restart()
    {
        _primed = false;
        _resampler.Reset();
        _resampler.SetRates(_sourceRate, _outputRate);
        CorrectionPpm = 0;
    }
}
