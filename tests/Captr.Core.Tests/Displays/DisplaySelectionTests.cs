using Captr.Core.Displays;

using Shouldly;

namespace Captr.Core.Tests.Displays;

/// <summary>
/// Display-identity resolution across simulated topology changes (SPEC §14):
/// reordering, hot-plug, and the return of a previously deselected display.
/// The fixtures model real monitor device paths; only the DXGI indices shuffle.
/// </summary>
public class DisplaySelectionTests
{
    private const string LaptopPanel = @"\\?\DISPLAY#SHP1523#4&2c34d13&0&UID8388688#{e6f07b5f-ee97-4a90-b076-33f57bf4eaa7}";
    private const string DellLeft = @"\\?\DISPLAY#DEL41B3#5&238e694&0&UID4352#{e6f07b5f-ee97-4a90-b076-33f57bf4eaa7}";
    private const string LgRight = @"\\?\DISPLAY#GSM5B08#5&238e694&0&UID4353#{e6f07b5f-ee97-4a90-b076-33f57bf4eaa7}";

    private static DisplayInfo Make(string stableId, int dxgiIndex, int number = 1) => new()
    {
        StableId = stableId,
        WindowsDisplayNumber = number,
        FriendlyName = "Test Monitor",
        DxgiOutputIndex = dxgiIndex,
        DxgiAdapterIndex = 0,
        Width = 1920,
        Height = 1080,
        VirtualX = dxgiIndex * 1920,
        VirtualY = 0,
        DpiScale = 1.0,
        RefreshRateHz = 60,
    };

    [Fact]
    public void With_no_exclusions_every_display_is_recorded()
    {
        var selection = DisplaySelection.Resolve(
            [Make(LaptopPanel, 0), Make(DellLeft, 1)],
            excludedStableIds: [],
            previouslySeenStableIds: [LaptopPanel, DellLeft]);

        selection.Included.Count.ShouldBe(2);
        selection.Excluded.ShouldBeEmpty();
        selection.CanStartRecording.ShouldBeTrue();
    }

    [Fact]
    public void An_exclusion_follows_the_display_when_dxgi_indices_reorder()
    {
        // Yesterday the Dell was output 1 and excluded. Today a cable swap makes it
        // output 0. A stored INDEX would now exclude the laptop panel instead —
        // exactly the silent failure SPEC §5 forbids. Stable identity must win.
        var selection = DisplaySelection.Resolve(
            [Make(DellLeft, 0), Make(LaptopPanel, 1)],
            excludedStableIds: [DellLeft],
            previouslySeenStableIds: [LaptopPanel, DellLeft]);

        selection.Included.ShouldHaveSingleItem().StableId.ShouldBe(LaptopPanel);
        selection.Excluded.ShouldHaveSingleItem().StableId.ShouldBe(DellLeft);
    }

    [Fact]
    public void A_never_seen_display_is_recorded_by_default_and_flagged_as_new()
    {
        var selection = DisplaySelection.Resolve(
            [Make(LaptopPanel, 0), Make(LgRight, 1)],
            excludedStableIds: [],
            previouslySeenStableIds: [LaptopPanel]);

        selection.Included.Count.ShouldBe(2);
        selection.NewlyAttached.ShouldHaveSingleItem().StableId.ShouldBe(LgRight);
    }

    [Fact]
    public void A_deselected_display_that_returns_after_absence_stays_deselected()
    {
        // The Dell was excluded, unplugged for a week, and reattached at a
        // different index. The exclusion must still hold (SPEC §14).
        var selection = DisplaySelection.Resolve(
            [Make(LaptopPanel, 0), Make(DellLeft, 1)],
            excludedStableIds: [DellLeft],
            previouslySeenStableIds: [LaptopPanel, DellLeft]);

        selection.Excluded.ShouldHaveSingleItem().StableId.ShouldBe(DellLeft);
        selection.NewlyAttached.ShouldBeEmpty();
    }

    [Fact]
    public void Excluding_every_display_blocks_starting()
    {
        var selection = DisplaySelection.Resolve(
            [Make(LaptopPanel, 0)],
            excludedStableIds: [LaptopPanel],
            previouslySeenStableIds: [LaptopPanel]);

        selection.CanStartRecording.ShouldBeFalse();
    }

    [Fact]
    public void An_exclusion_for_a_detached_display_is_simply_dormant()
    {
        var selection = DisplaySelection.Resolve(
            [Make(LaptopPanel, 0)],
            excludedStableIds: [DellLeft],
            previouslySeenStableIds: [LaptopPanel, DellLeft]);

        selection.Included.ShouldHaveSingleItem();
        selection.Excluded.ShouldBeEmpty();
        selection.CanStartRecording.ShouldBeTrue();
    }

    [Fact]
    public void Identity_matching_ignores_case()
    {
        var selection = DisplaySelection.Resolve(
            [Make(DellLeft, 0)],
            excludedStableIds: [DellLeft.ToUpperInvariant()],
            previouslySeenStableIds: []);

        selection.Excluded.ShouldHaveSingleItem();
    }
}
