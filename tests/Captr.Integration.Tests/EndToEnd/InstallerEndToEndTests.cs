using System.Diagnostics;
using System.Text.Json;
using System.Text.Json.Nodes;

using Captr.Core.Cli;
using Captr.Core.Common;

using Microsoft.Win32;

using Shouldly;

namespace Captr.Integration.Tests.EndToEnd;

/// <summary>
/// The real installer, run silently the way Intune and scripted rollouts run it:
/// install, first run, record, transfer, uninstall; an upgrade over a running
/// recording; a refused downgrade; and an upgrade from the previous release.
/// </summary>
/// <remarks>
/// <para>
/// Every install here is PER-USER (<c>/CURRENTUSER</c>) into a temp folder, with no
/// Start Menu shortcut and no PATH entry, and with its own data root — nothing is
/// installed machine-wide and the developer's own settings are never read. The one
/// registry key it writes is Captr's per-user uninstall entry, which the uninstall
/// at the end of every test removes.
/// </para>
/// <para>
/// Category "Installer" runs only on purpose (<c>build.ps1 -TestFilter
/// Category=Installer</c>), after <c>build.ps1 -Installer</c>: it needs the setup
/// program built from the current source, and a desktop to record.
/// </para>
/// </remarks>
[Trait("Category", "Installer")]
public sealed class InstallerEndToEndTests : IAsyncDisposable
{
    /// <summary>Captr's per-user uninstall entry (the AppId in captr.iss).</summary>
    private const string UninstallKey =
        @"Software\Microsoft\Windows\CurrentVersion\Uninstall\{7E1C7C2E-5A15-4F60-92D1-0CAB70000001}_is1";

    /// <summary>Names the previous release's setup program for the upgrade test.</summary>
    private const string PreviousInstallerVariable = "CAPTR_PREVIOUS_INSTALLER";

    private static readonly string[] RealSettingsFiles = ["settings.json", "settings.json.bak"];

    private readonly string _root = Directory.CreateTempSubdirectory("captr-install-e2e-").FullName;
    private readonly DateTime _startedUtc = DateTime.UtcNow;
    private int _logs;

    public InstallerEndToEndTests()
    {
        Registry.CurrentUser.OpenSubKey(UninstallKey).ShouldBeNull(
            "Captr is already installed for this Windows account. These tests install and uninstall it, " +
            "so they refuse to run over a real installation.");
    }

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public async ValueTask DisposeAsync()
    {
        EndToEnd.KillHostsStartedSince(_startedUtc, InstallDir);
        if (File.Exists(Path.Combine(InstallDir, "unins000.exe")))
        {
            await UninstallAsync();
        }

        EndToEnd.DeleteQuietly(_root);
    }

    private string InstallDir => Path.Combine(_root, "app");

    private string DataRoot => Path.Combine(_root, "data");

    private string Work => Path.Combine(_root, "work");

    private string InstalledCli => Path.Combine(InstallDir, "captr.exe");

    private Task<CliRun> CaptrAsync(params string[] arguments) =>
        EndToEnd.RunCliAsync(InstalledCli, arguments, DataRoot);

    /// <summary>The setup program built from this source.</summary>
    private static string CurrentInstaller()
    {
        string publish = PublishedPayload.Directory();
        string artifacts = Path.Combine(Path.GetDirectoryName(publish)!, "artifacts");
        FileInfo? setup = Directory.Exists(artifacts)
            ? new DirectoryInfo(artifacts).EnumerateFiles("captr-setup-*.exe").MaxBy(file => file.LastWriteTimeUtc)
            : null;

        setup.ShouldNotBeNull("No installer in artifacts/. Run: pwsh build/build.ps1 -Installer");
        setup.LastWriteTimeUtc.ShouldBeGreaterThanOrEqualTo(
            File.GetLastWriteTimeUtc(Path.Combine(publish, "Captr.Core.dll")),
            "the installer is older than publish/, so it would install an old build. Run: pwsh build/build.ps1 -Installer");
        return setup.FullName;
    }

