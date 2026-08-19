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
