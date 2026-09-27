using System.Diagnostics;
using System.Text.Json;

using Captr.Core.Cli;
using Captr.Core.Common;

using Shouldly;

namespace Captr.Integration.Tests.EndToEnd;

/// <summary>
/// Every `captr` command and every documented exit code (docs/user-guide/command-line.md),
/// driven through the PUBLISHED executable a scheduled task runs — against a data root
/// of its own, so the developer's settings, queue, and running recorder are never touched.
/// </summary>
/// <remarks>
/// Nothing here records: exit code 11 (paused) and the recording paths need a real
/// desktop and are in <see cref="RecordingEndToEndTests"/>. Where an exit code is only
/// reachable by breaking the installation (3: no recorder could be started), the test
/// breaks a hard-linked copy of the payload, never the payload itself.
/// </remarks>
[Trait("Category", "Published")]
public sealed class CliEndToEndTests : IDisposable
{
    private readonly string _root = Directory.CreateTempSubdirectory("captr-cli-e2e-").FullName;
    private readonly DateTime _startedUtc = DateTime.UtcNow;

    public void Dispose()
    {
        EndToEnd.KillHostsStartedSince(_startedUtc);
        EndToEnd.DeleteQuietly(_root);
    }

    private string DataRoot => Path.Combine(_root, "data");

    private Task<CliRun> RunAsync(params string[] arguments) =>
        EndToEnd.RunCliAsync(PublishedPayload.Cli(), arguments, DataRoot);

    [Fact]
    public async Task Help_lists_every_command_and_exits_0()
    {
        CliRun run = await RunAsync("--help");

        run.ExitCode.ShouldBe(ExitCodes.Success);
        foreach (string command in new[] { "start", "stop", "pause", "resume", "status", "recordings", "recover",
                                           "settings", "transfers", "auth", "doctor", "version" })
        {
            run.Stdout.ShouldContain(command);
        }
    }

    [Theory]
    [InlineData("teleport")]
    [InlineData("start", "--fps", "0")]
    [InlineData("start", "--fps", "7")]
    [InlineData("start", "--quality", "sublime")]
    [InlineData("start", "--preset", "ludicrous")]
    [InlineData("transfers", "retry", "not-a-number")]
    [InlineData("settings", "set")]
    public async Task Bad_arguments_exit_2_with_the_problem_on_stderr(params string[] arguments)
    {
        CliRun run = await RunAsync(arguments);

        run.ExitCode.ShouldBe(ExitCodes.Usage, run.ToString());
        run.Stderr.ShouldNotBeEmpty();
    }

    [Fact]
    public async Task Status_with_nothing_recording_exits_10_and_reports_idle_as_json()
    {
        CliRun run = await RunAsync("status", "--json");

        run.ExitCode.ShouldBe(ExitCodes.Idle, run.ToString());
        JsonDocument.Parse(run.Stdout).RootElement.GetProperty("state").GetString().ShouldBe("idle");
    }

    [Theory]
    [InlineData("stop")]
    [InlineData("pause")]
    [InlineData("resume")]
    public async Task Stop_pause_and_resume_with_nothing_recording_succeed(string command)
    {
        // SPEC §10: schedulers fire twice more often than anyone expects.
        CliRun run = await RunAsync(command, "--json");

        run.ExitCode.ShouldBe(ExitCodes.Success, run.ToString());
        Should.NotThrow(() => JsonDocument.Parse(run.Stdout));
    }

    [Fact]
    public async Task Recordings_list_answers_as_json_without_starting_anything()
    {
        CliRun run = await RunAsync("recordings", "list", "--json");

        run.ExitCode.ShouldBe(ExitCodes.Success, run.ToString());
        Should.NotThrow(() => JsonDocument.Parse(run.Stdout));
    }

