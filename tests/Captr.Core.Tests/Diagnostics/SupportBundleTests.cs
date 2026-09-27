using System.IO.Compression;

using Captr.Core.Diagnostics;
using Captr.Core.Settings;

using Shouldly;

namespace Captr.Core.Tests.Diagnostics;

/// <summary>
/// The SPEC §9 bundle test: plant a secret and video content, build the bundle,
/// prove neither appears in the output.
/// </summary>
public class SupportBundleTests : IDisposable
{
    private const string PlantedSecret = "PlantedSecret-0xFEEDFACE-hunter2";

    private readonly string _dir = Directory.CreateTempSubdirectory("captr-bundle-").FullName;

    [Fact]
    public async Task The_bundle_contains_diagnostics_but_no_secret_and_no_video()
    {
        // Arrange a realistic working folder: journal + log with a planted secret,
        // plus a (fake) video segment that must NOT travel.
        string workingRoot = Path.Combine(_dir, "sessions");
        string sessionFolder = Path.Combine(workingRoot, "20260819-1000-abc");
        Directory.CreateDirectory(sessionFolder);
        await File.WriteAllTextAsync(Path.Combine(sessionFolder, "journal.ndjson"),
            """{"kind":"session-started","machineName":"BOX"}""", TestContext.Current.CancellationToken);
        await File.WriteAllTextAsync(Path.Combine(sessionFolder, "seg-g01-101010.mkv"),
            "FAKE VIDEO BYTES", TestContext.Current.CancellationToken);

        string logFolder = Path.Combine(_dir, "logs");
        Directory.CreateDirectory(logFolder);
        await File.WriteAllTextAsync(Path.Combine(logFolder, "host-20260819.log"),
            $"2026-08-19 10:00:00 [INF] Something happened\n" +
            $"2026-08-19 10:00:01 [INF] clientSecret={PlantedSecret} used for auth\n",
            TestContext.Current.CancellationToken);

        string bundlePath = Path.Combine(_dir, "bundle.zip");
        await SupportBundle.CreateAsync(bundlePath, workingRoot, logFolder, TestContext.Current.CancellationToken);

        // Assert: read EVERY entry back and search for the planted material.
        // (VSTHRD103 pattern-matches "Open*"; the zip APIs have no async siblings.)
#pragma warning disable VSTHRD103
        using ZipArchive archive = ZipFile.OpenRead(bundlePath);
        archive.Entries.ShouldContain(e => e.FullName == "system-info.txt");
        archive.Entries.ShouldContain(e => e.FullName.EndsWith("journal.ndjson"));
        archive.Entries.ShouldNotContain(e => e.FullName.EndsWith(".mkv"), "video content must never travel");

        foreach (ZipArchiveEntry entry in archive.Entries)
        {
            using var reader = new StreamReader(entry.Open());
            string content = await reader.ReadToEndAsync(TestContext.Current.CancellationToken);
            content.ShouldNotContain(PlantedSecret, customMessage: $"secret found in {entry.FullName}");
        }
#pragma warning restore VSTHRD103
    }

    [Theory]
    [InlineData("clientSecret=abc123", "clientSecret [REDACTED]")]
    [InlineData("the password: hunter2", "the password [REDACTED]")]
    [InlineData("ordinary log line", "ordinary log line")]
    public void Line_scrubbing_masks_after_a_suspect_key(string input, string expected)
    {
        SupportBundle.ScrubLine(input).ShouldBe(expected);
    }

    [Theory]
    [InlineData("ffmpeg -i x -force_key_frames expr:gte(t,n_forced*2) -c:v libx264")]
    [InlineData("Keyframe interval 2 s; monkey business")]
    public void Ordinary_words_that_contain_key_are_not_masked(string line)
    {
        // "key" matched anywhere used to mask the rest of every FFmpeg command line.
        SupportBundle.ScrubLine(line).ShouldBe(line);
    }

    [Fact]
    public void A_bearer_token_is_masked_wherever_it_appears()
    {
        // Graph's pre-authenticated upload URLs carry a JWT in tempauth.
        const string jwt = "eyJ0eXAiOiJKV1QiLCJhbGciOiJub25lIn0.eyJhdWQiOiJodHRwczovL2V4YW1wbGUifQ.c2lnbmF0dXJlLWJ5dGVz";

        string scrubbed = SupportBundle.ScrubLine($"Resuming https://contoso.sharepoint.com/_api/upload?guid=1&tempauth={jwt}");

        scrubbed.ShouldNotContain(jwt);
    }

