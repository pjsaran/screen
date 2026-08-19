using Captr.Core.Naming;

using Shouldly;

namespace Captr.Core.Tests.Naming;

/// <summary>
/// Output naming (SPEC §14): every token, collision handling, illegal and reserved
/// names.
/// </summary>
public class OutputNamerTests
{
    private static readonly NamingContext Context = new()
    {
        // 16:30 UTC on 2026-08-19 = 09:30 Pacific (DST) — proves local conversion.
        StartUtc = new DateTimeOffset(2026, 8, 19, 16, 30, 5, TimeSpan.Zero),
        EndUtc = new DateTimeOffset(2026, 8, 19, 18, 45, 10, TimeSpan.Zero),
        MachineName = "TRADER-01",
        UserName = "psaran",
        TimeZone = TimeZoneInfo.FindSystemTimeZoneById("Pacific Standard Time"),
        Label = "morning-session",
    };

    [Theory]
    [InlineData("{date}", "2026-08-19.mkv")]
    [InlineData("{start}", "09-30-05.mkv")]
    [InlineData("{end}", "11-45-10.mkv")]
    [InlineData("{duration}", "2h15m.mkv")]
    [InlineData("{machine}", "TRADER-01.mkv")]
    [InlineData("{user}", "psaran.mkv")]
    [InlineData("{label}", "morning-session.mkv")]
    public void Each_token_substitutes_correctly(string pattern, string expected)
    {
        OutputNamer.BuildFileName(pattern, Context).ShouldBe(expected);
    }

    [Fact]
    public void Times_are_rendered_in_the_sessions_local_timezone()
    {
        // 16:30 UTC is 09:30 in Pacific daylight time — a UTC-rendered name would say 16-30-05.
        OutputNamer.BuildFileName("{start}", Context).ShouldBe("09-30-05.mkv");
    }

    [Theory]
    [InlineData(0, 0, 45, "45s.mkv")]
    [InlineData(0, 12, 30, "12m30s.mkv")]
    [InlineData(1, 5, 0, "1h05m.mkv")]
    public void Durations_format_compactly(int h, int m, int s, string expected)
    {
        var context = Context with { EndUtc = Context.StartUtc + new TimeSpan(h, m, s) };
        OutputNamer.BuildFileName("{duration}", context).ShouldBe(expected);
    }

    [Fact]
    public void A_missing_label_becomes_empty_not_the_literal_token()
    {
        var context = Context with { Label = null };
        OutputNamer.BuildFileName("x{label}y", context).ShouldBe("xy.mkv");
    }

    [Fact]
    public void An_empty_pattern_falls_back_to_the_default()
    {
        string name = OutputNamer.BuildFileName("  ", Context);
        name.ShouldBe("TRADER-01 2026-08-19 09-30-05.mkv");
    }

    [Theory]
    [InlineData("a<b>c:d\"e/f\\g|h?i*j", "a_b_c_d_e_f_g_h_i_j.mkv")]
    [InlineData("trailing dots...", "trailing dots.mkv")]
    [InlineData("trailing spaces   ", "trailing spaces.mkv")]
    public void Illegal_characters_and_trailing_junk_are_sanitised(string pattern, string expected)
    {
        OutputNamer.BuildFileName(pattern, Context).ShouldBe(expected);
    }

    [Theory]
    [InlineData("CON", "_CON.mkv")]
    [InlineData("con", "_con.mkv")]
    [InlineData("PRN", "_PRN.mkv")]
    [InlineData("COM7", "_COM7.mkv")]
    [InlineData("LPT1", "_LPT1.mkv")]
    [InlineData("NUL", "_NUL.mkv")]
    public void Reserved_device_names_are_prefixed_not_rejected(string pattern, string expected)
    {
        OutputNamer.BuildFileName(pattern, Context).ShouldBe(expected);
    }

    [Fact]
    public void A_pattern_that_sanitises_to_nothing_still_yields_a_usable_name()
    {
        OutputNamer.BuildFileName("...", Context).ShouldBe("recording.mkv");
    }

    [Fact]
    public void Collisions_suffix_rather_than_overwrite()
    {
        string dir = Directory.CreateTempSubdirectory("captr-naming-").FullName;
        try
        {
            File.WriteAllText(Path.Combine(dir, "rec.mkv"), "");
            File.WriteAllText(Path.Combine(dir, "rec (2).mkv"), "");

            string resolved = OutputNamer.ResolveCollision(dir, "rec.mkv");

            resolved.ShouldBe(Path.Combine(dir, "rec (3).mkv"));
            File.Exists(resolved).ShouldBeFalse();
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public void No_collision_means_the_original_name_is_used()
    {
        string dir = Directory.CreateTempSubdirectory("captr-naming-").FullName;
        try
        {
            OutputNamer.ResolveCollision(dir, "rec.mkv").ShouldBe(Path.Combine(dir, "rec.mkv"));
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }
}
