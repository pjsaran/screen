using Shouldly;

namespace Captr.Integration.Tests.Build;

/// <summary>
/// set-version.ps1 accepts only versions the build and the installer can stamp.
/// </summary>
[Trait("Category", "Os")]
public sealed class VersionScriptTests : IDisposable
{
    private readonly string _repo = Directory.CreateTempSubdirectory("captr-version-").FullName;

    public VersionScriptTests() =>
        File.WriteAllText(Path.Combine(_repo, "Version.props"),
            "<Project><PropertyGroup><CaptrVersion>0.1.1</CaptrVersion></PropertyGroup></Project>");

    public void Dispose() => Directory.Delete(_repo, recursive: true);

    private Task<(int ExitCode, string Output)> SetAsync(string version) =>
        BuildScriptHarness.RunAsync(BuildScriptHarness.Script(@"build\set-version.ps1"), [version, "-RepoRoot", _repo]);

    [Fact]
    public async Task A_prerelease_suffix_is_refused_before_it_can_break_the_build()
    {
        // Accepted before: AssemblyVersion became "0.2.0-beta.1.0" and the next build
        // failed with CS7034.
        (int exitCode, string output) = await SetAsync("0.2.0-beta.1");

        exitCode.ShouldNotBe(0);
        output.ShouldContain("must be numeric");
        (await File.ReadAllTextAsync(Path.Combine(_repo, "Version.props"), TestContext.Current.CancellationToken))
            .ShouldContain("<CaptrVersion>0.1.1</CaptrVersion>");
    }

    [Fact]
    public async Task A_plain_version_is_written()
    {
        (int exitCode, string output) = await SetAsync("0.2.0");

        exitCode.ShouldBe(0, output);
        (await File.ReadAllTextAsync(Path.Combine(_repo, "Version.props"), TestContext.Current.CancellationToken))
            .ShouldContain("<CaptrVersion>0.2.0</CaptrVersion>");
    }
}
