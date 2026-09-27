namespace Captr.Core.Naming;

/// <summary>
/// What a naming pattern or destination folder would produce for a recording made
/// now, in words for the box it is typed into — or what is wrong with it. Owns the
/// live preview the Settings page and the destination editor show while typing.
/// </summary>
/// <remarks>
/// A pattern used to be checked only when Save was pressed, and what it would
/// actually produce was never shown: "{date:dd-MM}" and "{date:MM-dd}" looked equally
/// right until the first recording landed with the wrong one. The rules are
/// <see cref="OutputNamer"/>'s; this only puts them in front of the person typing.
/// </remarks>
public static class NamingPreview
{
    /// <summary>The problem, when there is one — the caller shows it as an error.</summary>
    public sealed record Result(string Text, bool IsProblem);

    /// <summary>A recording that started an hour before <paramref name="nowUtc"/> on
    /// this PC, labelled "label", in the local time zone.</summary>
    public static NamingContext SampleContext(DateTimeOffset nowUtc) => new()
    {
        StartUtc = nowUtc.AddHours(-1),
        EndUtc = nowUtc,
        MachineName = Environment.MachineName,
        UserName = Environment.UserName,
        TimeZone = TimeZoneInfo.Local,
        Label = "label",
    };

    /// <summary>The recording's own file name, from the pattern on the Settings page.
    /// Empty means the default pattern, which is said.</summary>
    public static Result ForFileName(string? pattern, DateTimeOffset nowUtc)
    {
        if (!string.IsNullOrWhiteSpace(pattern) && OutputNamer.DescribePatternProblem(pattern) is { } problem)
        {
            return new Result(problem, IsProblem: true);
        }

        string name = OutputNamer.BuildFileName(pattern ?? "", SampleContext(nowUtc));
        return new Result(
            (string.IsNullOrWhiteSpace(pattern) ? "Empty uses the default. " : "") +
            "A recording started an hour ago would be named: " + name,
            IsProblem: false);
    }

    /// <summary>The name at one destination. Empty means "the recording's own name",
    /// so there is nothing to preview.</summary>
    public static Result ForDestinationFileName(string? pattern, DateTimeOffset nowUtc) =>
        string.IsNullOrWhiteSpace(pattern)
            ? new Result("", IsProblem: false)
            : ForFileName(pattern, nowUtc);

    /// <summary>Where a recording would land, for a folder destination
    /// (<paramref name="isLocalFolder"/>) or a folder in a SharePoint library.</summary>
    public static Result ForFolder(string? folder, bool isLocalFolder, DateTimeOffset nowUtc)
    {
        if (string.IsNullOrWhiteSpace(folder))
        {
            return new Result("", IsProblem: false);
        }

        if (OutputNamer.DescribeFolderPathProblem(folder, isLocalFolder) is { } problem)
        {
            return new Result(problem, IsProblem: true);
        }

        return folder.Contains('{', StringComparison.Ordinal)
            ? new Result("A recording made now would go to: " + OutputNamer.ExpandFolderPath(folder, SampleContext(nowUtc)), IsProblem: false)
            : new Result("", IsProblem: false);
    }
}
