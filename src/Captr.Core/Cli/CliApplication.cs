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
        root.Subcommands.Add(BuildDelivery(jsonOption));
        root.Subcommands.Add(BuildAuth());

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

    // ---- delivery ---------------------------------------------------------------

    private static Command BuildDelivery(Option<bool> jsonOption)
    {
        var command = new Command("delivery", "Inspect and retry transfers to destinations.");

        var list = new Command("list", "Every pending, failed, and completed transfer with attempts and the server's error.");
        list.Options.Add(jsonOption);
        list.SetAction(async (parseResult, cancellationToken) =>
        {
            bool json = parseResult.GetValue(jsonOption);
            return await WithHostAsync(startHostIfNeeded: true, json, async client =>
            {
                ListDeliveriesResponse response = await client.RequestAsync<ListDeliveriesResponse>(
                    IpcKinds.ListDeliveries, null, cancellationToken);
                string human = response.Deliveries.Count == 0
                    ? "No deliveries."
                    : string.Join(Environment.NewLine, response.Deliveries.Select(d =>
                        $"#{d.Id}  {d.State,-12} attempts:{d.Attempts}  {Path.GetFileName(d.OutputPath)} → {d.DestinationName}" +
                        (d.LastError is null ? string.Empty : Environment.NewLine + $"      server said: {d.LastError}")));
                Emit(json, response, human);
                return ExitCodes.Success;
            }, cancellationToken);
        });
        command.Subcommands.Add(list);

        var idArgument = new Argument<long>("id") { Description = "The delivery id from 'captr delivery list'." };
        var retry = new Command("retry", "Put a failed transfer back in the queue.");
        retry.Arguments.Add(idArgument);
        retry.Options.Add(jsonOption);
        retry.SetAction(async (parseResult, cancellationToken) =>
        {
            bool json = parseResult.GetValue(jsonOption);
            return await WithHostAsync(startHostIfNeeded: true, json, async client =>
            {
                StateResponse response = await client.RequestAsync<StateResponse>(
                    IpcKinds.RetryDelivery, new RetryDeliveryRequest(parseResult.GetValue(idArgument)), cancellationToken);
                Emit(json, response, response.Message);
                return ExitCodes.Success;
            }, cancellationToken);
        });
        command.Subcommands.Add(retry);

        return command;
    }

    // ---- auth -------------------------------------------------------------------

    private static Command BuildAuth()
    {
        var command = new Command("auth", "Provision destination credentials (stored in Windows Credential Manager).");
        var nameArgument = new Argument<string>("name") { Description = "Credential name, referenced by a destination's credentialName setting." };

        var set = new Command("set-secret",
            "Store a secret. Reads from stdin when piped, otherwise prompts with masked input. " +
            "NEVER pass secrets as arguments — command lines are visible to every process (SPEC §7).");
        set.Arguments.Add(nameArgument);
        set.SetAction(async (parseResult, cancellationToken) =>
        {
            string name = parseResult.GetValue(nameArgument)!;
            byte[] secret = Console.IsInputRedirected
                ? System.Text.Encoding.UTF8.GetBytes((await Console.In.ReadToEndAsync(cancellationToken)).TrimEnd('\r', '\n'))
                : ReadMasked($"Secret for '{name}': ");
            if (secret.Length == 0)
            {
                await Console.Error.WriteLineAsync("No secret provided; nothing stored.");
                return ExitCodes.Error;
            }

            Captr.Core.Secrets.CredentialVault.Store(name, secret); // Store() wipes the buffer.
            // Write-only confirmation (SPEC §7): stored + when, never the value.
            await Console.Out.WriteLineAsync($"Secret '{name}' stored at {DateTimeOffset.UtcNow.LocalDateTime:yyyy-MM-dd HH:mm}.");
            return ExitCodes.Success;
        });
        command.Subcommands.Add(set);

        var status = new Command("status", "Show whether a secret is stored (never the secret itself).");
        status.Arguments.Add(nameArgument);
        status.SetAction(async (parseResult, cancellationToken) =>
        {
            string name = parseResult.GetValue(nameArgument)!;
            bool exists = Captr.Core.Secrets.CredentialVault.Exists(name);
            await Console.Out.WriteLineAsync(exists
                ? $"A secret named '{name}' is stored."
                : $"No secret named '{name}' is stored.");
            return exists ? ExitCodes.Success : ExitCodes.Error;
        });
        command.Subcommands.Add(status);

        var delete = new Command("delete", "Remove a stored secret.");
        delete.Arguments.Add(nameArgument);
        delete.SetAction(async (parseResult, cancellationToken) =>
        {
            string name = parseResult.GetValue(nameArgument)!;
            bool removed = Captr.Core.Secrets.CredentialVault.Delete(name);
            await Console.Out.WriteLineAsync(removed ? $"Secret '{name}' deleted." : $"No secret named '{name}' was stored.");
            return ExitCodes.Success;
        });
        command.Subcommands.Add(delete);

        return command;
    }

    /// <summary>Masked interactive secret entry — characters echo as '*'.</summary>
    private static byte[] ReadMasked(string prompt)
    {
        Console.Write(prompt);
        var buffer = new List<byte>();
        while (true)
        {
            ConsoleKeyInfo key = Console.ReadKey(intercept: true);
            if (key.Key == ConsoleKey.Enter)
            {
                Console.WriteLine();
                return [.. buffer];
            }

            if (key.Key == ConsoleKey.Backspace)
            {
                if (buffer.Count > 0)
                {
                    buffer.RemoveAt(buffer.Count - 1);
                    Console.Write("\b \b");
                }

                continue;
            }

            buffer.AddRange(System.Text.Encoding.UTF8.GetBytes([key.KeyChar]));
            Console.Write('*');
        }
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
