using Captr.Core.Transfers;

using Shouldly;

namespace Captr.Core.Tests.Transfers;

public class FolderDestinationTests : IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("captr-folderdest-").FullName;

    private string MakeSource(string content = "recording bytes")
    {
        string path = Path.Combine(_dir, "source.mkv");
        File.WriteAllText(path, content);
        return path;
    }

    [Fact]
    public async Task A_transfer_lands_verified_under_the_desired_name()
    {
        string source = MakeSource();
        string target = Path.Combine(_dir, "dest");

        string landed = await FolderDestination.TransferAsync(
            source, target, "TRADER-01 2026-08-19.mkv", null, TestContext.Current.CancellationToken);

        landed.ShouldBe(Path.Combine(target, "TRADER-01 2026-08-19.mkv"));
        (await File.ReadAllTextAsync(landed, TestContext.Current.CancellationToken)).ShouldBe("recording bytes");
        // No .partial left behind.
        Directory.GetFiles(target, "*.partial").ShouldBeEmpty();
        // The source is untouched (SPEC §13 rule 4).
        File.Exists(source).ShouldBeTrue();
    }

    [Fact]
    public async Task A_name_collision_suffixes_and_never_overwrites()
    {
        string source = MakeSource("new content");
        string target = Path.Combine(_dir, "dest");
        Directory.CreateDirectory(target);
        await File.WriteAllTextAsync(Path.Combine(target, "rec.mkv"), "precious existing recording", TestContext.Current.CancellationToken);

        string landed = await FolderDestination.TransferAsync(
            source, target, "rec.mkv", null, TestContext.Current.CancellationToken);

        landed.ShouldBe(Path.Combine(target, "rec (2).mkv"));
        (await File.ReadAllTextAsync(Path.Combine(target, "rec.mkv"), TestContext.Current.CancellationToken)).ShouldBe("precious existing recording");
    }

    [Fact]
    public async Task Progress_is_reported_during_the_copy()
    {
        string source = Path.Combine(_dir, "big.mkv");
        await File.WriteAllBytesAsync(source, new byte[3 << 20], TestContext.Current.CancellationToken);
        var reports = new CollectingProgress();

        await FolderDestination.TransferAsync(
            source, Path.Combine(_dir, "dest"), "big.mkv",
            reports, TestContext.Current.CancellationToken);

        reports.Reports.ShouldNotBeEmpty();
        reports.Reports.Max().ShouldBe(1.0, 0.001);
    }

    /// <summary>
    /// Collects progress reports SYNCHRONOUSLY on the copying thread.
    /// </summary>
    /// <remarks>
    /// Deliberately not <see cref="Progress{T}"/>. That type posts every report to
    /// the thread pool, so the reports arrive after the copy has finished, in no
    /// guaranteed order, and land in the list from several threads at once — a data
    /// race that made this test fail about one run in ten. Production reports
    /// synchronously for the same reason (see TransferWorker.ThrottledProgress), so
    /// this is also the more faithful test double.
    /// </remarks>
    private sealed class CollectingProgress : IProgress<double>
    {
        public List<double> Reports { get; } = [];

        public void Report(double value) => Reports.Add(value);
    }

    public void Dispose() => Directory.Delete(_dir, recursive: true);
}
