using Captr.Core.Encoders;

using Shouldly;

namespace Captr.Core.Tests.Encoders;

/// <summary>
/// Text-escaping edge cases (SPEC §5: "colons and backslashes are the usual cause of
/// a filter graph that fails silently — unit-test the escaping"). These tests pin the
/// escaped FORM; integration tests prove the real FFmpeg accepts it.
/// </summary>
public class DrawTextEscaperTests
{
    [Fact]
    public void Plain_text_passes_through_unchanged()
    {
        DrawTextEscaper.Escape("Recording on TRADER-01").ShouldBe("Recording on TRADER-01");
    }

    // Expected escape depth differs per character because each of FFmpeg's parser
    // layers has its own reserved set — see DrawTextEscaper's remarks. These exact
    // forms were validated against the real ffmpeg binary, not just against docs.
    [Theory]
    [InlineData(":", @"\\:")]
    [InlineData("\\", @"\\\\")]
    [InlineData("'", @"\\\'")]
    [InlineData(",", @"\,")]
    [InlineData(";", @"\;")]
    [InlineData("[", @"\[")]
    [InlineData("]", @"\]")]
    public void Each_special_character_is_escaped(string input, string expected)
    {
        DrawTextEscaper.Escape(input).ShouldBe(expected);
    }

    [Fact]
    public void A_windows_path_with_time_survives()
    {
        DrawTextEscaper.Escape(@"C:\work at 10:30")
            .ShouldBe(@"C\\:\\\\work at 10\\:30");
    }

    [Fact]
    public void Expansion_sequences_are_left_usable()
    {
        // %{localtime} must stay intact so overlays can show a live clock.
        DrawTextEscaper.Escape("%{localtime}").ShouldBe("%{localtime}");
    }

    [Fact]
    public void Font_paths_use_forward_slashes_with_the_drive_colon_escaped()
    {
        DrawTextEscaper.EscapePath(@"C:\Windows\Fonts\consola.ttf")
            .ShouldBe(@"C\\:/Windows/Fonts/consola.ttf");
    }
}
