using Captr.Core.Delivery;

using Shouldly;

namespace Captr.Core.Tests.Delivery;

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
    public async Task A_delivery_lands_verified_under_the_desired_name()
    {
        string source = MakeSource();
        string target = Path.Combine(_dir, "dest");

        string landed = await FolderDestination.DeliverAsync(
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

        string landed = await FolderDestination.DeliverAsync(
            source, target, "rec.mkv", null, TestContext.Current.CancellationToken);

        landed.ShouldBe(Path.Combine(target, "rec (2).mkv"));
        (await File.ReadAllTextAsync(Path.Combine(target, "rec.mkv"), TestContext.Current.CancellationToken)).ShouldBe("precious existing recording");
    }

    [Fact]
    public async Task Progress_is_reported_during_the_copy()
    {
        string source = Path.Combine(_dir, "big.mkv");
        await File.WriteAllBytesAsync(source, new byte[3 << 20], TestContext.Current.CancellationToken);
        var reports = new List<double>();

        await FolderDestination.DeliverAsync(
            source, Path.Combine(_dir, "dest"), "big.mkv",
            new Progress<double>(reports.Add), TestContext.Current.CancellationToken);

        // Progress is asynchronous; give the posted callbacks a beat to land.
        await Task.Delay(200, TestContext.Current.CancellationToken);
        reports.ShouldNotBeEmpty();
        reports.Max().ShouldBe(1.0, 0.001);
    }

    public void Dispose() => Directory.Delete(_dir, recursive: true);
}
