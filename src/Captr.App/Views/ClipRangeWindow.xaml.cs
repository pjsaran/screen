using System.Globalization;
using System.Windows;

using Captr.Core.Sessions;

namespace Captr.App.Views;

/// <summary>
/// Asks which segment range to extract (SPEC §9: "extracting a clip at segment
/// boundaries by stream copy"). Owns nothing but the choice — it shows only real
/// boundaries, so an impossible range cannot be requested.
/// </summary>
public partial class ClipRangeWindow : Window
{
    /// <summary>Chosen first segment (1-based); valid once the dialog returns true.</summary>
    public int FirstSegment { get; private set; }

    /// <summary>Chosen last segment (inclusive).</summary>
    public int LastSegment { get; private set; }

    public ClipRangeWindow(string sessionFolder)
    {
        InitializeComponent();

        IReadOnlyList<ClipCandidate> candidates = SegmentClipper.ListSegments(sessionFolder);
        SegmentList.ItemsSource = candidates.Select(c => new
        {
            c.Index,
            c.FileName,
            StartText = c.StartOffset.ToString(@"hh\:mm\:ss", CultureInfo.InvariantCulture),
            DurationText = c.Duration.ToString(@"hh\:mm\:ss", CultureInfo.InvariantCulture),
        }).ToList();

        // Sensible default: the whole recording.
        FromBox.Text = "1";
        ToBox.Text = candidates.Count.ToString(CultureInfo.InvariantCulture);
        ExtractButton.IsEnabled = candidates.Count > 0;
    }

    private void OnExtractClicked(object sender, RoutedEventArgs e)
    {
        if (!int.TryParse(FromBox.Text, out int first) || !int.TryParse(ToBox.Text, out int last) || first > last)
        {
            MessageBox.Show(this, "Enter a valid segment range, first number no greater than the last.",
                "Extract clip", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        FirstSegment = first;
        LastSegment = last;
        DialogResult = true;
    }
}
