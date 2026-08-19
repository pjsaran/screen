namespace Captr.Core.Displays;

/// <summary>
/// Enumerates the currently attached displays. The production implementation
/// (<see cref="DisplayEnumerator"/>) talks to DXGI and the Windows display
/// configuration APIs; tests substitute fixture data to simulate topology changes
/// without hardware.
/// </summary>
public interface IDisplayEnumerator
{
    /// <summary>Snapshot of all displays attached right now, in DXGI output order.</summary>
    IReadOnlyList<DisplayInfo> Enumerate();
}
