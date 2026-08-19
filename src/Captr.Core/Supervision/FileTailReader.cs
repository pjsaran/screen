using System.Text;

namespace Captr.Core.Supervision;

/// <summary>
/// Asynchronously tails a growing text file, invoking a callback per complete line.
/// This is how the supervisor reads FFmpeg's progress stream and log — both go to
/// files rather than pipes, which is what lets the encoder survive host death and
/// makes the classic two-pipe deadlock structurally impossible (see the folder
/// README). If it fails, the supervisor goes blind, so it tolerates every transient:
/// the file not existing yet, being locked briefly, or the process recreating it.
/// </summary>
public sealed class FileTailReader
{
    private readonly string _path;
    private readonly Action<string> _onLine;

    public FileTailReader(string path, Action<string> onLine)
    {
        _path = path;
        _onLine = onLine;
    }

    /// <summary>
    /// Tails until cancelled. Starts from the given offset — 0 for a fresh file,
    /// or the previous position when resuming after re-adoption.
    /// </summary>
    public async Task TailAsync(long startOffset, CancellationToken cancellationToken)
    {
        long position = startOffset;
        var carry = new StringBuilder();

        while (!cancellationToken.IsCancellationRequested)
        {
            position = ReadNewText(position, carry);

            try
            {
                await Task.Delay(SupervisionConstants.TailPollInterval, cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                // One final read so lines written just before the stop are not lost
                // (the encoder's last progress block often lands in this window).
                ReadNewText(position, carry);
                return;
            }
        }
    }

    private long ReadNewText(long position, StringBuilder carry)
    {
        try
        {
            using var stream = new FileStream(
                _path, FileMode.Open, FileAccess.Read,
                FileShare.ReadWrite | FileShare.Delete);

            if (stream.Length < position)
            {
                // The file was truncated or recreated (an encoder restart) — start over.
                position = 0;
                carry.Clear();
            }

            stream.Position = position;
            using var reader = new StreamReader(stream, Encoding.UTF8, leaveOpen: true);
            string newText = reader.ReadToEnd();
            position = stream.Position;

            EmitCompleteLines(carry, newText);
            return position;
        }
        catch (FileNotFoundException)
        {
            return 0; // Not created yet — keep waiting.
        }
        catch (DirectoryNotFoundException)
        {
            return 0;
        }
        catch (IOException)
        {
            return position; // Briefly locked — try again next poll.
        }
    }

    /// <summary>Buffers a partial trailing line until its newline arrives, so the
    /// callback only ever sees complete lines.</summary>
    private void EmitCompleteLines(StringBuilder carry, string newText)
    {
        foreach (char c in newText)
        {
            if (c == '\n')
            {
                _onLine(carry.ToString().TrimEnd('\r'));
                carry.Clear();
            }
            else
            {
                carry.Append(c);
            }
        }
    }
}
