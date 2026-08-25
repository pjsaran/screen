using Captr.Core.Naming;

using Shouldly;

namespace Captr.Core.Tests.Naming;

/// <summary>
/// Optional formats on the date and time tokens — {date:yyyy_MM_dd} — so a pattern
/// can use underscores here and hyphens there, rather than being stuck with whatever
/// separator Captr happens to prefer.
/// </summary>
public class TokenFormatTests
{
    private static readonly NamingContext Context = new()
    {
        // 16:30 UTC on 2026-08-19 = 09:30 Pacific (DST) — proves local conversion
        // still applies when a custom format is used.
        StartUtc = new DateTimeOffset(2026, 8, 19, 16, 30, 5, TimeSpan.Zero),
        EndUtc = new DateTimeOffset(2026, 8, 19, 18, 45, 10, TimeSpan.Zero),
        MachineName = "TRADER-01",
        UserName = "psaran",
        TimeZone = TimeZoneInfo.FindSystemTimeZoneById("Pacific Standard Time"),
        Label = "morning",
    };

    [Theory]
    [InlineData("{date:yyyy_MM_dd}", "2026_08_19.mkv")]
    [InlineData("{start:HH_mm_ss}", "09_30_05.mkv")]
    [InlineData("{end:HH_mm_ss}", "11_45_10.mkv")]
    [InlineData("{date:yyyyMMdd}", "20260819.mkv")]
    [InlineData("{date:dd.MM.yyyy}", "19.08.2026.mkv")]
    [InlineData("{date:yyyy_MM_dd}-{start:HH_mm}", "2026_08_19-09_30.mkv")]
    public void A_format_after_the_colon_chooses_the_separator(string pattern, string expected) =>
        OutputNamer.BuildFileName(pattern, Context).ShouldBe(expected);

    [Fact]
    public void Omitting_the_format_keeps_the_original_behaviour()
    {
        // The whole point of a default: every pattern written before formats existed
        // must still produce exactly the same name.
        OutputNamer.BuildFileName("{machine} {date} {start}", Context)
            .ShouldBe("TRADER-01 2026-08-19 09-30-05.mkv");
    }

    [Fact]
    public void A_custom_format_still_renders_in_the_session_s_own_timezone()
    {
        // 16:30 UTC is 09:30 Pacific. A format must not accidentally switch to UTC.
        OutputNamer.BuildFileName("{start:HH}", Context).ShouldBe("09.mkv");
    }

    [Theory]
    [InlineData("{date:yyyy_MM_dd}")]
    [InlineData("{start:HH_mm_ss}")]
    [InlineData("{machine} {date} {start}")]
    [InlineData("")]
    public void A_usable_pattern_reports_no_problem(string pattern) =>
        OutputNamer.DescribePatternProblem(pattern).ShouldBeNull();

    [Fact]
    public void A_colon_in_the_format_is_refused_with_advice_rather_than_silently_mangled()
    {
        // The obvious trap: a colon is the natural time separator and is illegal in a
        // Windows file name. Sanitisation would turn it into '_' behind the user's
        // back, so it is rejected up front instead.
        string problem = OutputNamer.DescribePatternProblem("{start:HH:mm:ss}").ShouldNotBeNull();

        problem.ShouldContain("not allowed in a file name");
        problem.ShouldContain("_");
    }

    [Fact]
    public void An_invalid_format_string_is_reported_with_an_example()
    {
        string problem = OutputNamer.DescribePatternProblem(@"{date:\}").ShouldNotBeNull();

        problem.ShouldContain("not a valid date/time format");
    }

    [Fact]
    public void A_token_that_takes_no_format_says_which_ones_do()
    {
        string problem = OutputNamer.DescribePatternProblem("{machine:upper}").ShouldNotBeNull();

        problem.ShouldContain("does not take a format");
        problem.ShouldContain("{date}");
    }

    [Fact]
    public void An_empty_format_is_reported_rather_than_treated_as_the_default()
    {
        OutputNamer.DescribePatternProblem("{date:}").ShouldNotBeNull()
            .ShouldContain("empty format");
    }

    [Fact]
    public void An_unknown_token_is_still_caught_when_it_carries_a_format()
    {
        OutputNamer.DescribePatternProblem("{whoops:yyyy}").ShouldNotBeNull()
            .ShouldContain("Unknown token");
    }

    [Fact]
    public void An_unknown_token_survives_into_the_name_so_the_mistake_is_visible()
    {
        // Deleting it would leave a plausible-looking name and no clue what went
        // wrong. Validation stops this reaching a real recording.
        OutputNamer.BuildFileName("{whoops}", Context).ShouldBe("{whoops}.mkv");
    }

    // ---- hour formats ------------------------------------------------------------
    //
    // In .NET format strings 'h' is the 12-hour hour and 'H' is the 24-hour one. Both
    // are valid and Captr accepts either; these tests pin down which is which, because
    // they are one keystroke apart and the difference is invisible until an afternoon
    // recording is named as though it were a morning one.

    [Fact]
    public void The_default_time_format_is_the_24_hour_clock()
    {
        var afternoon = Context with
        {
            StartUtc = new DateTimeOffset(2026, 8, 19, 23, 5, 0, TimeSpan.Zero),
        };

        OutputNamer.BuildFileName("{start}", afternoon).ShouldBe("16-05-00.mkv");
    }

    [Fact]
    public void A_capital_H_format_renders_the_afternoon_as_the_afternoon()
    {
        var afternoon = Context with
        {
            StartUtc = new DateTimeOffset(2026, 8, 19, 23, 5, 0, TimeSpan.Zero),
        };

        OutputNamer.BuildFileName("{start:HH_mm}", afternoon).ShouldBe("16_05.mkv");
    }

    [Fact]
    public void A_lowercase_h_format_is_accepted_and_renders_the_12_hour_hour()
    {
        var afternoon = Context with
        {
            StartUtc = new DateTimeOffset(2026, 8, 19, 23, 5, 0, TimeSpan.Zero),
        };

        OutputNamer.DescribePatternProblem("{start:hh_mm}").ShouldBeNull();
        OutputNamer.BuildFileName("{start:hh_mm}", afternoon).ShouldBe("04_05.mkv");
    }

    [Fact]
    public void A_12_hour_format_with_am_pm_is_accepted_too() =>
        OutputNamer.DescribePatternProblem("{start:hh_mm_tt}").ShouldBeNull();

    [Fact]
    public void A_lowercase_h_is_fine_in_a_destination_folder_as_well() =>
        OutputNamer.DescribeFolderPathProblem(@"D:\Rec\{start:hh}").ShouldBeNull();
}
