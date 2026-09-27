using System.Diagnostics;

using Captr.Core.Encoders;
using Captr.Core.Supervision;

using Shouldly;

namespace Captr.Integration.Tests.Encoders;

/// <summary>
/// The overlay's escaping, proved on the pinned FFmpeg: text with every character
/// the filter graph reserves renders EXACTLY as written.
/// </summary>
/// <remarks>
/// The escaper's own documentation said it was exercised against the real binary;
/// nothing did that. Here each text is drawn twice - once through Captr's escaping,
/// once read verbatim from a file with expansion off - and the two frames must be
/// identical, byte for byte.
/// </remarks>
[Trait("Category", "Ffmpeg")]
public sealed class DrawTextOnRealFfmpegTests : IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("captr-drawtext-").FullName;
    private readonly string _ffmpeg = FfmpegLocator.FindFfmpeg();

    public void Dispose() => Directory.Delete(_dir, recursive: true);

    private async Task<(int ExitCode, string Output)> RenderAsync(string drawtext)
    {
        var startInfo = new ProcessStartInfo(_ffmpeg)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        foreach (string argument in new[]
                 {
                     "-hide_banner", "-loglevel", "error", "-f", "lavfi", "-i", "color=c=gray:s=640x96:d=1:r=1",
                     "-filter_complex", "[0:v]" + drawtext + ",format=yuv420p[v]", "-map", "[v]",
                     "-frames:v", "1", "-f", "md5", "-",
                 })
        {
            startInfo.ArgumentList.Add(argument);
        }

        using Process process = Process.Start(startInfo)!;
        Task<string> stdout = process.StandardOutput.ReadToEndAsync(TestContext.Current.CancellationToken);
        Task<string> stderr = process.StandardError.ReadToEndAsync(TestContext.Current.CancellationToken);
        await process.WaitForExitAsync(TestContext.Current.CancellationToken);
        return (process.ExitCode, (await stdout).Trim() + (await stderr).Trim());
    }

    [Theory]
    [InlineData("100% done")]
    [InlineData(@"C:\Recordings\desk 1")]
    [InlineData("it's 09:30, [desk]; ok")]
    [InlineData(@"a\b%c:d'e,f;g[h]i")]
    public async Task Overlay_text_renders_exactly_as_written(string text)
    {
        // Reference: the same text from a file, with drawtext's own expansion off.
        string file = Path.Combine(_dir, "text.txt");
        await File.WriteAllTextAsync(file, text, TestContext.Current.CancellationToken);
        string reference = FilterGraphBuilder.BuildDrawText("x").Replace(
            ":text=x", ":expansion=none:textfile=" + DrawTextEscaper.EscapePath(file), StringComparison.Ordinal);

        (int expectedExit, string expected) = await RenderAsync(reference);
        (int actualExit, string actual) = await RenderAsync(FilterGraphBuilder.BuildDrawText(text));

        expectedExit.ShouldBe(0, expected);
        actualExit.ShouldBe(0, "the overlay must not break the capture graph: " + actual);
        actual.ShouldBe(expected, "the overlay must show the text exactly as written");
    }

    [Fact]
    public async Task A_time_expansion_still_works()
    {
        (int exitCode, string output) = await RenderAsync(FilterGraphBuilder.BuildDrawText("%{localtime}"));

        exitCode.ShouldBe(0, output);
    }
}
