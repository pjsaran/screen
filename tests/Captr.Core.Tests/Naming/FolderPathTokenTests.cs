using Captr.Core.Naming;

using Shouldly;

namespace Captr.Core.Tests.Naming;

/// <summary>
/// Tokens inside a DESTINATION FOLDER, so a transfer can land in
/// <c>\\archive\recordings\2026-08\</c> without anyone creating the folder by hand.
/// The same tokens as a file name, with two extra rules: the separators the user
/// wrote are structure and must survive untouched, and a separator a TOKEN produces
/// must not be, because that would invent a folder level nobody asked for.
/// </summary>
public class FolderPathTokenTests
{
    private static readonly NamingContext Context = new()
    {
        // 16:30 UTC on 2026-08-19 = 09:30 Pacific (DST), so a wrong timezone shows up
        // as the wrong DAY at the boundary rather than merely the wrong hour.
        StartUtc = new DateTimeOffset(2026, 8, 19, 16, 30, 5, TimeSpan.Zero),
        EndUtc = new DateTimeOffset(2026, 8, 19, 18, 45, 10, TimeSpan.Zero),
        MachineName = "TRADER-01",
        UserName = "psaran",
        TimeZone = TimeZoneInfo.FindSystemTimeZoneById("Pacific Standard Time"),
        Label = "morning",
    };

    [Theory]
    [InlineData(@"D:\Recordings\{date:yyyy-MM}", @"D:\Recordings\2026-08")]
    [InlineData(@"D:\Recordings\{date:yyyy}\{date:MM}", @"D:\Recordings\2026\08")]
    [InlineData(@"\\nas\video\{machine}", @"\\nas\video\TRADER-01")]
    [InlineData("Shared Documents/Captr/{date:yyyy_MM_dd}", "Shared Documents/Captr/2026_08_19")]
    [InlineData(@"D:\Recordings\{user}\{label}", @"D:\Recordings\psaran\morning")]
    public void Tokens_expand_inside_a_folder_and_the_separators_are_left_alone(string pattern, string expected) =>
        OutputNamer.ExpandFolderPath(pattern, Context).ShouldBe(expected);

    [Fact]
    public void A_folder_with_no_tokens_is_returned_exactly_as_written()
    {
        // Including a trailing slash, a UNC prefix, and a drive colon — none of which
        // the sanitiser may touch, because all three are path structure.
        OutputNamer.ExpandFolderPath(@"\\server\share\recordings\", Context)
            .ShouldBe(@"\\server\share\recordings\");
    }

    [Fact]
    public void A_token_can_never_introduce_a_separator_of_its_own()
    {
        // A format producing '/' would otherwise create a folder level nobody asked
        // for. Validation rejects this before it is ever saved; this is the belt to
        // that pair of braces.
        OutputNamer.ExpandFolderPath(@"D:\Recordings\{date:yyyy/MM}", Context)
            .ShouldBe(@"D:\Recordings\2026_08");
    }

    [Fact]
    public void The_recordings_own_date_is_used_not_todays()
    {
        // The point of storing the expanded folder on the queue row: a transfer that
        // fails at 23:59 and retries at 00:01 must land where it was queued for.
        OutputNamer.ExpandFolderPath(@"D:\{date:yyyy-MM-dd}", Context)
            .ShouldBe(@"D:\2026-08-19");
    }

    // ---- Validation -------------------------------------------------------------

    [Fact]
    public void A_folder_without_tokens_has_no_problem() =>
        OutputNamer.DescribeFolderPathProblem(@"D:\Recordings").ShouldBeNull();

    [Fact]
    public void An_empty_folder_has_no_problem() =>
        OutputNamer.DescribeFolderPathProblem("").ShouldBeNull();

    [Fact]
    public void An_unknown_token_in_a_folder_is_reported_with_the_ones_that_exist()
    {
        string problem = OutputNamer.DescribeFolderPathProblem(@"D:\{whoops}").ShouldNotBeNull();

        problem.ShouldContain("{whoops}");
        problem.ShouldContain("{date}");
    }

    [Fact]
    public void A_format_that_would_produce_an_illegal_character_is_refused()
    {
        // The obvious trap: a colon is exactly what a time wants and exactly what a
        // path component cannot have.
        string problem = OutputNamer.DescribeFolderPathProblem(@"D:\{start:HH:mm:ss}").ShouldNotBeNull();

        problem.ShouldContain("':'");
    }

    [Fact]
    public void An_illegal_character_in_the_literal_part_is_refused_even_when_the_tokens_are_fine()
    {
        // The tokens here are perfectly valid; the problem is the pipe beside them,
        // which is why the literal text is checked separately.
        string problem = OutputNamer.DescribeFolderPathProblem("D:\\Rec|ordings\\{date}").ShouldNotBeNull();

        problem.ShouldContain("'|'");
    }

    [Fact]
    public void A_valid_token_format_in_a_folder_is_accepted() =>
        OutputNamer.DescribeFolderPathProblem(@"\\nas\video\{date:yyyy}\{machine}").ShouldBeNull();

    [Theory]
    [InlineData(@"Recordings")]
    [InlineData(@"Recordings\{date:yyyy}")]
    [InlineData(@"\Recordings")]
    [InlineData(@"D:Recordings")]
    public void A_folder_destination_must_be_a_full_path(string folder)
    {
        // A relative folder resolved against whatever directory the recorder happened
        // to start in, and was accepted.
        OutputNamer.DescribeFolderPathProblem(folder, isLocalFolder: true).ShouldNotBeNull().ShouldContain("full path");
    }

    [Theory]
    [InlineData(@"D:\Recordings:hidden")]
    [InlineData(@"D:\Recordings\..\..\Windows")]
    [InlineData(@"\\?\D:\Recordings")]
    [InlineData(@"\\.\D:\Recordings")]
    public void A_folder_destination_cannot_hide_in_a_stream_climb_out_or_use_a_device_path(string folder)
    {
        OutputNamer.DescribeFolderPathProblem(folder, isLocalFolder: true).ShouldNotBeNull();
    }

    [Theory]
    [InlineData(@"D:\Recordings\{date:yyyy}")]
    [InlineData(@"\\nas\video\{machine}")]
    public void Ordinary_full_paths_remain_fine(string folder)
    {
        OutputNamer.DescribeFolderPathProblem(folder, isLocalFolder: true).ShouldBeNull();
    }

    [Theory]
    [InlineData("C:/Recordings")]
    [InlineData("Shared Documents/../Other")]
    public void A_SharePoint_folder_is_a_path_inside_the_library(string folder)
    {
        OutputNamer.DescribeFolderPathProblem(folder, isLocalFolder: false).ShouldNotBeNull();
        OutputNamer.DescribeFolderPathProblem("Shared Documents/Captr/{date:yyyy}", isLocalFolder: false).ShouldBeNull();
    }

    [Fact]
    public void A_token_that_renders_to_a_device_name_does_not_create_an_unusable_folder()
    {
        string path = OutputNamer.ExpandFolderPath(@"D:\Recordings\{label}", Context with { Label = "CON" });

        path.ShouldBe(@"D:\Recordings\_CON");
    }
}
