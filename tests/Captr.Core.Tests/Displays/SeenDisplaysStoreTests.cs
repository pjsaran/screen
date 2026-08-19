using Captr.Core.Displays;

using Shouldly;

namespace Captr.Core.Tests.Displays;

public class SeenDisplaysStoreTests : IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("captr-seen-").FullName;

    private SeenDisplaysStore MakeStore() => new(Path.Combine(_dir, "seen-displays.json"));

    private static DisplayInfo Make(string stableId) => new()
    {
        StableId = stableId,
        WindowsDisplayNumber = 1,
        FriendlyName = "M",
        DxgiOutputIndex = 0,
        DxgiAdapterIndex = 0,
        Width = 1,
        Height = 1,
        VirtualX = 0,
        VirtualY = 0,
        DpiScale = 1,
        RefreshRateHz = 60,
    };

    [Fact]
    public void A_fresh_machine_has_seen_nothing()
    {
        MakeStore().Load().ShouldBeEmpty();
    }

    [Fact]
    public void Marked_displays_are_remembered_across_store_instances()
    {
        MakeStore().MarkSeen([Make("id-a"), Make("id-b")]);

        MakeStore().Load().ShouldBe(["id-a", "id-b"]);
    }

    [Fact]
    public void Marking_the_same_display_twice_stores_it_once()
    {
        SeenDisplaysStore store = MakeStore();
        store.MarkSeen([Make("id-a")]);
        store.MarkSeen([Make("id-a"), Make("id-b")]);

        store.Load().Count.ShouldBe(2);
    }

    [Fact]
    public void A_corrupt_state_file_resets_harmlessly()
    {
        string path = Path.Combine(_dir, "seen-displays.json");
        File.WriteAllText(path, "not json at all");

        new SeenDisplaysStore(path).Load().ShouldBeEmpty();
    }

    public void Dispose() => Directory.Delete(_dir, recursive: true);
}
