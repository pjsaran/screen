namespace Captr.Core.Displays;

/// <summary>
/// Resolves the user's display choice against what is attached right now (SPEC §5).
/// Pure — every topology scenario (reorder, hot-plug, the return of a deselected
/// display) is unit tested on fixtures. Owns the two rules that keep selection safe:
/// selection is an EXCLUSION set (new displays record by default), and matching is
/// by stable identity only (indices reorder).
/// </summary>
public static class DisplaySelection
{
    /// <summary>
    /// Splits the attached displays into included/excluded, flags displays never
    /// seen before (for the UI banner SPEC §5 asks for), and says whether recording
    /// may start at all.
    /// </summary>
    /// <param name="attached">Displays attached right now.</param>
    /// <param name="excludedStableIds">The user's exclusions (from settings).</param>
    /// <param name="previouslySeenStableIds">Every stable id ever seen on this
    /// machine (from <see cref="SeenDisplaysStore"/>).</param>
    public static ResolvedSelection Resolve(
        IReadOnlyList<DisplayInfo> attached,
        IReadOnlyCollection<string> excludedStableIds,
        IReadOnlyCollection<string> previouslySeenStableIds)
    {
        var excludedSet = new HashSet<string>(excludedStableIds, StringComparer.OrdinalIgnoreCase);
        var seenSet = new HashSet<string>(previouslySeenStableIds, StringComparer.OrdinalIgnoreCase);

        var included = new List<DisplayInfo>();
        var excluded = new List<DisplayInfo>();
        var newlyAttached = new List<DisplayInfo>();

        foreach (DisplayInfo display in attached)
        {
            if (excludedSet.Contains(display.StableId))
            {
                excluded.Add(display);
            }
            else
            {
                included.Add(display);
            }

            if (!seenSet.Contains(display.StableId))
            {
                newlyAttached.Add(display);
            }
        }

        return new ResolvedSelection(included, excluded, newlyAttached);
    }
}

/// <summary>
/// The outcome of resolving selection against the current topology.
/// <see cref="CanStartRecording"/> is false when every display is excluded —
/// starting would record nothing, so SPEC §5 blocks it.
/// </summary>
public sealed record ResolvedSelection(
    IReadOnlyList<DisplayInfo> Included,
    IReadOnlyList<DisplayInfo> Excluded,
    IReadOnlyList<DisplayInfo> NewlyAttached)
{
    /// <summary>Recording may start only when at least one display is included.</summary>
    public bool CanStartRecording => Included.Count > 0;
}