    [Theory]
    [InlineData("verify")]
    [InlineData("resend")]
    public async Task Verifying_or_resending_a_folder_that_is_not_a_recording_exits_1(string command)
    {
        string notARecording = Directory.CreateDirectory(Path.Combine(_root, "not-a-recording")).FullName;

        CliRun run = await RunAsync("recordings", command, notARecording, "--json");

        run.ExitCode.ShouldBe(ExitCodes.Error, run.ToString());
    }

    [Fact]
    public async Task Resend_takes_a_relative_path_relative_to_where_it_is_typed()
    {
        // The recorder runs in its own directory; a relative path used to be looked up
        // THERE and answered "No integrity record" for a perfectly good recording.
        string parent = Directory.CreateDirectory(Path.Combine(_root, "here")).FullName;
        string recording = Directory.CreateDirectory(Path.Combine(parent, "rec1")).FullName;
        await File.WriteAllTextAsync(Path.Combine(recording, "out.mkv"), "footage", TestContext.Current.CancellationToken);
        new Captr.Core.Sessions.IntegrityRecord
        {
            SessionId = Guid.NewGuid(),
            FinalizedUtc = DateTimeOffset.UtcNow,
            Segments = [],
            Outputs = [new Captr.Core.Sessions.HashedFile("out.mkv", 7, "00")],
            Coverage = new Captr.Core.Sessions.CoverageReport(Guid.NewGuid(), DateTimeOffset.UtcNow, DateTimeOffset.UtcNow, TimeSpan.Zero, TimeSpan.Zero, []),
            RepairedSegments = 0,
            Notes = [],
        }.Write(recording);

        CliRun run = await EndToEnd.RunCliAsync(PublishedPayload.Cli(), ["recordings", "resend", "rec1"], DataRoot, workingDirectory: parent);

        (run.Stdout + run.Stderr).ShouldNotContain("No integrity record", Case.Insensitive, run.ToString());
    }

    [Fact]
    public async Task A_piped_secret_longer_than_any_secret_is_refused_and_nothing_is_stored()
    {
        string name = "captr-e2e-" + Guid.NewGuid().ToString("N")[..8];

        CliRun set = await EndToEnd.RunCliAsync(PublishedPayload.Cli(), ["auth", "set-secret", name], DataRoot, stdin: new string('x', 10_000));

        set.ExitCode.ShouldBe(ExitCodes.Error, set.ToString());
        set.Stderr.ShouldContain("longer than");
        (await RunAsync("auth", "status", name)).ExitCode.ShouldBe(ExitCodes.Error, "nothing was stored");
    }

    [Fact]
    public async Task Recover_with_nothing_to_recover_exits_0()
    {
        CliRun run = await RunAsync("recover", "--json");

        run.ExitCode.ShouldBe(ExitCodes.Success, run.ToString());
        Should.NotThrow(() => JsonDocument.Parse(run.Stdout));
    }

    [Fact]
    public async Task Transfers_list_answers_and_retry_or_stop_of_an_unknown_id_exits_1()
    {
        (await RunAsync("transfers", "list", "--json")).ExitCode.ShouldBe(ExitCodes.Success);

        CliRun retry = await RunAsync("transfers", "retry", "999999");
        CliRun stop = await RunAsync("transfers", "stop", "999999");

        retry.ExitCode.ShouldBe(ExitCodes.Error, "there is no transfer 999999 to retry");
        stop.ExitCode.ShouldBe(ExitCodes.Error, "there is no transfer 999999 to stop");
        retry.Stdout.ShouldContain("transfers list");
    }

