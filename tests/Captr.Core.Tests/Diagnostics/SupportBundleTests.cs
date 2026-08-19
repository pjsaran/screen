using System.IO.Compression;

using Captr.Core.Diagnostics;

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

    public void Dispose() => Directory.Delete(_dir, recursive: true);
}
