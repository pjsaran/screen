using System.Diagnostics;
using System.Text.Json;
using System.Text.Json.Nodes;

using Captr.Core.Cli;
using Captr.Core.Sessions;

using Shouldly;

namespace Captr.Integration.Tests.EndToEnd;

/// <summary>
/// Recording through the PUBLISHED `captr` exactly as a scheduled task does — real
/// desktop capture, the real recorder, real finalisation and transfer — each test
/// with a data root of its own, and each failure path the release has to survive.
/// </summary>
[Trait("Category", "Gpu")]
public sealed class RecordingEndToEndTests : IDisposable
{
    private readonly string _root = Directory.CreateTempSubdirectory("captr-rec-e2e-").FullName;
    private readonly DateTime _startedUtc = DateTime.UtcNow;
    private readonly List<string> _copies = [];

    public void Dispose()
    {
        EndToEnd.KillHostsStartedSince(_startedUtc, [.. _copies]);
        EndToEnd.DeleteQuietly(_root);
    }

    private string DataRoot => Path.Combine(_root, "data");

    private string Work => Path.Combine(_root, "work");

    private Task<CliRun> RunAsync(params string[] arguments) => RunWithAsync(PublishedPayload.Cli(), arguments);

    private Task<CliRun> RunWithAsync(string cli, string[] arguments, IDictionary<string, string>? environment = null) =>
        EndToEnd.RunCliAsync(cli, arguments, DataRoot, environment: environment);

    /// <summary>Settings for this test: its own working folder, a quick frame rate,
    /// and — optionally — one folder destination.</summary>
    private async Task ConfigureAsync(string? destinationFolder = null)
    {
        (await RunAsync("settings", "set", "workingFolder", Work)).ExitCode.ShouldBe(ExitCodes.Success);
        (await RunAsync("settings", "set", "framerate", "10")).ExitCode.ShouldBe(ExitCodes.Success);
        if (destinationFolder is null)
        {
            return;
        }

        JsonObject document = JsonNode.Parse((await RunAsync("settings", "export")).Stdout)!.AsObject();
        document["destinations"] = new JsonArray(new JsonObject
        {
            ["name"] = "e2e-folder",
            ["kind"] = "folder",
            ["folderPath"] = destinationFolder,
        });
        string file = Path.Combine(_root, "with-destination.json");
        await File.WriteAllTextAsync(file, document.ToJsonString(), TestContext.Current.CancellationToken);
        (await RunAsync("settings", "import", file)).ExitCode.ShouldBe(ExitCodes.Success);
    }

    private static int StatusCode(CliRun run) => run.ExitCode;

    [Fact]
    public async Task A_scheduled_style_recording_runs_pauses_resumes_stops_finalises_verifies_and_transfers()
    {
        string destination = Path.Combine(_root, "destination");
        await ConfigureAsync(destination);

        CliRun start = await RunAsync("start", "--label", "e2e", "--json");
        start.ExitCode.ShouldBe(ExitCodes.Success, start.ToString());
        (await RunAsync("start")).ExitCode.ShouldBe(ExitCodes.Success, "starting twice is a success, not a second recording");

        await Task.Delay(TimeSpan.FromSeconds(6), TestContext.Current.CancellationToken);
        StatusCode(await RunAsync("status")).ShouldBe(ExitCodes.Success, "0 = recording");

        (await RunAsync("pause")).ExitCode.ShouldBe(ExitCodes.Success);
        await Task.Delay(TimeSpan.FromSeconds(3), TestContext.Current.CancellationToken);
        StatusCode(await RunAsync("status")).ShouldBe(ExitCodes.Paused, "11 = paused");

        (await RunAsync("resume")).ExitCode.ShouldBe(ExitCodes.Success);
        await Task.Delay(TimeSpan.FromSeconds(3), TestContext.Current.CancellationToken);
        StatusCode(await RunAsync("status")).ShouldBe(ExitCodes.Success);

        // The while-recording lock: asking the encoder for MORE is refused.
        CliRun increase = await RunAsync("settings", "set", "framerate", "30");
        increase.ExitCode.ShouldBe(ExitCodes.Error, increase.ToString());

        (await RunAsync("stop")).ExitCode.ShouldBe(ExitCodes.Success);
        await EndToEnd.WaitUntilIdleAsync(PublishedPayload.Cli(), DataRoot, TimeSpan.FromMinutes(3));

        JsonElement recording = JsonDocument.Parse((await RunAsync("recordings", "list", "--json")).Stdout)
            .RootElement.GetProperty("recordings").EnumerateArray().ShouldHaveSingleItem();
        string folder = recording.GetProperty("folder").GetString()!;
        (await RunAsync("recordings", "verify", folder)).ExitCode.ShouldBe(ExitCodes.Success, "an untouched recording verifies");

        string output = await WaitForTransferAsync(destination);
        new FileInfo(output).Length.ShouldBeGreaterThan(0);
    }

