using Shouldly;

namespace Captr.Integration.Tests.Build;

/// <summary>
/// The build-script tests see what a script said, whatever console width PowerShell
/// laid it out at.
/// </summary>
/// <remarks>
/// A signing test passed on one PC and failed on another: PowerShell wrapped the
/// thrown message at the console width, and the longer temp path there (it contains
/// the Windows user name) pushed "not the pinned FFmpeg build" across a line break.
/// </remarks>
[Trait("Category", "Os")]
public sealed class BuildScriptHarnessTests : IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("captr-harness-").FullName;

    public void Dispose() => Directory.Delete(_dir, recursive: true);

    [Theory]
    [InlineData(10)]
    [InlineData(55)]
    [InlineData(140)]
    public async Task A_thrown_message_reads_whole_wherever_the_console_wraps_it(int pathLength)
    {
        string path = @"C:\Users\" + new string('u', pathLength) + @"\AppData\Local\Temp\payload\ffmpeg\ffmpeg.exe";
        string script = Path.Combine(_dir, "throws.ps1");
        await File.WriteAllTextAsync(script,
            $"throw (\"Refusing to sign {path}: it is not the pinned FFmpeg build (digest 1234, pinned 5678). Re-run build/fetch-ffmpeg.ps1.\")",
            TestContext.Current.CancellationToken);

        (int exitCode, string output) = await BuildScriptHarness.RunAsync(script, []);

        exitCode.ShouldNotBe(0);
        output.ShouldContain("it is not the pinned FFmpeg build (digest 1234, pinned 5678)");
        output.ShouldNotContain("\u001b[", customMessage: "colour codes are not part of what the script said");
    }
}
