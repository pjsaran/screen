using Captr.Core.Encoders;

using Shouldly;

namespace Captr.Core.Tests.Encoders;

public class ArrangementPlannerTests
{
    [Fact]
    public void A_single_display_is_its_own_canvas()
    {
        var plan = ArrangementPlanner.Plan([new CaptureSource(0, 1920, 1080)]);

        plan.Kind.ShouldBe(ArrangementKind.SideBySide);
        plan.CanvasWidth.ShouldBe(1920);
        plan.CanvasHeight.ShouldBe(1080);
    }

    [Fact]
    public void Unequal_heights_pad_to_the_tallest_display()
    {
        var plan = ArrangementPlanner.Plan(
            [new CaptureSource(0, 2560, 1440), new CaptureSource(1, 1920, 1080)]);

        plan.Kind.ShouldBe(ArrangementKind.SideBySide);
        plan.Cells[0].ShouldBe(new ArrangementCell(2560, 1440));
        plan.Cells[1].ShouldBe(new ArrangementCell(1920, 1440));
        plan.CanvasWidth.ShouldBe(4480);
        plan.CanvasHeight.ShouldBe(1440);
    }

    [Fact]
    public void Three_regular_displays_stay_side_by_side()
    {
        var plan = ArrangementPlanner.Plan(
            [new CaptureSource(0, 1920, 1080), new CaptureSource(1, 1920, 1080), new CaptureSource(2, 1920, 1080)]);

        plan.Kind.ShouldBe(ArrangementKind.SideBySide);
        plan.CanvasWidth.ShouldBe(5760);
    }

    [Fact]
    public void Exceeding_the_width_limit_switches_to_a_grid()
    {
        // 3 × 3440 = 10320 > 8192 — side by side would exceed encoder limits.
        var plan = ArrangementPlanner.Plan(
            [new CaptureSource(0, 3440, 1440), new CaptureSource(1, 3440, 1440), new CaptureSource(2, 3440, 1440)]);

        plan.Kind.ShouldBe(ArrangementKind.Grid);
        plan.Columns.ShouldBe(2);
        plan.Rows.ShouldBe(2);
        plan.CanvasWidth.ShouldBe(6880);
        plan.CanvasHeight.ShouldBe(2880);
    }

    [Fact]
    public void A_fourth_display_always_forms_a_grid()
    {
        var plan = ArrangementPlanner.Plan(
        [
            new CaptureSource(0, 1920, 1080), new CaptureSource(1, 1920, 1080),
            new CaptureSource(2, 1920, 1080), new CaptureSource(3, 1920, 1080),
        ]);

        plan.Kind.ShouldBe(ArrangementKind.Grid);
        plan.Columns.ShouldBe(2);
        plan.Rows.ShouldBe(2);
    }

    [Fact]
    public void Grid_cells_are_uniform_at_the_largest_display_size()
    {
        var plan = ArrangementPlanner.Plan(
        [
            new CaptureSource(0, 3440, 1440), new CaptureSource(1, 1920, 1080),
            new CaptureSource(2, 1920, 1200), new CaptureSource(3, 1280, 1024),
        ]);

        plan.Cells.ShouldAllBe(c => c.Width == 3440 && c.Height == 1440);
    }

    [Fact]
    public void Odd_display_dimensions_are_rounded_up_to_even()
    {
        // 4:2:0 encoders refuse odd dimensions; the planner absorbs the quirk here.
        var plan = ArrangementPlanner.Plan([new CaptureSource(0, 1365, 767)]);

        plan.Cells[0].ShouldBe(new ArrangementCell(1366, 768));
    }

    [Fact]
    public void No_sources_is_rejected()
    {
        Should.Throw<ArgumentException>(() => ArrangementPlanner.Plan([]));
    }
}
