using System.Diagnostics;

namespace FloodgateSpike;

/// <summary>
/// Spaces packets out so the average rate equals the limit. Each packet is given a departure slot;
/// if the slot is too far in the future the packet is dropped (tail drop), which makes TCP slow down.
/// </summary>
internal sealed class Pacer
{
    private readonly object _lock = new();
    private readonly double _ticksPerByte;
    private readonly long _maxQueueTicks;
    private long _nextFree;

    public Pacer(long bytesPerSecond, double maxQueueSeconds)
    {
        _ticksPerByte = (double)Stopwatch.Frequency / bytesPerSecond;
        _maxQueueTicks = (long)(maxQueueSeconds * Stopwatch.Frequency);
        _nextFree = Stopwatch.GetTimestamp();
    }

    /// <summary>Returns false when the queue is full and the packet should be dropped.</summary>
    public bool TryReserve(int bytes, out long releaseAtTicks)
    {
        long now = Stopwatch.GetTimestamp();
        lock (_lock)
        {
            long start = Math.Max(now, _nextFree);
            if (start - now > _maxQueueTicks)
            {
                releaseAtTicks = 0;
                return false;
            }
            _nextFree = start + (long)(bytes * _ticksPerByte);
            releaseAtTicks = start;
            return true;
        }
    }
}
