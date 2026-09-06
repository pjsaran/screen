using System.Diagnostics;
using System.Text.Json;

using Shouldly;

namespace Captr.Integration.Tests.Cli;

/// <summary>
/// The CLI's published contract (SPEC §14: "every command produces correct exit
/// codes and valid JSON"). Deliberately runs against the PUBLISHED payload —
/// <c>publish/captr.exe</c> — because that is the file a scheduled task actually
/// invokes, and the apphost rename it depends on happens at publish time.
/// </summary>
/// <remarks>
/// Its own category, and the reason is an ordering one rather than a hardware one.
/// These tests were once trait <c>Ffmpeg</c>: correct about needing no GPU and no
/// desktop, but wrong about what "CI-safe" means, because <c>publish/</c> does not
/// exist until the packaging step that runs AFTER the test steps. They passed only
/// on a machine with a stale <c>publish/</c> from an earlier run, and failed on
/// every clean clone and every CI run. A category is a promise about what a test
/// needs; this one needs a published payload, so it says so.
/// </remarks>
[Trait("Category", "Published")]
public class CliContractTests
{
    private static string Cli()
    {
        DirectoryInfo? directory = new(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "Captr.slnx")))
        {
            directory = directory.Parent;
        }

        directory.ShouldNotBeNull("could not locate the repo root");
        string cli = Path.Combine(directory.FullName, "publish", "captr.exe");
        File.Exists(cli).ShouldBeTrue($"No published payload at {cli}. Run: pwsh build/build.ps1 -Publish -SkipTests");
        return cli;
    }

    private static (int ExitCode, string Stdout, string Stderr) Run(params string[] arguments)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = Cli(),
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        foreach (string argument in arguments)
        {
            startInfo.ArgumentList.Add(argument);
        }

        using Process process = Process.Start(startInfo)!;
        string stdout = process.StandardOutput.ReadToEnd();
        string stderr = process.StandardError.ReadToEnd();
        process.WaitForExit(60_000).ShouldBeTrue($"'captr {string.Join(' ', arguments)}' did not return");
        return (process.ExitCode, stdout, stderr);
    }

    [Theory]
    [InlineData("status")]
    [InlineData("recordings", "list")]
    [InlineData("settings", "get")]
    public void Read_only_commands_emit_parseable_json_on_stdout(params string[] arguments)
    {
        string[] withJson = arguments[^1] == "get" ? arguments : [.. arguments, "--json"];

        (int exitCode, string stdout, string stderr) = Run(withJson);

        exitCode.ShouldBeOneOf(0, 10, 11);
        stderr.ShouldBeEmpty("results go to stdout, errors to stderr (SPEC §10)");
        Should.NotThrow(() => JsonDocument.Parse(stdout), $"stdout was not JSON:\n{stdout}");
    }

    [Fact]
    public void Stopping_when_idle_is_a_success_not_an_error()
    {
        // SPEC §10: "stopping when idle is not an error — report the existing state
        // and exit successfully. Schedulers fire twice more often than anyone expects."
        KillStrayHosts();

        (int exitCode, string stdout, _) = Run("stop");

        exitCode.ShouldBe(0);
        stdout.ShouldNotBeEmpty();
    }

    [Fact]
    public void Status_when_idle_reports_the_documented_idle_exit_code()
    {
        KillStrayHosts();

        (int exitCode, string stdout, _) = Run("status", "--json");

        exitCode.ShouldBe(10, "10 = idle, so a scheduled task can branch on it");
        JsonDocument.Parse(stdout).RootElement.GetProperty("state").GetString().ShouldBe("idle");
    }

    [Fact]
    public void Version_names_the_exact_build_including_commit_and_ffmpeg_identity()
    {
        // SPEC §11: "a user must be able to determine from a running installation
        // exactly which build they have."
        (int exitCode, string stdout, _) = Run("version", "--json");

        exitCode.ShouldBe(0);
        System.Text.Json.JsonElement root = JsonDocument.Parse(stdout).RootElement;
        root.GetProperty("version").GetString().ShouldNotBeNullOrWhiteSpace();
        root.GetProperty("commit").GetString().ShouldNotBe("unknown", "the build must carry its source commit");
        root.GetProperty("ffmpegBuildId").GetString().ShouldStartWith("ffmpeg-");
    }

    [Fact]
    public void An_unknown_command_fails_with_the_usage_exit_code_and_writes_to_stderr()
    {
        (int exitCode, _, string stderr) = Run("teleport");

        exitCode.ShouldBe(Captr.Core.Cli.ExitCodes.Usage);
        stderr.ShouldNotBeEmpty("errors go to stderr (SPEC §10)");
    }

    [Fact]
    public void Verifying_a_folder_that_is_not_a_session_fails_cleanly_rather_than_crashing()
    {
        string empty = Directory.CreateTempSubdirectory("captr-notasession-").FullName;
        try
        {
            (int exitCode, string stdout, _) = Run("recordings", "verify", empty, "--json");

            exitCode.ShouldBe(Captr.Core.Cli.ExitCodes.Error);
            JsonDocument.Parse(stdout).RootElement.GetProperty("intact").GetBoolean().ShouldBeFalse();
        }
        finally
        {
            Directory.Delete(empty, recursive: true);
        }
    }

    [Fact]
    public void Auth_status_never_reveals_a_secret_only_whether_one_exists()
    {
        (int exitCode, string stdout, _) = Run("auth", "status", "definitely-not-provisioned");

        exitCode.ShouldBe(Captr.Core.Cli.ExitCodes.Error, "no such credential");
        stdout.ShouldContain("No secret");
    }

    private static void KillStrayHosts()
    {
        foreach (Process process in Process.GetProcessesByName("Captr.App"))
        {
            try
            {
                process.Kill(entireProcessTree: true);
                process.WaitForExit(5000);
            }
            catch (Exception exception) when (exception is InvalidOperationException or System.ComponentModel.Win32Exception)
            {
            }
            finally
            {
                process.Dispose();
            }
        }
    }
}
