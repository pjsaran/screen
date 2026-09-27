using Captr.Core.Naming;

using Shouldly;

namespace Captr.Core.Tests.Naming;

/// <summary>
/// The live preview under a naming box: what the pattern would produce, or what is
/// wrong with it, while the person is still typing.
/// </summary>
public class NamingPreviewTests
{
    private static readonly DateTimeOffset Now = new(2026, 9, 28, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public void A_usable_pattern_shows_the_name_it_would_produce()
    {
        NamingPreview.Result result = NamingPreview.ForFileName("{label} {date:yyyy}", Now);

        result.IsProblem.ShouldBeFalse();
        result.Text.ShouldEndWith("label 2026.mkv");
    }

    [Fact]
    public void A_broken_pattern_shows_the_problem_instead()
    {
        NamingPreview.Result result = NamingPreview.ForFileName("{start:HH:mm}", Now);

        result.IsProblem.ShouldBeTrue();
        result.Text.ShouldContain("not allowed in a file name");
    }

    [Fact]
    public void An_empty_pattern_says_the_default_is_used()
    {
        NamingPreview.ForFileName("", Now).Text.ShouldStartWith("Empty uses the default.");
        NamingPreview.ForDestinationFileName("", Now).Text.ShouldBeEmpty("a destination without a pattern keeps the recording's name");
    }

    [Fact]
    public void A_dated_folder_shows_where_a_recording_would_go()
    {
        NamingPreview.Result result = NamingPreview.ForFolder(@"D:\Recordings\{date:yyyy}", isLocalFolder: true, Now);

        result.IsProblem.ShouldBeFalse();
        result.Text.ShouldEndWith(@"D:\Recordings\2026");
    }

    [Fact]
    public void A_relative_folder_is_flagged_while_typing()
    {
        NamingPreview.ForFolder("Recordings", isLocalFolder: true, Now).IsProblem.ShouldBeTrue();
    }
}
