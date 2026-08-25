using System.Windows.Controls;

using Captr.Core.Naming;

namespace Captr.App.Views;

/// <summary>
/// The end-user reference page: what each naming token does, how date formats work,
/// and where to go when something breaks. This page exists so the forms do not have
/// to explain tokens inline — a field carries a one-line pointer here, and the full
/// story lives in one place instead of being repeated under every folder box.
/// </summary>
public partial class HelpPage : Page
{
    /// <summary>One row of the token table.</summary>
    public sealed record TokenRow(string Token, string Meaning, string Example);

    public HelpPage()
    {
        InitializeComponent();

        // The rows are built FROM OutputNamer.SupportedTokens rather than typed out
        // here, so a token added to the recorder shows up on this page automatically
        // — undocumented, loudly, rather than invisibly missing.
        TokenList.ItemsSource = OutputNamer.SupportedTokens
            .Select(token => new TokenRow(token, MeaningOf(token), ExampleOf(token)))
            .ToList();

        FormatExamples.Text =
            "{date:yyyy-MM-dd}   →  2026-08-24\n" +
            "{date:yyyy_MM_dd}   →  2026_08_24\n" +
            "{start:HH-mm}       →  14-30\n" +
            "{date:yyyy-MM}      →  2026-08   (one folder per month, when used in a folder path)";
    }

    private static string MeaningOf(string token) => token switch
    {
        "{date}" => "The date the recording started.",
        "{start}" => "The time the recording started.",
        "{end}" => "The time the recording stopped.",
        "{duration}" => "How long the recording ran.",
        "{machine}" => "This computer's name.",
        "{user}" => "The Windows user who recorded.",
        "{label}" => "The label given when recording started, if any.",
        _ => "New token — not documented yet (tell whoever maintains Captr).",
    };

    private static string ExampleOf(string token) => token switch
    {
        "{date}" => "2026-08-24",
        "{start}" => "14-30-05",
        "{end}" => "15-00-12",
        "{duration}" => "30m15s",
        "{machine}" => Environment.MachineName,
        "{user}" => Environment.UserName,
        "{label}" => "weekly-review",
        _ => "",
    };
}
