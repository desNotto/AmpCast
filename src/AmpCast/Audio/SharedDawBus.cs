using System;
using System.IO.MemoryMappedFiles;
using System.Runtime.CompilerServices;
using System.Threading;

namespace AmpCast.Audio;

/// <summary>
/// Reader side of the shared-memory ring written by the AmpCast ASIO driver
/// inside the DAW process. Layout must match src/AmpCast.Asio/src/SharedBus.h.
/// </summary>
internal sealed unsafe class SharedDawBus : IFrameSource, IDisposable
{
    public const string MapName = @"Local\AmpCast_DawBus";
    public const string ResetEventName = @"Local\AmpCast_DriverReset";

    private const uint Magic = 0x31424341;          // "ACB1"
    private const uint Version = 1;
    private const int HeaderSize = 256;
    private const int BusChannels = 2;
    private const int BusCapacityFrames = 32768;
    private const long TotalSize = HeaderSize + (long)BusCapacityFrames * BusChannels * sizeof(float);

    // Header offsets
    private const int OffMagic = 0, OffVersion = 4, OffChannels = 8, OffCapacity = 12, OffSampleRate = 16,
                      OffBufferSize = 24, OffOwnerPid = 28, OffWritePos = 32, OffHeartbeat = 40,
                      OffStreaming = 48, OffHostName = 56;

    private readonly MemoryMappedFile _file;
    private readonly MemoryMappedViewAccessor _view;
    private readonly byte* _base;
    private readonly float* _data;
    private bool _disposed;

    public SharedDawBus()
    {
        _file = MemoryMappedFile.CreateOrOpen(MapName, TotalSize, MemoryMappedFileAccess.ReadWrite);
        _view = _file.CreateViewAccessor(0, TotalSize, MemoryMappedFileAccess.ReadWrite);
        byte* p = null;
        _view.SafeMemoryMappedViewHandle.AcquirePointer(ref p);
        _base = p + _view.PointerOffset;
        _data = (float*)(_base + HeaderSize);
    }

    public int Channels => BusChannels;
    public int CapacityFrames => BusCapacityFrames;

    public bool IsValid =>
        Volatile.Read(ref U32(OffMagic)) == Magic &&
        U32(OffVersion) == Version &&
        U32(OffChannels) == BusChannels &&
        U32(OffCapacity) == BusCapacityFrames;

    public long WritePosition => Volatile.Read(ref I64(OffWritePos));
    public double SampleRate => Volatile.Read(ref Unsafe.AsRef<double>(_base + OffSampleRate));
    public int BufferSize => Volatile.Read(ref Unsafe.AsRef<int>(_base + OffBufferSize));
    public int OwnerPid => Volatile.Read(ref Unsafe.AsRef<int>(_base + OffOwnerPid));
    public bool Streaming => Volatile.Read(ref Unsafe.AsRef<int>(_base + OffStreaming)) != 0;
    public long HeartbeatMs => Volatile.Read(ref I64(OffHeartbeat));

    /// <summary>True while a DAW is actively pushing audio through AmpCast ASIO.</summary>
    public bool IsAlive => IsValid && Streaming && OwnerPid != 0 && Environment.TickCount64 - HeartbeatMs < 400;

    /// <summary>DAW executable name. Allocates a string — UI thread only.</summary>
    public string HostName => IsValid ? new string((char*)(_base + OffHostName)) : string.Empty;

    public void Read(long frame, int frames, float[] destination, int destinationOffset)
    {
        const int mask = BusCapacityFrames - 1;
        int index = (int)(frame & mask);
        int first = Math.Min(frames, BusCapacityFrames - index);
        new ReadOnlySpan<float>(_data + index * BusChannels, first * BusChannels)
            .CopyTo(destination.AsSpan(destinationOffset));
        if (first < frames)
            new ReadOnlySpan<float>(_data, (frames - first) * BusChannels)
                .CopyTo(destination.AsSpan(destinationOffset + first * BusChannels));
    }

    /// <summary>Asks a running DAW to re-open AmpCast ASIO (applies a new interface / 48 kHz lock).</summary>
    public static void SignalDriverReset()
    {
        try
        {
            using var ev = new EventWaitHandle(false, EventResetMode.AutoReset, ResetEventName);
            ev.Set();
        }
        catch
        {
            // No DAW has the driver open — nothing to reset.
        }
    }

    private ref uint U32(int offset) => ref Unsafe.AsRef<uint>(_base + offset);
    private ref long I64(int offset) => ref Unsafe.AsRef<long>(_base + offset);

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _view.SafeMemoryMappedViewHandle.ReleasePointer();
        _view.Dispose();
        _file.Dispose();
    }
}
