namespace Captr.Core.Supervision;

/// <summary>
/// A bounded buffer of the most recent encoder log lines (SPEC §6). Owns the memory
/// bound: an encoder flooding its log — the exact scenario the flood test simulates —
/// costs a fixed amount of memory here, never an OOM. Thread-safe: the tail reader
/// appends while diagnostics snapshots read.
/// </summary>
public sealed class LogRingBuffer
{
    private readonly Queue<string> _lines;
    private readonly int _capacity;
    private readonly Lock _gate = new();
    private long _everAdded;

    public LogRingBuffer(int capacity = SupervisionConstants.LogTailLines)
    {
        _capacity = capacity;
        _lines = new Queue<string>(capacity);
    }

    /// <summary>Appends a line, discarding the oldest once full.</summary>
    public void Add(string line)
    {
        lock (_gate)
        {
            if (_lines.Count == _capacity)
            {
                _lines.Dequeue();
            }

            _lines.Enqueue(line);
            _everAdded++;
        }
    }

    /// <summary>
    /// A bookmark in the line stream, for <see cref="SnapshotSince"/>. Take one when
    /// an encoder process is launched so its exit can be judged on ITS OWN output.
    /// </summary>
    public long Mark()
    {
        lock (_gate)
        {
            return _everAdded;
        }
    }

    /// <summary>
    /// The lines added since <paramref name="mark"/>, oldest first.
    /// </summary>
    /// <remarks>
    /// Why this exists: the buffer spans the whole session, and FFmpeg's report file
    /// is rewritten by every relaunch, so one early "Error" line used to sit in the
    /// tail for hundreds of lines and make every LATER exit — including a perfectly
    /// clean external one — classify as a fault. Faults march the session toward a
    /// loud stop, so a single stale line could end a recording nobody asked to end.
    /// Diagnostics still want the whole tail; only classification wants this slice.
    /// Lines evicted since the mark are simply gone — the slice is a best effort,
    /// which is exactly right for a bounded buffer.
    /// </remarks>
    public IReadOnlyList<string> SnapshotSince(long mark)
    {
        lock (_gate)
        {
            long oldestHeld = _everAdded - _lines.Count;
            int skip = (int)Math.Clamp(mark - oldestHeld, 0, _lines.Count);
            return [.. _lines.Skip(skip)];
        }
    }

    /// <summary>A stable copy of the current tail, oldest first.</summary>
    public IReadOnlyList<string> Snapshot()
    {
        lock (_gate)
        {
            return [.. _lines];
        }
    }
}