    [Fact]
    public async Task Settings_set_validates_and_get_shows_what_was_saved()
    {
        (await RunAsync("settings", "set", "framerate", "10")).ExitCode.ShouldBe(ExitCodes.Success);
        CliRun unsupported = await RunAsync("settings", "set", "framerate", "7");
        CliRun unknownKey = await RunAsync("settings", "set", "noSuchSetting", "1");
        CliRun badPattern = await RunAsync("settings", "set", "outputPattern", "{nope}");
        CliRun get = await RunAsync("settings", "get");

        unsupported.ExitCode.ShouldBe(ExitCodes.Error);
        unknownKey.ExitCode.ShouldBe(ExitCodes.Error);
        badPattern.ExitCode.ShouldBe(ExitCodes.Error, "an invalid naming pattern is refused before it is saved");
        badPattern.Stderr.ShouldContain("nope");
        get.ExitCode.ShouldBe(ExitCodes.Success);
        JsonDocument.Parse(get.Stdout).RootElement.GetProperty("framerate").GetInt32().ShouldBe(10);
        File.Exists(Path.Combine(DataRoot, "settings.json")).ShouldBeTrue("the relocated data root holds the settings");
    }

    [Fact]
    public async Task Settings_init_creates_once_and_never_overwrites()
    {
        CliRun first = await RunAsync("settings", "init", "--json");
        (await RunAsync("settings", "set", "framerate", "20")).ExitCode.ShouldBe(ExitCodes.Success);
        CliRun second = await RunAsync("settings", "init", "--json");

        first.ExitCode.ShouldBe(ExitCodes.Success);
        JsonDocument.Parse(first.Stdout).RootElement.GetProperty("created").GetBoolean().ShouldBeTrue();
        second.ExitCode.ShouldBe(ExitCodes.Success);
        JsonDocument.Parse(second.Stdout).RootElement.GetProperty("created").GetBoolean().ShouldBeFalse();
        JsonDocument.Parse((await RunAsync("settings", "get")).Stdout).RootElement.GetProperty("framerate").GetInt32().ShouldBe(20);
    }

    [Fact]
    public async Task Settings_init_restores_the_previous_version_instead_of_writing_defaults_over_it()
    {
        // settings.json is gone but its previous version survives. `init` - which the
        // installer runs on every install - used to see "no file" and write defaults,
        // after which recovery from the previous version could never happen.
        Directory.CreateDirectory(DataRoot);
        await File.WriteAllTextAsync(Path.Combine(DataRoot, "settings.json.bak"),
            $$"""{"schemaVersion": 3, "framerate": 24, "workingFolder": "{{Path.Combine(_root, "kept").Replace("\\", "\\\\")}}"}""",
            TestContext.Current.CancellationToken);

        CliRun init = await RunAsync("settings", "init", "--json");

        init.ExitCode.ShouldBe(ExitCodes.Success, init.ToString());
        JsonDocument.Parse((await RunAsync("settings", "get")).Stdout).RootElement.GetProperty("framerate").GetInt32()
            .ShouldBe(24, "the user's own settings came back");
    }

    [Fact]
    public async Task Export_says_credentials_are_not_included_and_import_says_where_recordings_will_go()
    {
        CliRun export = await RunAsync("settings", "export");
        export.ExitCode.ShouldBe(ExitCodes.Success);
        export.Stdout.ShouldContain("Credentials are NOT included");

        // A settings file from somewhere else can move the working folder and add a
        // destination - where every future recording goes. It must say so.
        using JsonDocument exported = JsonDocument.Parse(export.Stdout);
        var document = System.Text.Json.Nodes.JsonNode.Parse(export.Stdout)!.AsObject();
        document["workingFolder"] = Path.Combine(_root, "imported-work");
        document["destinations"] = new System.Text.Json.Nodes.JsonArray(new System.Text.Json.Nodes.JsonObject
        {
            ["name"] = "archive",
            ["kind"] = "folder",
            ["folderPath"] = Path.Combine(_root, "archive"),
        });
        string file = Path.Combine(_root, "import.json");
        await File.WriteAllTextAsync(file, document.ToJsonString(), TestContext.Current.CancellationToken);

        CliRun import = await RunAsync("settings", "import", file);

        import.ExitCode.ShouldBe(ExitCodes.Success, import.ToString());
        import.Stdout.ShouldContain("Working folder:");
        import.Stdout.ShouldContain("Destination added: archive");
    }

