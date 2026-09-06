using System.Text;

using Captr.Core.Supervision;

using Shouldly;

namespace Captr.Integration.Tests.Supervision;

/// <summary>
/// Guards the one contract Captr has with FFmpeg's own wording: the log signatures
/// that mean "the desktop was taken away" (see
/// <see cref="SupervisorPolicy.CaptureAccessLostSignatures"/>).
/// </summary>
/// <remarks>
/// <para>
/// WHY THIS TEST EXISTS. Those signatures decide whether a lost desktop is treated
/// as a transient outage — retry with backoff, never give up — or as an encoder
/// fault, which marches the session through the fallback ladder to a loud stop.
/// The list once matched three strings (<c>ACCESS_LOST</c>, <c>ACCESS_DENIED</c>,
/// <c>"Failed to duplicate output"</c>) that FFmpeg has never printed. The unit
/// tests passed, because they fed the classifier those same invented lines. In
/// production the tolerate-with-backoff path was simply unreachable, and on a
/// machine with no hardware encoder — an AWS WorkSpace, where capture runs through
/// gdigrab — three desktop hiccups inside five minutes ended the recording.
/// </para>
/// <para>
/// A unit test can only ever confirm that the classifier matches what we TOLD it to
/// match. Only reading the shipped binary can confirm we told it the truth.
/// </para>
/// </remarks>
[Trait("Category", "Ffmpeg")]
public class CaptureLossSignatureTests
{
    [Fact]
    public void Every_capture_loss_signature_is_really_a_string_inside_the_ffmpeg_we_ship()
    {
        string ffmpegPath = FfmpegLocator.FindFfmpeg();
        byte[] binary = File.ReadAllBytes(ffmpegPath);

        // FFmpeg's format strings are plain ASCII in the executable's data section,
        // so searching the raw bytes is both exact and free of encoding guesswork.
        var missing = SupervisorPolicy.CaptureAccessLostSignatures
            .Where(signature => IndexOf(binary, Encoding.ASCII.GetBytes(signature)) < 0)
            .ToList();

        missing.ShouldBeEmpty(
            $"these signatures are not in {Path.GetFileName(ffmpegPath)}, so they can never match a real " +
            "log line and the tolerate-a-lost-desktop path is dead: " + string.Join(", ", missing) +
            ". Paste the replacement text out of the binary, never out of memory.");
    }

    /// <summary>Plain byte-sequence search — the haystack is ~100 MB and this runs
    /// a handful of times, so clarity beats a fancier algorithm here.</summary>
    private static int IndexOf(byte[] haystack, byte[] needle)
    {
        for (int start = 0; start <= haystack.Length - needle.Length; start++)
        {
            int offset = 0;
            while (offset < needle.Length && haystack[start + offset] == needle[offset])
            {
                offset++;
            }

            if (offset == needle.Length)
            {
                return start;
            }
        }

        return -1;
    }
}
