using System;
using System.Threading;

namespace AmpCast.Audio;

/// <summary>A source of interleaved float frames with a monotonic write counter.</summary>
internal interface IFrameSource
{
    int Channels { get; }
    int CapacityFrames { get; }

    /// <summary>Total number of frames ever written (monotonic).</summary>
    long WritePosition { get; }

    /// <summary>Copies <paramref name="frames"/> frames starting at absolute frame <paramref name="frame"/>.</summary>
    void Read(long frame, int frames, float[] destination, int destinationOffset);
}

/// <summary>
/// Single-producer / single-consumer lock-free ring of interleaved floats.
/// Allocates once in the constructor; Write/Read never allocate.
/// </summary>
internal sealed class FloatRing : IFrameSource
{
    private readonly float[] _data;
    private readonly int _mask;
    private long _writePosition;

    public FloatRing(int minCapacityFrames, int channels)
    {
        int capacity = 1;
        while (capacity < minCapacityFrames) capacity <<= 1;
        CapacityFrames = capacity;
        Channels = channels;
        _mask = capacity - 1;
        _data = new float[capacity * channels];
    }

    public int Channels { get; }
    public int CapacityFrames { get; }
    public long WritePosition => Volatile.Read(ref _writePosition);

    public void Write(ReadOnlySpan<float> interleaved)
    {
        int frames = interleaved.Length / Channels;
        if (frames == 0) return;
        if (frames > CapacityFrames)
        {
            interleaved = interleaved.Slice((frames - CapacityFrames) * Channels);
            frames = CapacityFrames;
        }

        long position = _writePosition;               // only this thread writes it
        int index = (int)(position & _mask);
        int first = Math.Min(frames, CapacityFrames - index);
        interleaved.Slice(0, first * Channels).CopyTo(_data.AsSpan(index * Channels));
        if (first < frames)
            interleaved.Slice(first * Channels).CopyTo(_data.AsSpan(0));

        Volatile.Write(ref _writePosition, position + frames);   // publish after the data
    }

    public void Read(long frame, int frames, float[] destination, int destinationOffset)
    {
        int index = (int)(frame & _mask);
        int first = Math.Min(frames, CapacityFrames - index);
        Array.Copy(_data, index * Channels, destination, destinationOffset, first * Channels);
        if (first < frames)
            Array.Copy(_data, 0, destination, destinationOffset + first * Channels, (frames - first) * Channels);
    }
}