    /// <summary>Runs a setup or uninstall program silently, with this test's data
    /// root, and returns its exit code. The log goes next to the test's files.</summary>
    private async Task<(int ExitCode, string Log)> RunSilentlyAsync(string program, params string[] extra)
    {
        string log = Path.Combine(_root, $"setup-{++_logs}.log");
        var startInfo = new ProcessStartInfo(program)
        {
            UseShellExecute = false,
            Arguments = string.Join(' ', ["/VERYSILENT", "/SUPPRESSMSGBOXES", "/NORESTART", $"/LOG=\"{log}\"", .. extra]),
        };
        startInfo.Environment[CaptrPaths.DataRootVariable] = DataRoot;

        using Process process = Process.Start(startInfo)!;
        using var deadline = new CancellationTokenSource(TimeSpan.FromMinutes(5));
        try
        {
            await process.WaitForExitAsync(deadline.Token);
        }
        catch (OperationCanceledException)
        {
            process.Kill(entireProcessTree: true);
            throw new TimeoutException($"{Path.GetFileName(program)} {startInfo.Arguments} did not finish: a silent run must never wait for anyone.");
        }

        return (process.ExitCode, await ReadLogAsync(log));
    }

    /// <summary>Reads a setup log, sharing it with whoever may still be writing it:
    /// the uninstaller hands over to a copy of itself that keeps the log open until
    /// it has finished.</summary>
    private static async Task<string> ReadLogAsync(string log)
    {
        if (!File.Exists(log))
        {
            return "";
        }

        await using var stream = new FileStream(log, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        using var reader = new StreamReader(stream);
        return await reader.ReadToEndAsync(Ct);
    }

    private Task<(int ExitCode, string Log)> InstallAsync(string installer, params string[] extra) =>
        RunSilentlyAsync(installer,
        [
            "/CURRENTUSER", $"/DIR=\"{InstallDir}\"", "/MERGETASKS=\"!shortcuts,!addtopath\"",
            // With the trailing backslash an administrator naturally types, which used
            // to swallow the closing quote when setup passed it on to captr.
            $"/WORKINGFOLDER=\"{Work}\\\"",
            .. extra,
        ]);

    /// <summary>Uninstalls and waits until it is really done: the uninstaller copies
    /// itself to a temp folder and exits at once, and the copy does the work.</summary>
    private async Task UninstallAsync(params string[] extra)
    {
        (int exitCode, string log) = await RunSilentlyAsync(Path.Combine(InstallDir, "unins000.exe"), extra);
        exitCode.ShouldBe(0, log);

        string logPath = Path.Combine(_root, $"setup-{_logs}.log");
        DateTime giveUp = DateTime.UtcNow.AddMinutes(3);
        while (!log.Contains("Log closed", StringComparison.Ordinal))
        {
            if (DateTime.UtcNow > giveUp)
            {
                throw new TimeoutException("The uninstaller did not finish:\n" + log);
            }

            await Task.Delay(500, Ct);
            log = await ReadLogAsync(logPath);
        }

        log.ShouldContain("Uninstallation process succeeded");
        using RegistryKey? key = Registry.CurrentUser.OpenSubKey(UninstallKey);
        key.ShouldBeNull("the uninstall entry is gone from Apps & features");
    }

    private static string? InstalledVersion()
    {
        using RegistryKey? key = Registry.CurrentUser.OpenSubKey(UninstallKey);
        return key?.GetValue("DisplayVersion") as string;
    }

    private async Task<string> ExpectedVersionAsync()
    {
        CliRun version = await EndToEnd.RunCliAsync(PublishedPayload.Cli(), ["version", "--json"], DataRoot);
        return JsonDocument.Parse(version.Stdout).RootElement.GetProperty("version").GetString()!;
    }

    private async Task AddFolderDestinationAsync(string folder)
    {
        JsonObject document = JsonNode.Parse((await CaptrAsync("settings", "export")).Stdout)!.AsObject();
        document["framerate"] = 10;
        document["destinations"] = new JsonArray(new JsonObject
        {
            ["name"] = "installed-e2e",
            ["kind"] = "folder",
            ["folderPath"] = folder,
        });
        string file = Path.Combine(_root, "settings-with-destination.json");
        await File.WriteAllTextAsync(file, document.ToJsonString(), Ct);
        (await CaptrAsync("settings", "import", file)).ExitCode.ShouldBe(ExitCodes.Success);
    }

    [Fact]
    public async Task Install_first_run_record_transfer_and_uninstall()
    {
        string expected = await ExpectedVersionAsync();

        (int exitCode, string log) = await InstallAsync(CurrentInstaller());

        // Installed, per-user, exactly as asked.
        exitCode.ShouldBe(0, log);
        File.Exists(InstalledCli).ShouldBeTrue();
        InstalledVersion().ShouldBe(expected);
        (Environment.GetEnvironmentVariable("Path", EnvironmentVariableTarget.User) ?? "")
            .ShouldNotContain(InstallDir, Case.Insensitive, "the PATH task was turned off");
        Directory.EnumerateFiles(InstallDir, "*.pdb", SearchOption.AllDirectories)
            .ShouldBeEmpty("symbols are archived with the release, never installed");
        foreach (string binary in new[] { "captr.exe", "Captr.App.exe", "Captr.Core.dll" })
        {
            FileVersionInfo.GetVersionInfo(Path.Combine(InstallDir, binary)).ProductVersion
                .ShouldNotBeNull().ShouldStartWith(expected, customMessage: binary + " carries the release version");
        }

        // First run: setup created settings with the preset working folder, and the
        // installed Captr reports itself healthy.
        JsonDocument settings = JsonDocument.Parse((await CaptrAsync("settings", "get")).Stdout);
        settings.RootElement.GetProperty("workingFolder").GetString()!.TrimEnd('\\')
            .ShouldBe(Work, StringCompareShould.IgnoreCase);
        (await CaptrAsync("version")).Stdout.ShouldContain(expected);

        using (Process window = Process.Start(new ProcessStartInfo(Path.Combine(InstallDir, "Captr.App.exe"))
        {
            UseShellExecute = false,
            Environment = { [CaptrPaths.DataRootVariable] = DataRoot },
        })!)
        {
            DateTime giveUp = DateTime.UtcNow.AddSeconds(45);
            while (window.MainWindowHandle == IntPtr.Zero && !window.HasExited && DateTime.UtcNow < giveUp)
            {
                await Task.Delay(500, Ct);
                window.Refresh();
            }

            window.HasExited.ShouldBeFalse("the window must not fall over on first run");
            window.MainWindowHandle.ShouldNotBe(IntPtr.Zero, "the window opens on first run");
            window.Kill(entireProcessTree: true);
        }

        // Record, and the recording reaches its destination.
        string destination = Path.Combine(_root, "destination");
        await AddFolderDestinationAsync(destination);
        (await CaptrAsync("start", "--label", "installed")).ExitCode.ShouldBe(ExitCodes.Success);
        await Task.Delay(TimeSpan.FromSeconds(8), Ct);
        (await CaptrAsync("stop")).ExitCode.ShouldBe(ExitCodes.Success);
        await EndToEnd.WaitUntilIdleAsync(InstalledCli, DataRoot, TimeSpan.FromMinutes(3));

        string session = Directory.GetDirectories(Work).ShouldHaveSingleItem();
        (await CaptrAsync("recordings", "verify", session)).ExitCode.ShouldBe(ExitCodes.Success);
        string transferred = await WaitForFileAsync(destination);

        // Uninstall removes the program and nothing of the user's.
        EndToEnd.KillHostsStartedSince(_startedUtc, InstallDir);
        await UninstallAsync();

        File.Exists(InstalledCli).ShouldBeFalse();
        Directory.Exists(session).ShouldBeTrue("recordings are the user's, never the uninstaller's");
        File.Exists(transferred).ShouldBeTrue();
        File.Exists(Path.Combine(DataRoot, "settings.json")).ShouldBeTrue("settings survive, for a reinstall");
    }

    [Fact]
    public async Task A_silent_upgrade_over_a_running_recording_refuses_quickly_or_stops_it_cleanly_when_told()
    {
        string installer = CurrentInstaller();
        (await InstallAsync(installer)).ExitCode.ShouldBe(0);
        (await CaptrAsync("start")).ExitCode.ShouldBe(ExitCodes.Success);
        await Task.Delay(TimeSpan.FromSeconds(5), Ct);

        // Unattended: it must neither wait for a person nor end the recording.
        (int refused, string refusedLog) = await InstallAsync(installer);
        refused.ShouldNotBe(0, "setup refuses while a recording is in progress");
        refusedLog.ShouldContain("recording is in progress");
        (await CaptrAsync("status")).ExitCode.ShouldBe(ExitCodes.Success, "the recording carries on");

        (int forced, string forcedLog) = await InstallAsync(installer, "/FORCESTOP=yes");
        forced.ShouldBe(0, forcedLog);
        string session = Directory.GetDirectories(Work).ShouldHaveSingleItem();
        (await CaptrAsync("recordings", "verify", session)).ExitCode.ShouldBe(ExitCodes.Success,
            "a forced upgrade stops AND finalises the recording before replacing any file");
    }

    [Fact]
    public async Task A_silent_downgrade_is_refused_unless_explicitly_allowed()
    {
        string installer = CurrentInstaller();
        (await InstallAsync(installer)).ExitCode.ShouldBe(0);

        // Pretend a newer version is installed: exactly what setup reads to decide.
        using (RegistryKey key = Registry.CurrentUser.OpenSubKey(UninstallKey, writable: true)!)
        {
            key.SetValue("DisplayVersion", "99.0.0");
        }

        (int refused, string refusedLog) = await InstallAsync(installer);
        refused.ShouldNotBe(0);
        refusedLog.ShouldContain("Refusing to downgrade");

        (int allowed, string allowedLog) = await InstallAsync(installer, "/ALLOWDOWNGRADE=yes");
        allowed.ShouldBe(0, allowedLog);
        InstalledVersion().ShouldBe(await ExpectedVersionAsync());
    }

    [Fact]
    public async Task Upgrading_from_the_previous_release_replaces_it_in_place()
    {
        string? previous = Environment.GetEnvironmentVariable(PreviousInstallerVariable);
        if (string.IsNullOrWhiteSpace(previous) || !File.Exists(previous))
        {
            Assert.Skip($"Set {PreviousInstallerVariable} to the previous release's captr-setup-*.exe to run this test.");
        }

        // The previous release predates CAPTR_DATA_ROOT, so it reads and writes the
        // real per-user settings; they are put back exactly as they were afterwards.
        string realRoot = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Captr");
        Dictionary<string, byte[]?> saved = RealSettingsFiles.ToDictionary(name => name, name => File.Exists(Path.Combine(realRoot, name))
                ? File.ReadAllBytes(Path.Combine(realRoot, name))
                : null);
        try
        {
            (int oldExit, string oldLog) = await InstallAsync(previous);
            oldExit.ShouldBe(0, oldLog);
            string? oldVersion = InstalledVersion();

            (int exitCode, string log) = await InstallAsync(CurrentInstaller());

            exitCode.ShouldBe(0, log);
            log.ShouldContain($"Existing Captr {oldVersion} found");
            InstalledVersion().ShouldBe(await ExpectedVersionAsync());
            (await CaptrAsync("version")).Stdout.ShouldContain(await ExpectedVersionAsync());
            using (RegistryKey? uninstall = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Uninstall"))
            {
                uninstall!.GetSubKeyNames().Count(name => name.Contains("7E1C7C2E", StringComparison.OrdinalIgnoreCase))
                    .ShouldBe(1, "an upgrade replaces the entry in Apps & features, it does not add a second");
            }

            // The upgraded install records.
            (await CaptrAsync("start")).ExitCode.ShouldBe(ExitCodes.Success);
            await Task.Delay(TimeSpan.FromSeconds(5), Ct);
            (await CaptrAsync("stop")).ExitCode.ShouldBe(ExitCodes.Success);
            await EndToEnd.WaitUntilIdleAsync(InstalledCli, DataRoot, TimeSpan.FromMinutes(3));

            EndToEnd.KillHostsStartedSince(_startedUtc, InstallDir);
            await UninstallAsync();
            (Directory.Exists(InstallDir) ? Directory.EnumerateFiles(InstallDir, "*", SearchOption.AllDirectories) : Enumerable.Empty<string>())
                .ShouldBeEmpty("uninstall removes the files of BOTH versions");
        }
        finally
        {
            foreach ((string name, byte[]? content) in saved)
            {
                string path = Path.Combine(realRoot, name);
                if (content is null)
                {
                    File.Delete(path);
                }
                else
                {
                    await File.WriteAllBytesAsync(path, content, Ct);
                }
            }
        }
    }

    private static async Task<string> WaitForFileAsync(string folder)
    {
        for (int attempt = 0; attempt < 120; attempt++)
        {
            if (Directory.Exists(folder)
                && Directory.EnumerateFiles(folder, "*.mkv", SearchOption.AllDirectories).FirstOrDefault() is { } file)
            {
                return file;
            }

            await Task.Delay(1000, Ct);
        }

        throw new TimeoutException($"Nothing arrived in {folder}.");
    }
}
