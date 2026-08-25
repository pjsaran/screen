namespace Captr.Core.Encoders;

/// <summary>
/// The frame rates a user may choose, each with a plain-English name for what it
/// feels like (SPEC §5: the frame rate is a user setting, identical for every
/// display). Owns the list the UI and CLI both offer, so the two can never drift.
/// </summary>
/// <remarks>
/// The rates are not arbitrary: 5 covers slide-like screens, 15 is the default
/// because it reads as continuous for desktop work at a third of 45's cost, 24/30
/// are the video-familiar rates, and 60 exists for demos of animation. Higher rates
/// multiply both CPU and file size roughly linearly, which is why every option
/// carries its trade-off in the label.
/// </remarks>
public static class CaptureRates
{
    /// <summary>What a fresh install records at.</summary>
    public const int Default = 15;

    /// <summary>Every offered rate, slowest first.</summary>
    public static readonly IReadOnlyList<CaptureRate> All =
    [
        new(5, "Static", "Slide decks and dashboards. Smallest files, least CPU."),
        new(10, "Very low", "Reading and typing. Motion looks stepped."),
        new(15, "Low", "The default. Continuous enough for normal desktop work."),
        new(20, "Moderate", "Smoother scrolling at a modest extra cost."),
        new(24, "Cinematic", "Film rate. Smooth pans, familiar motion."),
        new(30, "Fluid", "Video-standard. Comfortable for demos and walkthroughs."),
        new(45, "High", "Fast motion stays sharp. Noticeably larger files."),
        new(60, "Maximum", "Animation and gameplay. Highest CPU and file size."),
    ];

    /// <summary>True when the rate is one Captr offers.</summary>
    public static bool IsSupported(int framesPerSecond) => All.Any(r => r.FramesPerSecond == framesPerSecond);

    /// <summary>The rate's descriptor, or null when unsupported.</summary>
    public static CaptureRate? Find(int framesPerSecond) =>
        All.FirstOrDefault(r => r.FramesPerSecond == framesPerSecond);

    /// <summary>"15 FPS [Low]" — the label the UI shows and the CLI prints.</summary>
    public static string Describe(int framesPerSecond) =>
        Find(framesPerSecond) is { } rate ? rate.Label : $"{framesPerSecond} FPS";
}

/// <summary>One selectable frame rate.</summary>
/// <param name="FramesPerSecond">The rate passed to the capture filter.</param>
/// <param name="Name">Short characterisation, e.g. "Cinematic".</param>
/// <param name="Description">One sentence on when to pick it.</param>
public sealed record CaptureRate(int FramesPerSecond, string Name, string Description)
{
    /// <summary>"24 FPS [Cinematic]".</summary>
    public string Label => $"{FramesPerSecond} FPS [{Name}]";
}
