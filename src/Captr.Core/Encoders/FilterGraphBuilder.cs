using System.Globalization;
using System.Text;

namespace Captr.Core.Encoders;

/// <summary>
/// Produces the <c>-filter_complex</c> graph string: one <c>ddagrab</c> capture per
/// display, downloaded to system memory, padded onto its cell, stacked into the
/// canvas, optionally overlaid with text, and converted to the encoder pixel format.
/// Owns the graph's exact text — which is why it is pure and pinned by golden files.
/// If it is wrong, capture produces the wrong picture or fails to start at all.
/// </summary>
public static class FilterGraphBuilder
{
    /// <summary>Builds the complete filter graph for a plan.</summary>
    public static string Build(RecordingPlan plan, ArrangementPlan arrangement)
    {
        var graph = new StringBuilder();

        // One capture chain per display, each ending in a labelled pad [s0], [s1]…
        for (int i = 0; i < plan.Sources.Count; i++)
        {
            CaptureSource source = plan.Sources[i];
            ArrangementCell cell = arrangement.Cells[i];

            if (i > 0)
            {
                graph.Append(';');
            }

            graph.Append(CultureInfo.InvariantCulture,
                $"ddagrab=output_idx={source.OutputIndex}:framerate={plan.FrameRate}:draw_mouse={(EncodingConstants.DrawMouse ? 1 : 0)}");
            // hwdownload: D3D11 frames -> system memory; format=bgra is mandatory
            // immediately after (the download must be told its layout, and every
            // CPU filter downstream needs a defined format).
            graph.Append(",hwdownload,format=bgra");
            // Pad onto the cell with the display centred; bars are black.
            graph.Append(CultureInfo.InvariantCulture,
                $",pad=w={cell.Width}:h={cell.Height}:x=(ow-iw)/2:y=(oh-ih)/2:color=black");
            graph.Append(CultureInfo.InvariantCulture, $"[s{i}]");
        }

        // Stack the cells into the canvas.
        string canvasLabel;
        if (plan.Sources.Count == 1)
        {
            canvasLabel = "s0";
        }
        else
        {
            graph.Append(';');
            for (int i = 0; i < plan.Sources.Count; i++)
            {
                graph.Append(CultureInfo.InvariantCulture, $"[s{i}]");
            }

            graph.Append(arrangement.Kind == ArrangementKind.SideBySide
                ? $"hstack=inputs={plan.Sources.Count}"
                : BuildXstack(plan.Sources.Count, arrangement));
            graph.Append("[st]");
            canvasLabel = "st";
        }

        // Final chain: optional overlay, then the encoder pixel format.
        graph.Append(CultureInfo.InvariantCulture, $";[{canvasLabel}]");
        if (!string.IsNullOrEmpty(plan.OverlayText))
        {
            graph.Append(BuildDrawText(plan.OverlayText));
            graph.Append(',');
        }

        graph.Append(CultureInfo.InvariantCulture, $"format={EncodingConstants.PixelFormat}[v]");

        return graph.ToString();
    }

    /// <summary>Grid stacking with absolute cell positions. <c>fill=black</c> paints
    /// unused cells when the last row is incomplete (e.g. 3 displays in a 2×2 grid).</summary>
    private static string BuildXstack(int sourceCount, ArrangementPlan arrangement)
    {
        var positions = new List<string>(sourceCount);
        for (int i = 0; i < sourceCount; i++)
        {
            int column = i % arrangement.Columns;
            int row = i / arrangement.Columns;
            int x = column * arrangement.Cells[0].Width;
            int y = row * arrangement.Cells[0].Height;
            positions.Add(FormattableString.Invariant($"{x}_{y}"));
        }

        string layout = string.Join('|', positions);
        bool gridHasEmptyCells = sourceCount < arrangement.Columns * arrangement.Rows;
        string fill = gridHasEmptyCells ? ":fill=black" : string.Empty;

        return FormattableString.Invariant($"xstack=inputs={sourceCount}:layout={layout}{fill}");
    }

    /// <summary>The overlay filter. The text and the font path both pass through
    /// <see cref="DrawTextEscaper"/> — see SPEC §5's warning about colons and
    /// backslashes silently breaking the graph.</summary>
    private static string BuildDrawText(string overlayText)
    {
        // Consolas ships with every supported Windows and is monospaced, which keeps
        // a timestamp overlay steady instead of jittering as digits change width.
        string fontFile = DrawTextEscaper.EscapePath(@"C:\Windows\Fonts\consola.ttf");
        string text = DrawTextEscaper.Escape(overlayText);

        return
            $"drawtext=fontfile={fontFile}:text={text}" +
            ":fontsize=24:fontcolor=white:box=1:boxcolor=black@0.4:boxborderw=6:x=12:y=12";
    }
}
