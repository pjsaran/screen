using System.Text.Json;

using Captr.Core.Supervision;

using Shouldly;

namespace Captr.Integration.Tests.Supervision;

/// <summary>
/// The digest build/fetch-ffmpeg.ps1 records (PowerShell) and the one the
/// application checks (C#) are the same function of the same bytes.
/// </summary>
/// <remarks>
/// Two implementations of one algorithm drift apart silently; if they did, every
/// installation would refuse its own FFmpeg. This holds them together on the real
/// pinned binaries.
/// </remarks>
[Trait("Category", "Ffmpeg")]
public class PinnedFfmpegDigestTests
{
    [Theory]
    [InlineData("ffmpeg.exe")]
    [InlineData("ffprobe.exe")]
    public void The_recorded_digest_is_what_the_application_computes(string binary)
    {
        string path = FfmpegLocator.Find(AppContext.BaseDirectory, binary);
        string capabilities = Path.Combine(Path.GetDirectoryName(path)!, "..", "capabilities.json");

        using JsonDocument document = JsonDocument.Parse(File.ReadAllText(capabilities));
        string recorded = document.RootElement.GetProperty("binaryDigests").GetProperty(binary).GetString()!;

        PeImageDigest.Compute(path).ShouldBe(recorded);
    }
}
