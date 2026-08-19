namespace Captr.Core.Encoders;

/// <summary>
/// Escapes text for use inside an FFmpeg filter-graph option value — specifically the
/// drawtext overlay. Owns the one piece of string handling SPEC §5 calls out by name:
/// "colons and backslashes are the usual cause of a filter graph that fails silently.
/// Unit-test the escaping." If this is wrong, the overlay silently breaks the whole
/// capture graph.
/// </summary>
/// <remarks>
/// FFmpeg parses a filter graph in layers, and each layer has reserved characters:
/// <list type="bullet">
/// <item>the option parser splits on <c>:</c> and unquotes <c>'…'</c>,</item>
/// <item>the graph parser splits on <c>,</c> <c>;</c> <c>[</c> <c>]</c>,</item>
/// <item>drawtext itself expands <c>%{…}</c> sequences and treats <c>\</c> as escape.</item>
/// </list>
/// We pass the whole argument vector without a shell, so shell quoting is NOT
/// involved — only FFmpeg's own layers. The strategy here escapes every reserved
/// character with a backslash, which survives both parser layers for option values.
/// The exact behaviour is pinned by unit tests AND exercised against the real FFmpeg
/// binary in integration (a graph that parses is the only proof that matters).
/// </remarks>
public static class DrawTextEscaper
{
    /// <summary>
    /// Escapes arbitrary text so drawtext renders it literally (except <c>%{…}</c>
    /// expansion sequences, which are deliberately left usable so an overlay can show
    /// e.g. <c>%{localtime}</c>).
    /// </summary>
    /// <remarks>
    /// The escape depth differs PER CHARACTER because each parser layer has its own
    /// reserved set (this mirrors the worked example in FFmpeg's own "filtergraph
    /// escaping" documentation, and is verified against the real binary in
    /// integration — a first attempt with uniform double-backslashes parsed in no
    /// test but failed on the real ffmpeg):
    /// <list type="bullet">
    /// <item><c>,</c> <c>;</c> <c>[</c> <c>]</c> — graph-parser level only → one backslash.</item>
    /// <item><c>:</c> — option-parser level: <c>\:</c>, whose backslash the graph
    /// parser must itself see escaped → <c>\\:</c>.</item>
    /// <item><c>'</c> — option level <c>\'</c>, then the backslash re-escaped → <c>\\\'</c>.</item>
    /// <item><c>\</c> — escaped at both levels → <c>\\\\</c>.</item>
    /// </list>
    /// </remarks>
    public static string Escape(string text)
    {
        var builder = new System.Text.StringBuilder(text.Length + 8);
        foreach (char c in text)
        {
            switch (c)
            {
                case '\\':
                    builder.Append(@"\\\\");
                    break;
                case '\'':
                    builder.Append(@"\\\'");
                    break;
                case ':':
                    builder.Append(@"\\:");
                    break;
                case ',' or ';' or '[' or ']':
                    builder.Append('\\').Append(c);
                    break;
                default:
                    builder.Append(c);
                    break;
            }
        }

        return builder.ToString();
    }

    /// <summary>
    /// Escapes a Windows file path for a filter option value (e.g. drawtext's
    /// <c>fontfile</c>). Uses forward slashes (FFmpeg accepts them everywhere) and
    /// escapes the drive-letter colon, the classic silent-failure character.
    /// </summary>
    public static string EscapePath(string path)
    {
        string forwardSlashes = path.Replace('\\', '/');
        return forwardSlashes.Replace(":", "\\\\:", StringComparison.Ordinal);
    }
}
