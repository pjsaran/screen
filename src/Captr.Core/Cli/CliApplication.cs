using System.CommandLine;
using System.Text.Json;

using Captr.Core.Encoders;
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
        root.Subcommands.Add(BuildTransfers(jsonOption));
        root.Subcommands.Add(BuildAuth());
        root.Subcommands.Add(BuildVersion(jsonOption));
        root.Subcommands.Add(BuildDoctor(jsonOption));

        ParseResult parseResult = root.Parse(args);

        // Honour our own documented contract (docs/user-guide/command-line.md): a usage error exits 2.
        // System.CommandLine's default for parse failures is 1, which would make a
        // typo indistinguishable from a real recording failure in a scheduled task.
        if (parseResult.Errors.Count > 0)
        {
            foreach (System.CommandLine.Parsing.ParseError error in parseResult.Errors)
            {
                await Console.Error.WriteLineAsync(error.Message);
            }

            await Console.Error.WriteLineAsync("Run 'captr --help' for usage.");
            return ExitCodes.Usage;
        }

        // System.CommandLine's own handler prints a stack trace and exits 1 - on a
        // scheduled task's history, or to a script parsing --json, that is noise with
        // no next step. Every command's expected failures are mapped where they
        // happen; this catches the rest, says what failed in one line, keeps the
        // details in the log folder, and keeps the documented exit codes.
        try
        {
            return await parseResult.InvokeAsync(new InvocationConfiguration { EnableDefaultExceptionHandler = false });
        }
        catch (HostUnreachableException exception)
        {
            await Console.Error.WriteLineAsync(exception.Message);
            return ExitCodes.HostUnreachable;
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            string? details = WriteErrorDetails(args, exception);
            await Console.Error.WriteLineAsync(
                $"captr {string.Join(' ', args.Take(2))} failed: {exception.Message}" +
                (details is null ? "" : $" (details: {details})"));
            return ExitCodes.Error;
        }
    }

    /// <summary>Keeps the full exception for whoever investigates, beside the host's
    /// logs. Best effort: a failure to write it must not replace the real error.</summary>
    private static string? WriteErrorDetails(string[] args, Exception exception)
    {
        try
        {
            Directory.CreateDirectory(Common.CaptrPaths.Logs);
            string path = Path.Combine(Common.CaptrPaths.Logs, "cli-last-error.txt");
            File.WriteAllText(path,
                $"{DateTimeOffset.Now:O}{Environment.NewLine}captr {string.Join(' ', args)}{Environment.NewLine}{Environment.NewLine}" +
                string.Join(Environment.NewLine, exception.ToString().Split(Environment.NewLine).Select(Diagnostics.SupportBundle.ScrubLine)));
            return path;
        }
        catch (Exception writeFailure) when (writeFailure is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    // ---- start ------------------------------------------------------------------

    private static Command BuildStart(Option<bool> jsonOption)
    {
        var fpsOption = new Option<int?>("--fps")
        {
            Description = "Frame rate override for this session: " +
                string.Join(", ", CaptureRates.All.Select(r => r.FramesPerSecond)) + ".",
        };
        var qualityOption = new Option<string?>("--quality")
        {
            Description = "Quality override: " + string.Join(", ", QualityLevels.All.Select(q => q.Name)) + ".",
        };
        var presetOption = new Option<string?>("--preset")
        {
            Description = "Speed preset override: " + string.Join(", ", SpeedPresets.All.Select(p => p.Name)) + ".",
        };
        var labelOption = new Option<string?>("--label")
        {
            Description = "A label for this recording, available to the naming pattern as {label}.",
        };

        // Documented as usage errors (exit 2). They used to be passed through to the
        // recorder unchecked: --fps 0 surfaced as "no working encoder was found".
        fpsOption.Validators.Add(result =>
        {
            if (result.GetValueOrDefault<int?>() is { } fps && !CaptureRates.IsSupported(fps))
            {
                result.AddError($"--fps {fps} is not a supported frame rate. Use one of: " +
                    string.Join(", ", CaptureRates.All.Select(r => r.FramesPerSecond)) + ".");
            }
        });
        qualityOption.Validators.Add(result =>
        {
            if (result.GetValueOrDefault<string?>() is { } quality && QualityLevels.Find(quality) is null)
            {
                result.AddError($"--quality {quality} is not a quality level. Use one of: " +
                    string.Join(", ", QualityLevels.All.Select(q => q.Name)) + ".");
            }
        });
        presetOption.Validators.Add(result =>
        {
            if (result.GetValueOrDefault<string?>() is { } preset && SpeedPresets.Find(preset) is null)
            {
                result.AddError($"--preset {preset} is not a speed preset. Use one of: " +
                    string.Join(", ", SpeedPresets.All.Select(p => p.Name)) + ".");
            }
        });

        var command = new Command("start", "Start recording. Succeeds (without starting twice) when already recording.");
        command.Options.Add(fpsOption);
        command.Options.Add(qualityOption);
        command.Options.Add(presetOption);
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
                    parseResult.GetValue(presetOption),
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

            // Coverage is stated honestly and always — "continuous" only when it
            // genuinely is (SPEC §6: never present a recording as continuous when
            // it isn't).
            string coverage = status.GapCount == 0
                ? "continuous"
                : FormattableString.Invariant($"{status.GapCount} gap(s), {status.Coverage:P1} covered");

            string human = status.State switch
            {
                // The exit code stays 10 (a contract); the words say what the window
                // says, so a script's log shows why the last recording ended early.
                "idle" when StatusPresentation.ForDisplay(status, null).State == StatusPresentation.Failed =>
                    "Idle — nothing is recording. The last recording stopped on its own: " +
                    StatusPresentation.DescribeFailure(status),
                "idle" => "Idle — nothing is recording.",
                "paused" => $"Paused (session {status.SessionId:N}, elapsed {status.Elapsed:hh\\:mm\\:ss}, {coverage}). Don't forget to resume.",
                _ => $"{status.State} — session {status.SessionId:N}, elapsed {status.Elapsed:hh\\:mm\\:ss}, " +
                     $"{coverage}, encoder {status.Encoder}, {status.FrameRate} fps" +
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
                    $"{Common.ByteSize.Format(r.TotalBytes),9}  gaps:{r.GapCount}  {(r.Finalized ? "finalised" : "NOT FINALISED")}  {r.Folder}"));
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

        // --- resend: queue the outputs to every enabled destination (SPEC §9) ----
        var resendFolder = new Argument<string>("folder") { Description = "The session's working folder." };
        var resend = new Command("resend", "Send a finished recording to every enabled destination again.");
        resend.Arguments.Add(resendFolder);
        resend.Options.Add(jsonOption);
        resend.SetAction(async (parseResult, cancellationToken) =>
        {
            bool json = parseResult.GetValue(jsonOption);
            return await WithHostAsync(startHostIfNeeded: true, json, async client =>
            {
                ResendResponse response = await client.RequestAsync<ResendResponse>(
                    // Resolved HERE: the recorder runs in its own directory, so a relative
                    // path used to be looked up there and reported "no integrity record".
                    IpcKinds.Resend, new ResendRequest(Path.GetFullPath(parseResult.GetValue(resendFolder)!)), cancellationToken);
                Emit(json, response, response.Message);
                return response.Queued > 0 ? ExitCodes.Success : ExitCodes.Error;
            }, cancellationToken);
        });
        command.Subcommands.Add(resend);

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
            await Console.Out.WriteLineAsync(JsonSerializer.Serialize(settings, SettingsStore.JsonOptions));
            return ExitCodes.Success;
        });
        command.Subcommands.Add(get);

        // ---- init ---------------------------------------------------------------
        // Used by the installer so a fresh machine has a real settings.json rather
        // than nothing until the user first opens Settings and saves. Doing it
        // through the CLI rather than writing JSON from the installer script means
        // the defaults, the schema version, and the validation all come from ONE
        // place and cannot drift from the product.
        var workingFolderOption = new Option<string?>("--working-folder")
        {
            Description = "Where recordings are written. Omit to use the default.",
        };

        var init = new Command(
            "init",
            "Create settings.json with defaults if it does not exist yet. Never overwrites an existing file.");
        init.Options.Add(workingFolderOption);
        init.Options.Add(jsonOption);
        init.SetAction(async (parseResult, cancellationToken) =>
        {
            bool json = parseResult.GetValue(jsonOption);
            string path = SettingsStore.DefaultSettingsPath();

            // Load first: it moves an older Captr's roaming settings across and
            // restores settings.json from its previous version if the file is gone.
            // Checking only whether the file existed - which the installer runs on
            // every install - used to write fresh defaults over both, after which
            // neither recovery could ever happen.
            new SettingsStore().Load();

            if (File.Exists(path))
            {
                // Idempotent BY DESIGN: an upgrade re-runs this, and a user's
                // configuration surviving an upgrade matters far more than defaults
                // being fresh.
                await Console.Out.WriteLineAsync(json
                    ? JsonSerializer.Serialize(new { path, created = false }, JsonOptions)
                    : $"Settings already exist at {path}; left unchanged.");
                return ExitCodes.Success;
            }

            CaptrSettings settings = CaptrSettings.CreateDefault();
            if (parseResult.GetValue(workingFolderOption) is { Length: > 0 } workingFolder)
            {
                settings = settings with { WorkingFolder = workingFolder };
            }

            try
            {
                new SettingsStore().Save(settings);
            }
            catch (SettingsValidationException exception)
            {
                await Console.Error.WriteLineAsync(
                    "Could not create settings: " +
                    string.Join("; ", exception.Errors.Select(e => $"{e.Field} — {e.Message}")));
                return ExitCodes.Error;
            }

            await Console.Out.WriteLineAsync(json
                ? JsonSerializer.Serialize(new { path, created = true }, JsonOptions)
                : $"Created {path} with default settings.");
            return ExitCodes.Success;
        });
        command.Subcommands.Add(init);

        var keyArgument = new Argument<string>("key") { Description = "Setting name, e.g. frameRate, quality, speedPreset, workingFolder." };
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
                return await SaveThroughRecorderAsync(store, updated, "Saved.", cancellationToken);
            }
            catch (Exception exception) when (
                exception is SettingsValidationException or ArgumentException or IpcRequestException
                    or HostUnreachableException or ProtocolMismatchException)
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
                CaptrSettings current = store.Load();
                CaptrSettings imported = store.Import(json);

                // Say what the import changes where recordings GO. A shared settings
                // file can add a destination or move the working folder - and with
                // it every future recording - so it must never do that unannounced.
                // Then save it the way `set` does: through the recorder when one is
                // running, so the while-recording lock applies (it used to bypass it,
                // and moving the working folder mid-recording left an interrupted
                // session where recovery would never look).
                foreach (string change in SettingsEditor.DescribeWhereRecordingsGo(current, imported))
                {
                    await Console.Out.WriteLineAsync(change);
                }

                return await SaveThroughRecorderAsync(store, imported, "Imported and saved.", cancellationToken);
            }
            catch (Exception exception) when (exception is SettingsValidationException or JsonException or IOException
                                                  or UnauthorizedAccessException or Settings.Migrations.SettingsMigrationException
                                                  or IpcRequestException or HostUnreachableException or ProtocolMismatchException
                                                  or NotSupportedException or InvalidOperationException)
            {
                await Console.Error.WriteLineAsync(exception.Message);
                return ExitCodes.Error;
            }
        });
        command.Subcommands.Add(import);

        return command;
    }

    // ---- doctor -----------------------------------------------------------------

    /// <summary>
    /// The same health checks the Diagnostics page shows, on the command line.
    /// </summary>
    /// <remarks>
    /// Exits 1 when any check is a Problem, so a script or a deployment step can ask
    /// "is this machine ready to record?" and branch on the answer instead of parsing
    /// text. Attention-level findings do not fail: they are worth reading, not worth
    /// stopping for.
    /// </remarks>
    private static Command BuildDoctor(Option<bool> jsonOption)
    {
        var command = new Command(
            "doctor",
            "Check this installation and report anything that would stop it recording or transferring.");
        command.Options.Add(jsonOption);
        command.SetAction(async (parseResult, cancellationToken) =>
        {
            IReadOnlyList<Diagnostics.HealthCheck> checks =
                await Task.Run(Diagnostics.HealthReport.Run, cancellationToken);

            if (parseResult.GetValue(jsonOption))
            {
                await Console.Out.WriteLineAsync(JsonSerializer.Serialize(checks, JsonOptions));
            }
            else
            {
                foreach (Diagnostics.HealthCheck check in checks)
                {
                    await Console.Out.WriteLineAsync(
                        $"{check.Level.ToString().ToUpperInvariant(),-9} {check.Name,-14} {check.Finding}");
                    if (check.WhatToDo is { } fix)
                    {
                        await Console.Out.WriteLineAsync($"{new string(' ', 24)}{fix}");
                    }
                }
            }

            return checks.Any(c => c.Level == Diagnostics.HealthLevel.Problem)
                ? ExitCodes.Error
                : ExitCodes.Success;
        });
        return command;
    }

    // ---- version ----------------------------------------------------------------

    private static Command BuildVersion(Option<bool> jsonOption)
    {
        var command = new Command(
            "version",
            "Show exactly which build this is: version, source commit, bundled FFmpeg build, and .NET runtime.");
        command.Options.Add(jsonOption);
        command.SetAction(async (parseResult, cancellationToken) =>
        {
            Captr.Core.Common.BuildInfo info = Captr.Core.Common.BuildInfo.Current();
            if (parseResult.GetValue(jsonOption))
            {
                await Console.Out.WriteLineAsync(JsonSerializer.Serialize(info, JsonOptions));
            }
            else
            {
                await Console.Out.WriteLineAsync(info.ToDisplayText());
            }

            return ExitCodes.Success;
        });
        return command;
    }

    // ---- transfer ---------------------------------------------------------------

    private static Command BuildTransfers(Option<bool> jsonOption)
    {
        var command = new Command("transfers", "Inspect, retry, and stop transfers to destinations.");

        var list = new Command("list", "Every pending, failed, and completed transfer with attempts and the server's error.");
        list.Options.Add(jsonOption);
        list.SetAction(async (parseResult, cancellationToken) =>
        {
            bool json = parseResult.GetValue(jsonOption);
            return await WithHostAsync(startHostIfNeeded: true, json, async client =>
            {
                ListTransfersResponse response = await client.RequestAsync<ListTransfersResponse>(
                    IpcKinds.ListTransfers, null, cancellationToken);
                string human = response.Transfers.Count == 0
                    ? "No transfers."
                    : string.Join(Environment.NewLine, response.Transfers.Select(d =>
                        $"#{d.Id}  {d.State,-12} attempts:{d.Attempts}  {Path.GetFileName(d.OutputPath)} → {d.DestinationName}" +
                        (d.LastError is null ? string.Empty : Environment.NewLine + $"      server said: {d.LastError}")));
                Emit(json, response, human);
                return ExitCodes.Success;
            }, cancellationToken);
        });
        command.Subcommands.Add(list);

        var idArgument = new Argument<long>("id") { Description = "The transfer id from 'captr transfers list'." };
        var retry = new Command("retry", "Put a failed transfer back in the queue.");
        retry.Arguments.Add(idArgument);
        retry.Options.Add(jsonOption);
        retry.SetAction(async (parseResult, cancellationToken) =>
        {
            bool json = parseResult.GetValue(jsonOption);
            return await WithHostAsync(startHostIfNeeded: true, json, async client =>
            {
                StateResponse response = await client.RequestAsync<StateResponse>(
                    IpcKinds.RetryTransfer, new RetryTransferRequest(parseResult.GetValue(idArgument)), cancellationToken);
                Emit(json, response, response.Message);
                return response.State == Hosting.HostService.NotFound ? ExitCodes.Error : ExitCodes.Success;
            }, cancellationToken);
        });
        command.Subcommands.Add(retry);

        var stopId = new Argument<long>("id") { Description = "The transfer id from 'captr transfers list'." };
        var stop = new Command(
            "stop",
            "Stop a queued or running transfer. It stays in the list and can be retried; nothing local is deleted.");
        stop.Arguments.Add(stopId);
        stop.Options.Add(jsonOption);
        stop.SetAction(async (parseResult, cancellationToken) =>
        {
            bool json = parseResult.GetValue(jsonOption);
            return await WithHostAsync(startHostIfNeeded: true, json, async client =>
            {
                StateResponse response = await client.RequestAsync<StateResponse>(
                    IpcKinds.CancelTransfer, new CancelTransferRequest(parseResult.GetValue(stopId)), cancellationToken);
                Emit(json, response, response.Message);
                return response.State == Hosting.HostService.NotFound ? ExitCodes.Error : ExitCodes.Success;
            }, cancellationToken);
        });
        command.Subcommands.Add(stop);

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
            byte[]? secret = Console.IsInputRedirected
                ? await ReadPipedSecretAsync(cancellationToken)
                : ReadMasked($"Secret for '{name}': ");
            if (secret is null)
            {
                await Console.Error.WriteLineAsync(
                    $"The secret is longer than {MaxSecretChars} characters, which no client secret is; nothing stored. " +
                    "Check what is being piped in.");
                return ExitCodes.Error;
            }

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

    /// <summary>Longer than any client secret or certificate password; anything
    /// beyond it is a mistake in what was piped in, not a secret.</summary>
    private const int MaxSecretChars = 4096;

    /// <summary>
    /// Masked interactive secret entry — characters echo as '*'.
    /// </summary>
    /// <remarks>
    /// Characters are kept as characters and encoded once, at the end: each key used
    /// to be UTF-8-encoded on its own, so a character outside the Basic Multilingual
    /// Plane (two UTF-16 halves) became two replacement characters, and Backspace
    /// removed one BYTE of a multi-byte character. The buffer is wiped after use.
    /// With no console to read keys from (a scheduled task without piped input) this
    /// says so instead of failing with an unhandled exception.
    /// </remarks>
    private static byte[] ReadMasked(string prompt)
    {
        char[] buffer = new char[MaxSecretChars];
        int length = 0;
        try
        {
            Console.Write(prompt);
            while (true)
            {
                ConsoleKeyInfo key = Console.ReadKey(intercept: true);
                if (key.Key == ConsoleKey.Enter)
                {
                    Console.WriteLine();
                    return System.Text.Encoding.UTF8.GetBytes(buffer, 0, length);
                }

                if (key.Key == ConsoleKey.Backspace)
                {
                    if (length > 0)
                    {
                        // A surrogate pair is one character to the person typing.
                        length -= length > 1 && char.IsLowSurrogate(buffer[length - 1]) && char.IsHighSurrogate(buffer[length - 2]) ? 2 : 1;
                        Console.Write("\b \b");
                    }

                    continue;
                }

                if (key.KeyChar == '\0' || length == buffer.Length)
                {
                    continue;
                }

                buffer[length++] = key.KeyChar;
                if (!char.IsHighSurrogate(key.KeyChar))
                {
                    Console.Write('*');
                }
            }
        }
        catch (InvalidOperationException)
        {
            Console.Error.WriteLine();
            Console.Error.WriteLine("There is no console to type a secret into. Pipe it in instead, for example: " +
                                    "Get-Content secret.txt | captr auth set-secret <name>");
            return [];
        }
        finally
        {
            Array.Clear(buffer);
        }
    }

    /// <summary>A piped secret, read up to <see cref="MaxSecretChars"/> (null when
    /// longer), without the trailing newline, the character buffer wiped after.</summary>
    private static async Task<byte[]?> ReadPipedSecretAsync(CancellationToken cancellationToken)
    {
        char[] buffer = new char[MaxSecretChars + 1];
        try
        {
            int length = 0;
            int read;
            while (length < buffer.Length
                   && (read = await Console.In.ReadAsync(buffer.AsMemory(length), cancellationToken)) > 0)
            {
                length += read;
            }

            if (length > MaxSecretChars)
            {
                return null;
            }

            while (length > 0 && buffer[length - 1] is '\r' or '\n')
            {
                length--;
            }

            return System.Text.Encoding.UTF8.GetBytes(buffer, 0, length);
        }
        finally
        {
            Array.Clear(buffer);
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

    /// <summary>
    /// Saves settings through the running recorder when there is one, so the SPEC §8
    /// lock is enforced against the live session; with no recorder there is no
    /// recording, and writing the file directly is equivalent (and no recorder is
    /// started just to change a setting).
    /// </summary>
    private static async Task<int> SaveThroughRecorderAsync(
        SettingsStore store, CaptrSettings updated, string doneMessage, CancellationToken cancellationToken)
    {
        await using IpcClient? client = await IpcClient.ConnectAsync(
            ClientVersion(), startHostIfNeeded: false, null, cancellationToken);
        if (client is null)
        {
            store.Save(updated);
            await Console.Out.WriteLineAsync(doneMessage);
            return ExitCodes.Success;
        }

        SetSettingsResponse response = await client.RequestAsync<SetSettingsResponse>(
            IpcKinds.SetSettings, new SetSettingsRequest(updated), cancellationToken);
        if (!response.Applied)
        {
            await Console.Error.WriteLineAsync(response.Message);
            return ExitCodes.Error;
        }

        await Console.Out.WriteLineAsync(response.Message);
        return ExitCodes.Success;
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
            IReadOnlyList<RecordingSummary> summaries = RecordingCatalog.Scan(
                new SettingsStore().Load().WorkingFolder, cancellationToken);
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