    [Fact]
    public async Task Importing_something_that_is_not_settings_exits_1()
    {
        string file = Path.Combine(_root, "garbage.json");
        await File.WriteAllTextAsync(file, "{ not json", TestContext.Current.CancellationToken);

        (await RunAsync("settings", "import", file)).ExitCode.ShouldBe(ExitCodes.Error);
    }

    [Fact]
    public async Task Auth_stores_confirms_and_deletes_a_secret_without_ever_printing_it()
    {
        string name = "captr-e2e-" + Guid.NewGuid().ToString("N")[..8];
        const string Secret = "e2e-secret-value-should-never-appear";
        try
        {
            (await RunAsync("auth", "status", name)).ExitCode.ShouldBe(ExitCodes.Error, "nothing stored yet");

            CliRun set = await EndToEnd.RunCliAsync(PublishedPayload.Cli(), ["auth", "set-secret", name], DataRoot, stdin: Secret);
            CliRun status = await RunAsync("auth", "status", name);

            set.ExitCode.ShouldBe(ExitCodes.Success, set.ToString());
            status.ExitCode.ShouldBe(ExitCodes.Success);
            (set.Stdout + set.Stderr + status.Stdout + status.Stderr).ShouldNotContain(Secret);
        }
        finally
        {
            (await RunAsync("auth", "delete", name)).ExitCode.ShouldBe(ExitCodes.Success);
        }

        (await RunAsync("auth", "status", name)).ExitCode.ShouldBe(ExitCodes.Error, "deleted");
    }

    [Fact]
    public async Task Doctor_exits_1_when_the_settings_are_broken_and_names_the_problem()
    {
        Directory.CreateDirectory(DataRoot);
        await File.WriteAllTextAsync(Path.Combine(DataRoot, "settings.json"),
            """{"schemaVersion": 3, "retentionDays": 99999}""", TestContext.Current.CancellationToken);

        CliRun run = await RunAsync("doctor", "--json");

        run.ExitCode.ShouldBe(ExitCodes.Error, run.ToString());
        run.Stdout.ShouldContain("etention");
    }

    [Fact]
    public async Task Version_names_the_exact_build()
    {
        CliRun run = await RunAsync("version", "--json");

        run.ExitCode.ShouldBe(ExitCodes.Success);
        JsonElement root = JsonDocument.Parse(run.Stdout).RootElement;
        root.GetProperty("commit").GetString().ShouldNotBe("unknown");
        root.GetProperty("ffmpegBuildId").GetString().ShouldStartWith("ffmpeg-");
    }

    [Fact]
    public async Task Start_exits_3_when_no_recorder_can_be_started()
    {
        // The only honest way to reach "no recorder answered and none could be
        // started": a copy of the installation with the recorder missing.
        string broken = EndToEnd.LinkedCopyOfPayload(Path.Combine(_root, "broken-install"), except: ["Captr.App.exe"]);

        CliRun run = await EndToEnd.RunCliAsync(Path.Combine(broken, "captr.exe"), ["start"], DataRoot);

        run.ExitCode.ShouldBe(ExitCodes.HostUnreachable, run.ToString());
        run.Stderr.ShouldContain("Captr.App.exe");
    }

    [Fact]
    public async Task A_relocated_data_root_keeps_the_real_settings_untouched()
    {
        string real = Captr.Core.Settings.SettingsStore.DefaultSettingsPath();
        string? before = File.Exists(real) ? await File.ReadAllTextAsync(real, TestContext.Current.CancellationToken) : null;

        (await RunAsync("settings", "set", "framerate", "5")).ExitCode.ShouldBe(ExitCodes.Success);

        string? after = File.Exists(real) ? await File.ReadAllTextAsync(real, TestContext.Current.CancellationToken) : null;
        after.ShouldBe(before);
        CaptrPaths.DataRootVariable.ShouldBe("CAPTR_DATA_ROOT");
    }
}
