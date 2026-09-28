using System.Diagnostics;

using Shouldly;

namespace Captr.Integration.Tests.Build;

/// <summary>
/// When git cannot give the build its source commit, the build says exactly why and
/// what to do - including the case that used to end in git's bare "fatal: Needed a
/// single revision", which no advice in the old message fixed.
/// </summary>
[Trait("Category", "Os")]
public sealed class SourceCommitTests : IDisposable
{
    private static readonly string[] Identity = ["-c", "user.name=t", "-c", "user.email=t@example.invalid"];

    private readonly string _root = Directory.CreateTempSubdirectory("captr-srccommit-").FullName;

    public void Dispose()
    {
        foreach (string file in Directory.EnumerateFiles(_root, "*", SearchOption.AllDirectories))
        {
            File.SetAttributes(file, FileAttributes.Normal); // git writes objects read-only
        }

        Directory.Delete(_root, recursive: true);
    }

    private static async Task GitAsync(string folder, params string[] arguments)
    {
        var startInfo = new ProcessStartInfo("git") { UseShellExecute = false, CreateNoWindow = true };
        startInfo.ArgumentList.Add("-C");
        startInfo.ArgumentList.Add(folder);
        foreach (string argument in Identity.Concat(arguments))
        {
            startInfo.ArgumentList.Add(argument);
        }

        using Process git = Process.Start(startInfo)!;
        await git.WaitForExitAsync(TestContext.Current.CancellationToken);
        git.ExitCode.ShouldBe(0, "git " + string.Join(' ', arguments));
    }

    /// <summary>Runs Get-SourceCommit on a folder and returns (exit code, output).</summary>
    private async Task<(int ExitCode, string Output)> ResolveAsync(string folder)
    {
        string script = Path.Combine(_root, "resolve-" + Guid.NewGuid().ToString("N") + ".ps1");
        await File.WriteAllTextAsync(script,
            $". '{BuildScriptHarness.Script(@"build\lib\SourceCommit.ps1")}'\n" +
            "$ErrorActionPreference = 'Stop'\n" +
            $"Get-SourceCommit -RepoRoot '{folder}'\n",
            TestContext.Current.CancellationToken);
        return await BuildScriptHarness.RunAsync(script, []);
    }

    private string Folder(string name) => Directory.CreateDirectory(Path.Combine(_root, name)).FullName;

    [Fact]
    public async Task A_repository_with_no_commits_is_named_as_such_with_the_fix()
    {
        // A copied folder turned into a repository with 'git init' and never committed.
        string copy = Folder("copy-then-init");
        await GitAsync(copy, "init", "-q");

        (int exitCode, string output) = await ResolveAsync(copy);

        exitCode.ShouldNotBe(0);
        output.ShouldContain("has no commits");
        output.ShouldContain("git clone");
    }

    [Fact]
    public async Task A_folder_inside_another_repository_names_that_repository()
    {
        // C:\Projects was 'git init'-ed; the unzipped Captr folder sits inside it.
        string parent = Folder("projects");
        await GitAsync(parent, "init", "-q");
        string captr = Directory.CreateDirectory(Path.Combine(parent, "screen-recorder")).FullName;

        (int exitCode, string output) = await ResolveAsync(captr);

        exitCode.ShouldNotBe(0);
        output.ShouldContain("is not a clone of Captr");
        output.ShouldContain(parent);
    }

    [Fact]
    public async Task A_plain_copy_says_to_clone()
    {
        (int exitCode, string output) = await ResolveAsync(Folder("plain-copy"));

        exitCode.ShouldNotBe(0);
        output.ShouldContain("is not a git clone");
    }

    [Fact]
    public async Task A_real_clone_gives_its_commit()
    {
        string clone = Folder("clone");
        await GitAsync(clone, "init", "-q");
        await File.WriteAllTextAsync(Path.Combine(clone, "a.txt"), "a", TestContext.Current.CancellationToken);
        await GitAsync(clone, "add", ".");
        await GitAsync(clone, "commit", "-q", "-m", "a");

        (int exitCode, string output) = await ResolveAsync(clone);

        exitCode.ShouldBe(0, output);
        output.Trim().Length.ShouldBe(12);
    }
}
