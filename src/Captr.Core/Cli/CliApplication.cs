using System.CommandLine;
using System.Text.Json;

using Captr.Core.Ipc;
using Captr.Core.Sessions;
using Captr.Core.Settings;

namespace Captr.Core.Cli;

/// <summary>
/// The complete captr command tree (SPEC §10). Owns argument parsing, the
/// human/JSON output split, and the mapping of every outcome to a documented exit
/// code. Talks to the recording host over IPC; starts one only for commands that
/// summon work.
/// </summary>
public static class CliApplication
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true,
    };

    /// <summary>Runs the command line and returns the process exit code.</summary>
    public static async Task<int> RunAsync(string[] args)
    {
        var jsonOption = new Option<bool>("--json") { Description = "Machine-readable JSON output on stdout." };

        var root = new RootCommand("Captr — lightweight screen recorder. Records displays into crash-safe segmented video.");
        root.Options.Add(jsonOption);

        root.Subcommands.Add(BuildStart(jsonOption));
        root.Subcommands.Add(BuildSimpleHostCommand("stop", "Stop the current recording (succeeds even when idle).", jsonOption,
            async (client, cancellationToken) =>
            {
                StopResponse response = await client.RequestAsync<StopResponse>(IpcKinds.Stop, null, cancellationToken);
                return (response, response.WasRecording
                    ? "Stopping. Finalisation continues in the background; run 'captr status' to watch."
                    : "Nothing was recording — already idle.", ExitCodes.Success);
            }));
        root.Subcommands.Add(BuildSimpleHostCommand("pause", "Pause the current recording (the pause is journaled).", jsonOption,
            async (client, cancellationToken) =>
            {
                StateResponse response = await client.RequestAsync<StateResponse>(IpcKinds.Pause, null, cancellationToken);
                return (response, response.Message, ExitCodes.Success);
            }));
        root.Subcommands.Add(BuildSimpleHostCommand("resume", "Resume a paused recording into a new segment.", jsonOption,
            async (client, cancellationToken) =>
            {
                StateResponse response = await client.RequestAsync<StateResponse>(IpcKinds.Resume, null, cancellationToken);
                return (response, response.Message, ExitCodes.Success);
            }));
        root.Subcommands.Add(BuildStatus(jsonOption));
        root.Subcommands.Add(BuildRecordings(jsonOption));
        root.Subcommands.Add(BuildRecover(jsonOption));
        root.Subcommands.Add(BuildSettings(jsonOption));

        return await root.Parse(args).InvokeAsync();
    }

    // ---- start ------------------------------------------------------------------

    private static Command BuildStart(Option<bool> jsonOption)
    {
        var fpsOption = new Option<int?>("--fps") { Description = "Frame rate override for this session." };
        var qualityOption = new Option<string?>("--quality") { Description = "Quality preset override (archival, sharp-text, balanced, compact)." };
        var labelOption = new Option<string?>("--label") { Description = "A label included in the output file name." };

        var command = new Command("start", "Start recording. Succeeds (without starting twice) when already recording.");
        command.Options.Add(fpsOption);
        command.Options.Add(qualityOption);
        command.Options.Add(labelOption);
        command.Options.Add(jsonOption);

        command.SetAction(async (parseResult, cancellationToken) =>
        {
            bool json = parseResult.GetValue(jsonOption);
            return await WithHostAsync(startHostIfNeeded: true, json, async client =>
            {
                var request = new StartRequest(
                    parseResult.GetValue(fpsOption),
                    parseResult.GetValue(qualityOption),
                    parseResult.GetValue(labelOption));
                StartResponse response = await client.RequestAsync<StartResponse>(IpcKinds.Start, request, cancellationToken);
                Emit(json, response, response.Message);
                return ExitCodes.Success;
            }, cancellationToken);
        });
        return command;
    }

    // ---- status -----------------------------------------------------------------

    private static Command BuildStatus(Option<bool> jsonOption)
    {
        var command = new Command("status", "Show what the recorder is doing. Exit code: 0 recording, 10 idle, 11 paused.");
        command.Options.Add(jsonOption);
        command.SetAction(async (parseResult, cancellationToken) =>
        {
            bool json = parseResult.GetValue(jsonOption);
            await using IpcClient? client = await IpcClient.ConnectAsync(
                ClientVersion(), startHostIfNeeded: false, null, cancellationToken);

            StatusResponse status = client is null
                ? new StatusResponse("idle", null, null, null, null, null, null, null, null)
                : await client.RequestAsync<StatusResponse>(IpcKinds.Status, null, cancellationToken);

            string human = status.State switch
            {
                "idle" => "Idle — nothing is recording.",
                "paused" => $"Paused (session {status.SessionId:N}, elapsed {status.Elapsed:hh\\:mm\\:ss}). Don't forget to resume.",
                _ => $"{status.State} — session {status.SessionId:N}, elapsed {status.Elapsed:hh\\:mm\\:ss}, " +
                     $"encoder {status.Encoder}, {status.FrameRate} fps" +
                     (status.DiskMinutesRemaining is { } minutes ? $", ~{minutes:F0} min of disk left" : string.Empty),
            };
            Emit(json, status, human);

            return status.State switch
            {
                "idle" => ExitCodes.Idle,
                "paused" => ExitCodes.Paused,
                _ => ExitCodes.Success,
            };
        });
        return command;
    }

    // ---- recordings -------------------------------------------------------------

    private static Command BuildRecordings(Option<bool> jsonOption)
    {
        var command = new Command("recordings", "Inspect past recordings.");

        var list = new Command("list", "List recorded sessions in the working folder.");
        list.Options.Add(jsonOption);
        list.SetAction(async (parseResult, cancellationToken) =>
        {
            bool json = parseResult.GetValue(jsonOption);
            ListRecordingsResponse response = await LocalOperations.ListRecordingsAsync(cancellationToken);
            string human = response.Recordings.Count == 0
                ? "No recordings found."
                : string.Join(Environment.NewLine, response.Recordings.Select(r =>
                    $"{r.StartedUtc.ToLocalTime():yyyy-MM-dd HH:mm}  {r.RecordedSpan:hh\\:mm\\:ss}  " +
                    $"{r.TotalBytes / 1_000_000.0:F0} MB  gaps:{r.GapCount}  {(r.Finalized ? "finalised" : "NOT FINALISED")}  {r.Folder}"));
            Emit(json, response, human);
            return ExitCodes.Success;
        });
        command.Subcommands.Add(list);

        var folderArgument = new Argument<string>("folder") { Description = "The session's working folder." };
        var verify = new Command("verify", "Recompute hashes and prove a recording is unaltered on disk.");
        verify.Arguments.Add(folderArgument);
        verify.Options.Add(jsonOption);
        verify.SetAction(async (parseResult, cancellationToken) =>
        {
            bool json = parseResult.GetValue(jsonOption);
            string folder = parseResult.GetValue(folderArgument)!;
            VerifyResponse response = await LocalOperations.VerifyAsync(folder, cancellationToken);
            Emit(json, response, response.Intact
                ? "Intact — every hash matches the integrity record."
                : "PROBLEMS FOUND:" + Environment.NewLine + string.Join(Environment.NewLine, response.Problems.Select(p => "  " + p)));
            return response.Intact ? ExitCodes.Success : ExitCodes.Error;
        });
        command.Subcommands.Add(verify);

        return command;
    }

    // ---- recover ----------------------------------------------------------------

    private static Command BuildRecover(Option<bool> jsonOption)
    {
        var command = new Command("recover", "Finalise any sessions interrupted by a crash or power loss.");
        command.Options.Add(jsonOption);
        command.SetAction(async (parseResult, cancellationToken) =>
        {
            bool json = parseResult.GetValue(jsonOption);
            return await WithHostAsync(startHostIfNeeded: true, json, async client =>
            {
                RecoverResponse response = await client.RequestAsync<RecoverResponse>(IpcKinds.Recover, null, cancellationToken);
                string human = response.Recovered.Count == 0
                    ? "Nothing needed recovery."
                    : string.Join(Environment.NewLine, response.Recovered.Select(r =>
                        r.Succeeded
                            ? $"Recovered {r.Folder}: {r.RecoveredDuration:hh\\:mm\\:ss} of footage."
                            : $"FAILED to recover {r.Folder}: {r.FailureReason}"));
                Emit(json, response, human);
                return response.Recovered.All(r => r.Succeeded) ? ExitCodes.Success : ExitCodes.Error;
            }, cancellationToken);
        });
        return command;
    }

    // ---- settings ---------------------------------------------------------------

    private static Command BuildSettings(Option<bool> jsonOption)
    {
        var command = new Command("settings", "Read and change configuration.");

        var get = new Command("get", "Print the current settings.");
        get.Options.Add(jsonOption);
        get.SetAction(async (parseResult, cancellationToken) =>
        {
            CaptrSettings settings = new SettingsStore().Load();
            await Console.Out.WriteLineAsync(JsonSerializer.Serialize(settings, JsonOptions));
            return ExitCodes.Success;
        });
        command.Subcommands.Add(get);

        var keyArgument = new Argument<string>("key") { Description = "Setting name, e.g. frameRate, qualityPreset, workingFolder." };
        var valueArgument = new Argument<string>("value") { Description = "New value." };
        var set = new Command("set", "Change one setting (validated before saving).");
        set.Arguments.Add(keyArgument);
        set.Arguments.Add(valueArgument);
        set.SetAction(async (parseResult, cancellationToken) =>
        {
            try
            {
                var store = new SettingsStore();
                CaptrSettings updated = SettingsEditor.Apply(store.Load(), parseResult.GetValue(keyArgument)!, parseResult.GetValue(valueArgument)!);
                store.Save(updated);
                await Console.Out.WriteLineAsync("Saved.");
                return ExitCodes.Success;
            }
            catch (Exception exception) when (exception is SettingsValidationException or ArgumentException)
            {
                await Console.Error.WriteLineAsync(exception.Message);
                return ExitCodes.Error;
            }
        });
        command.Subcommands.Add(set);

        var export = new Command("export", "Print settings as a portable document (credentials are never included).");
        export.SetAction(async (parseResult, cancellationToken) =>
        {
            await Console.Out.WriteLineAsync(SettingsStore.Export(new SettingsStore().Load()));
            return ExitCodes.Success;
        });
        command.Subcommands.Add(export);

        var fileArgument = new Argument<string>("file") { Description = "Path of an exported settings file." };
        var import = new Command("import", "Import a settings document (stored credentials are untouched).");
        import.Arguments.Add(fileArgument);
        import.SetAction(async (parseResult, cancellationToken) =>
        {
            try
            {
                var store = new SettingsStore();
                string json = await File.ReadAllTextAsync(parseResult.GetValue(fileArgument)!, cancellationToken);
                store.Save(store.Import(json));
                await Console.Out.WriteLineAsync("Imported and saved.");
                return ExitCodes.Success;
            }
            catch (Exception exception) when (exception is SettingsValidationException or JsonException or IOException)
            {
                await Console.Error.WriteLineAsync(exception.Message);
                return ExitCodes.Error;
            }
        });
        command.Subcommands.Add(import);

        return command;
    }

    // ---- plumbing ---------------------------------------------------------------

    private static Command BuildSimpleHostCommand(
        string name, string description, Option<bool> jsonOption,
        Func<IpcClient, CancellationToken, Task<(object Response, string Human, int ExitCode)>> operation)
    {
        var command = new Command(name, description);
        command.Options.Add(jsonOption);
        command.SetAction(async (parseResult, cancellationToken) =>
        {
            bool json = parseResult.GetValue(jsonOption);
            await using IpcClient? client = await IpcClient.ConnectAsync(
                ClientVersion(), startHostIfNeeded: false, null, cancellationToken);
            if (client is null)
            {
                // No host = nothing recording: for stop/pause/resume that is a
                // successful no-op (SPEC §10), reported honestly.
                Emit(json, new StateResponse("idle", "No recording host is running — already idle."),
                    "No recording host is running — already idle.");
                return ExitCodes.Success;
            }

            (object response, string human, int exitCode) = await operation(client, cancellationToken);
            Emit(json, response, human);
            return exitCode;
        });
        return command;
    }

    /// <summary>Connects (optionally summoning a host) and maps connection failures
    /// to the documented exit codes.</summary>
    private static async Task<int> WithHostAsync(
        bool startHostIfNeeded, bool json, Func<IpcClient, Task<int>> operation, CancellationToken cancellationToken)
    {
        try
        {
            await using IpcClient? client = await IpcClient.ConnectAsync(
                ClientVersion(), startHostIfNeeded, null, cancellationToken);
            if (client is null)
            {
                await Console.Error.WriteLineAsync("No recording host is running.");
                return ExitCodes.HostUnreachable;
            }

            return await operation(client);
        }
        catch (HostUnreachableException exception)
        {
            await Console.Error.WriteLineAsync(exception.Message);
            return ExitCodes.HostUnreachable;
        }
        catch (ProtocolMismatchException exception)
        {
            await Console.Error.WriteLineAsync(exception.Message);
            return ExitCodes.Error;
        }
        catch (IpcRequestException exception)
        {
            await Console.Error.WriteLineAsync(exception.Message);
            return ExitCodes.Error;
        }
    }

    /// <summary>Results to stdout — JSON or human, never both (SPEC §10).</summary>
    private static void Emit(bool json, object response, string human) =>
        Console.Out.WriteLine(json ? JsonSerializer.Serialize(response, JsonOptions) : human);

    private static string ClientVersion() =>
        typeof(CliApplication).Assembly.GetName().Version?.ToString() ?? "0";

    /// <summary>Host-free operations reading the working folder directly.</summary>
    private static class LocalOperations
    {
        public static async Task<ListRecordingsResponse> ListRecordingsAsync(CancellationToken cancellationToken)
        {
            var summaries = new List<RecordingSummary>();
            string root = new SettingsStore().Load().WorkingFolder;
            if (Directory.Exists(root))
            {
                foreach (string folder in Directory.GetDirectories(root).OrderDescending(StringComparer.Ordinal))
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    string journalPath = Path.Combine(folder, SessionJournal.FileName);
                    if (!File.Exists(journalPath))
                    {
                        continue;
                    }

                    IReadOnlyList<JournalEvent> events = SessionJournal.ReadAll(journalPath);
                    if (events.OfType<SessionStarted>().FirstOrDefault() is not { } start)
                    {
                        continue;
                    }

                    SessionFinalized? finalized = events.OfType<SessionFinalized>().FirstOrDefault();
                    long bytes = Directory.EnumerateFiles(folder, "*.mkv").Sum(f => new FileInfo(f).Length);
                    summaries.Add(new RecordingSummary(
                        folder, start.TimestampUtc, finalized?.RecordedSpan ?? TimeSpan.Zero,
                        bytes, finalized?.GapCount ?? 0, finalized is not null));
                }
            }

            return await Task.FromResult(new ListRecordingsResponse(summaries));
        }

        public static async Task<VerifyResponse> VerifyAsync(string folder, CancellationToken cancellationToken)
        {
            IntegrityRecord? record = IntegrityRecord.ReadOrNull(folder);
            if (record is null)
            {
                return new VerifyResponse(false, [$"No integrity record found in {folder} — was the session finalised?"]);
            }

            IReadOnlyList<string> problems = await record.VerifyAsync(folder, cancellationToken);
            return new VerifyResponse(problems.Count == 0, problems);
        }
    }
}