    private static async Task<Dictionary<string, string>> ReadAllAsync(string bundlePath)
    {
        var entries = new Dictionary<string, string>();
#pragma warning disable VSTHRD103
        using ZipArchive archive = ZipFile.OpenRead(bundlePath);
        foreach (ZipArchiveEntry entry in archive.Entries)
        {
            using var reader = new StreamReader(entry.Open());
            entries[entry.FullName] = await reader.ReadToEndAsync(TestContext.Current.CancellationToken);
        }
#pragma warning restore VSTHRD103
        return entries;
    }

    [Fact]
    public async Task The_log_the_running_recorder_is_writing_is_included()
    {
        // Serilog holds today's log open for writing; reading it the default way hit
        // a sharing violation and silently left out the one log that mattered.
        string logFolder = Path.Combine(_dir, "logs");
        Directory.CreateDirectory(logFolder);
        string live = Path.Combine(logFolder, "host-20260928.log");
        await using var writer = new FileStream(live, FileMode.Append, FileAccess.Write, FileShare.Read);
        await writer.WriteAsync("2026-09-28 10:00:00 [ERR] the thing that went wrong\n"u8.ToArray(), TestContext.Current.CancellationToken);
        await writer.FlushAsync(TestContext.Current.CancellationToken);

        string bundlePath = Path.Combine(_dir, "bundle.zip");
        SupportBundleContents contents = await SupportBundle.CreateAsync(
            bundlePath, Path.Combine(_dir, "none"), logFolder, TestContext.Current.CancellationToken);

        contents.Unreadable.ShouldBeEmpty();
        (await ReadAllAsync(bundlePath))["logs/host-20260928.log"].ShouldContain("the thing that went wrong");
    }

    [Fact]
    public async Task Who_and_where_are_replaced_and_the_bundle_says_what_it_holds()
    {
        string logFolder = Path.Combine(_dir, "logs");
        Directory.CreateDirectory(logFolder);
        const string tenant = "3f2b9c41-7d7e-4a51-9a61-0c1d2e3f4a5b";
        string profile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        await File.WriteAllTextAsync(Path.Combine(logFolder, "host-20260928.log"),
            $"Recording by {Environment.UserName} on {Environment.MachineName} into {profile}\\Videos\n" +
            $"AADSTS700016: Application not found in the directory '{tenant}' at contoso.sharepoint.com\n",
            TestContext.Current.CancellationToken);
        CaptrSettings settings = CaptrSettings.CreateDefault() with
        {
            Destinations =
            [
                new DestinationSettings
                {
                    Name = "sp", Kind = DestinationKind.SharePoint, TenantId = tenant, ClientId = "c", CredentialName = "sp",
                    SharePointSiteUrl = "https://contoso.sharepoint.com/sites/rec", SharePointDriveId = "d",
                },
            ],
        };

        string bundlePath = Path.Combine(_dir, "bundle.zip");
        await SupportBundle.CreateAsync(bundlePath, Path.Combine(_dir, "none"), logFolder, TestContext.Current.CancellationToken, settings);

        Dictionary<string, string> entries = await ReadAllAsync(bundlePath);
        foreach ((string name, string content) in entries)
        {
            content.ShouldNotContain(Environment.UserName, Case.Insensitive, $"user name in {name}");
            content.ShouldNotContain(Environment.MachineName, Case.Insensitive, $"PC name in {name}");
            content.ShouldNotContain(tenant, Case.Insensitive, $"tenant in {name}");
            content.ShouldNotContain("contoso.sharepoint.com", Case.Insensitive, $"SharePoint host in {name}");
        }

        entries["logs/host-20260928.log"].ShouldContain(@"%USERPROFILE%\Videos", customMessage: "replaced, not removed: the path still reads");
        entries["README.txt"].ShouldContain("logs/host-20260928.log");
        entries["README.txt"].ShouldContain("no video and no secrets");
    }

    public void Dispose() => Directory.Delete(_dir, recursive: true);
}
