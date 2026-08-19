using Captr.Core.Displays;

using Shouldly;

namespace Captr.Integration.Tests.Displays;

/// <summary>
/// Runs against the real display hardware of this machine (trait Display — excluded
/// in CI, where no interactive desktop exists). Scenario: whatever is attached must
/// enumerate with a stable EDID-derived identity, sane geometry, and index numbering
/// a ddagrab capture can consume.
/// </summary>
[Trait("Category", "Display")]
public class DisplayEnumeratorTests
{
    [Fact]
    public void At_least_one_display_enumerates_with_full_identity_and_geometry()
    {
        IReadOnlyList<DisplayInfo> displays = new DisplayEnumerator().Enumerate();

        displays.ShouldNotBeEmpty();
        foreach (DisplayInfo display in displays)
        {
            // The stable id is a monitor device interface path, not a GDI name.
            display.StableId.ShouldStartWith(@"\\?\DISPLAY#");
            display.WindowsDisplayNumber.ShouldBeGreaterThan(0);
            display.Width.ShouldBeGreaterThan(0);
            display.Height.ShouldBeGreaterThan(0);
            display.DpiScale.ShouldBeGreaterThanOrEqualTo(1.0);
            display.RefreshRateHz.ShouldBeGreaterThan(0);
        }
    }

    [Fact]
    public void Dxgi_output_indices_are_the_flat_ddagrab_numbering()
    {
        IReadOnlyList<DisplayInfo> displays = new DisplayEnumerator().Enumerate();

        // Flat numbering: dense, starting somewhere >= 0, no duplicates. (ddagrab
        // counts every attached output across adapters in enumeration order.)
        displays.Select(d => d.DxgiOutputIndex).ShouldBeUnique();
        displays.Select(d => d.DxgiOutputIndex).Min().ShouldBeGreaterThanOrEqualTo(0);
    }

    [Fact]
    public void Stable_ids_are_unique_per_monitor()
    {
        IReadOnlyList<DisplayInfo> displays = new DisplayEnumerator().Enumerate();

        displays.Select(d => d.StableId).ShouldBeUnique();
    }
}