    [Fact]
    public async Task When_ffmpeg_dies_mid_recording_it_is_restarted_and_the_gap_is_recorded_honestly()
    {
        await ConfigureAsync();
        (await RunAsync("start")).ExitCode.ShouldBe(ExitCodes.Success);
        await Task.Delay(TimeSpan.FromSeconds(6), TestContext.Current.CancellationToken);

        Process encoder = OurEncoder();
        encoder.Kill();
        await encoder.WaitForExitAsync(TestContext.Current.CancellationToken);
        await Task.Delay(TimeSpan.FromSeconds(8), TestContext.Current.CancellationToken);

        StatusCode(await RunAsync("status")).ShouldBe(ExitCodes.Success, "the recording carries on");
        (await RunAsync("stop")).ExitCode.ShouldBe(ExitCodes.Success);
        await EndToEnd.WaitUntilIdleAsync(PublishedPayload.Cli(), DataRoot, TimeSpan.FromMinutes(3));

        string session = Directory.GetDirectories(Work).ShouldHaveSingleItem();
        IReadOnlyList<JournalEvent> journal = SessionJournal.ReadAll(Path.Combine(session, SessionJournal.FileName));
        journal.OfType<EncoderRestarted>().ShouldNotBeEmpty("the supervisor brought a new encoder up");
        journal.OfType<GapRecorded>().ShouldNotBeEmpty("the time without an encoder is a gap, never pretended away");
        journal.OfType<SessionFinalized>().ShouldHaveSingleItem();
    }

    [Fact]
    public async Task An_unreachable_destination_keeps_the_recording_and_the_transfer_waits_with_the_reason()
    {
        await ConfigureAsync(@"\\captr-e2e-unreachable.invalid\recordings");
        (await RunAsync("start")).ExitCode.ShouldBe(ExitCodes.Success);
        await Task.Delay(TimeSpan.FromSeconds(4), TestContext.Current.CancellationToken);
        (await RunAsync("stop")).ExitCode.ShouldBe(ExitCodes.Success);
        await EndToEnd.WaitUntilIdleAsync(PublishedPayload.Cli(), DataRoot, TimeSpan.FromMinutes(3));

        JsonElement transfer = default;
        for (int attempt = 0; attempt < 60; attempt++)
        {
            transfer = JsonDocument.Parse((await RunAsync("transfers", "list", "--json")).Stdout)
                .RootElement.GetProperty("transfers").EnumerateArray().ShouldHaveSingleItem();
            if (transfer.GetProperty("lastError").ValueKind == JsonValueKind.String)
            {
                break;
            }

            await Task.Delay(1000, TestContext.Current.CancellationToken);
        }

        transfer.GetProperty("state").GetString().ShouldNotBe("completed");
        transfer.GetProperty("lastError").GetString().ShouldNotBeNullOrWhiteSpace();
        File.Exists(transfer.GetProperty("outputPath").GetString()).ShouldBeTrue("the local recording is untouched");
    }

    [Fact]
    public async Task Starting_in_session_zero_is_refused_with_the_fix_named()
    {
        await ConfigureAsync();

        CliRun run = await RunWithAsync(PublishedPayload.Cli(), ["start"],
            new Dictionary<string, string> { ["CAPTR_SIMULATE_SESSION0"] = "1" });

        run.ExitCode.ShouldBe(ExitCodes.Error, run.ToString());
        run.Stderr.ShouldContain("session 0");
        run.Stderr.ShouldContain("Run only when user is logged on");
    }

    [Fact]
    public async Task A_missing_ffmpeg_refuses_to_start_and_says_to_reinstall()
    {
        await ConfigureAsync();
        string install = Copy("no-ffmpeg", except: [@"ffmpeg\ffmpeg.exe"]);

        CliRun run = await RunWithAsync(Path.Combine(install, "captr.exe"), ["start"]);

        run.ExitCode.ShouldBe(ExitCodes.Error, run.ToString());
        run.Stderr.ShouldContain("ffmpeg.exe was not found");
    }

    [Fact]
    public async Task A_tampered_ffmpeg_is_never_run()
    {
        await ConfigureAsync();
        string install = Copy("tampered", copyInstead: [@"ffmpeg\ffmpeg.exe"]);
        string ffmpeg = Path.Combine(install, "ffmpeg", "ffmpeg.exe");
        await using (var stream = new FileStream(ffmpeg, FileMode.Open, FileAccess.ReadWrite))
        {
            stream.Position = stream.Length / 2;
            int value = stream.ReadByte();
            stream.Position--;
            stream.WriteByte((byte)(value ^ 0x01));
        }

        CliRun run = await RunWithAsync(Path.Combine(install, "captr.exe"), ["start"]);

        run.ExitCode.ShouldBe(ExitCodes.Error, run.ToString());
        run.Stderr.ShouldContain("not the FFmpeg build Captr ships");
    }

    private string Copy(string name, string[]? except = null, string[]? copyInstead = null)
    {
        string folder = EndToEnd.LinkedCopyOfPayload(Path.Combine(_root, name), except, copyInstead);
        _copies.Add(folder);
        return folder;
    }

    /// <summary>The ffmpeg this test's recorder launched (it runs from publish/).</summary>
    private Process OurEncoder()
    {
        string publish = PublishedPayload.Directory();
        foreach (Process process in Process.GetProcessesByName("ffmpeg"))
        {
            try
            {
                if (process.StartTime.ToUniversalTime() >= _startedUtc
                    && process.MainModule?.FileName.StartsWith(publish, StringComparison.OrdinalIgnoreCase) == true)
                {
                    return process;
                }
            }
            catch (Exception exception) when (exception is InvalidOperationException or System.ComponentModel.Win32Exception)
            {
            }

            process.Dispose();
        }

        throw new ShouldAssertException("No encoder launched by this test's recorder was found.");
    }

    private async Task<string> WaitForTransferAsync(string destination)
    {
        for (int attempt = 0; attempt < 120; attempt++)
        {
            string? landed = Directory.Exists(destination)
                ? Directory.GetFiles(destination, "*.mkv").FirstOrDefault()
                : null;
            if (landed is not null)
            {
                return landed;
            }

            await Task.Delay(1000, TestContext.Current.CancellationToken);
        }

        throw new ShouldAssertException("The recording never reached the folder destination: " +
            (await RunAsync("transfers", "list", "--json")).Stdout);
    }
}
