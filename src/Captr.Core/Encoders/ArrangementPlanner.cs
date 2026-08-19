namespace Captr.Core.Encoders;

/// <summary>
/// Decides how multiple displays combine into one canvas (SPEC §5): side-by-side for
/// a small number of displays, a grid when the combined width would become
/// unreasonable; displays of unequal size are padded onto a common cell with black
/// bars. Pure arithmetic — no I/O — so every arrangement case is directly unit
/// tested. If it is wrong, the recording layout is wrong for every session.
/// </summary>
public static class ArrangementPlanner
{
    /// <summary>Plans the canvas for the given sources (canvas order).</summary>
    public static ArrangementPlan Plan(IReadOnlyList<CaptureSource> sources)
    {
        if (sources.Count == 0)
        {
            throw new ArgumentException("At least one capture source is required.", nameof(sources));
        }

        int combinedWidth = sources.Sum(s => s.Width);
        bool sideBySide =
            sources.Count <= EncodingConstants.MaxSideBySideCount
            && combinedWidth <= EncodingConstants.MaxSideBySideWidth;

        return sideBySide ? PlanSideBySide(sources) : PlanGrid(sources);
    }

    /// <summary>Side by side: every display keeps its own width, padded to the
    /// tallest display's height with centred black bars.</summary>
    private static ArrangementPlan PlanSideBySide(IReadOnlyList<CaptureSource> sources)
    {
        int cellHeight = MakeEven(sources.Max(s => s.Height));

        var cells = sources
            .Select(s => new ArrangementCell(Width: MakeEven(s.Width), Height: cellHeight))
            .ToList();

        return new ArrangementPlan(
            Kind: ArrangementKind.SideBySide,
            Columns: sources.Count,
            Rows: 1,
            Cells: cells,
            CanvasWidth: cells.Sum(c => c.Width),
            CanvasHeight: cellHeight);
    }

    /// <summary>Grid: uniform cells sized to the largest display (a grid's stacking
    /// requires equal-size cells), squarest layout that fits all sources.</summary>
    private static ArrangementPlan PlanGrid(IReadOnlyList<CaptureSource> sources)
    {
        int cellWidth = MakeEven(sources.Max(s => s.Width));
        int cellHeight = MakeEven(sources.Max(s => s.Height));

        int columns = (int)Math.Ceiling(Math.Sqrt(sources.Count));
        int rows = (int)Math.Ceiling(sources.Count / (double)columns);

        var cells = sources.Select(_ => new ArrangementCell(cellWidth, cellHeight)).ToList();

        return new ArrangementPlan(
            Kind: ArrangementKind.Grid,
            Columns: columns,
            Rows: rows,
            Cells: cells,
            CanvasWidth: columns * cellWidth,
            CanvasHeight: rows * cellHeight);
    }

    /// <summary>Encoders require even dimensions for 4:2:0 output; an odd display
    /// dimension (rare, but possible with exotic scaling) gets one pixel of pad.</summary>
    private static int MakeEven(int value) => value % 2 == 0 ? value : value + 1;
}

/// <summary>The planned canvas: arrangement kind, grid shape, one cell per source
/// (same order as the sources), and the resulting canvas dimensions.</summary>
public sealed record ArrangementPlan(
    ArrangementKind Kind,
    int Columns,
    int Rows,
    IReadOnlyList<ArrangementCell> Cells,
    int CanvasWidth,
    int CanvasHeight);

/// <summary>The rectangle one source is padded into before stacking.</summary>
public sealed record ArrangementCell(int Width, int Height);

/// <summary>How the cells are combined.</summary>
public enum ArrangementKind
{
    SideBySide,
    Grid,
}
