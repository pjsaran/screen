using System.Net.Http;
using System.Text;
using System.Windows;
using System.Windows.Controls;

using Captr.Core.Naming;
using Captr.Core.Secrets;
using Captr.Core.Settings;

namespace Captr.App.Views;

/// <summary>
/// Adds or edits one transfer destination. Owns the per-kind field layout — folder
/// destinations and SharePoint destinations need entirely different information, and
/// showing all of it at once would be the least usable possible form.
/// </summary>
/// <remarks>
/// <para>
/// The kind list comes from <see cref="DestinationKinds"/> and includes the kinds
/// Captr does NOT yet implement. That is deliberate: people ask "can it upload to
/// S3?", and the honest answer belongs where they look for it. Choosing one shows a
/// plain "not built yet" notice and refuses to save, which is far better than the
/// option not existing and the question going unanswered.
/// </para>
/// <para>
/// <b>The SharePoint secret is typed here and stored immediately in Windows
/// Credential Manager</b> — the user never invents or types a "credential name", and
/// the secret never touches settings.json (SPEC §7). The entry name is derived from
/// the destination's name by <see cref="DestinationSettings.CredentialNameFor"/>. The
/// secret is write-only from the UI's point of view: it can be replaced, never read
/// back, which is why an existing destination shows only whether one is stored.
/// </para>
/// </remarks>
public partial class DestinationEditorWindow
{
    private readonly DestinationSettings? _editing;

    /// <summary>True once the user has typed into the secret box, so an untouched box
    /// on an existing destination means "keep the stored secret" rather than "clear
    /// it".</summary>
    private bool _secretEntered;

    /// <summary>The destination the user built, once the dialog returns true.</summary>
    public DestinationSettings? Result { get; private set; }

    /// <param name="existing">The destination being edited, or null to add a new one.</param>
    public DestinationEditorWindow(DestinationSettings? existing = null)
    {
        _editing = existing;
        InitializeComponent();

        // A SharePoint destination has enough fields to outgrow a 1080p screen, which
        // put the Save button underneath the taskbar where nobody could reach it.
        // Capping against the work area (which excludes the taskbar) lets the fields
        // scroll instead; the buttons stay pinned outside the scrolling region.
        MaxHeight = System.Windows.SystemParameters.WorkArea.Height - 60;

        KindBox.ItemsSource = DestinationKinds.All;

        if (existing is null)
        {
            KindBox.SelectedIndex = 0;
        }
        else
        {
            Title = "Edit destination";
            KindBox.SelectedItem = DestinationKinds.Describe(existing.Kind);
            NameBox.Text = existing.Name;
            FolderBox.Text = existing.FolderPath ?? "";
            SiteBox.Text = existing.SharePointSiteUrl ?? "";
            DriveIdBox.Text = existing.SharePointDriveId ?? "";
            SharePointFolderBox.Text = existing.SharePointFolder ?? "";
            TenantBox.Text = existing.TenantId ?? "";
            ClientBox.Text = existing.ClientId ?? "";
            PatternBox.Text = existing.FileNamePattern ?? "";
            EnabledBox.IsChecked = existing.Enabled;
        }

        // Lay the fields out for whichever kind is now selected. This must be an
        // explicit call: editing a destination whose kind happens to be the one
        // already selected changes nothing, so SelectionChanged never fires and the
        // per-kind fields would stay in their XAML default state.
        ApplySelectedKind();
    }

    private DestinationKindInfo SelectedKind =>
        KindBox.SelectedItem as DestinationKindInfo ?? DestinationKinds.All[0];

    private void OnKindChanged(object sender, SelectionChangedEventArgs e)
    {
        if (FolderFields is not null)
        {
            ApplySelectedKind();
        }
    }

    /// <summary>Shows only the fields the chosen kind actually needs, and refuses to
    /// save a kind Captr has not built.</summary>
    private void ApplySelectedKind()
    {
        DestinationKindInfo kind = SelectedKind;
        KindHint.Text = kind.Description;

        FolderFields.Visibility = Visible(kind.Kind == DestinationKind.Folder);
        SharePointFields.Visibility = Visible(kind.Kind == DestinationKind.SharePoint);

        // Test connection proves SharePoint sign-in details; the other kinds have
        // nothing comparable to test, so the button leaves rather than greys.
        TestButton.Visibility = Visible(kind.Kind == DestinationKind.SharePoint);
        TestResultText.Visibility = Visibility.Collapsed;

        ComingSoonNotice.Visibility = Visible(!kind.Implemented);
        ComingSoonText.Text = kind.Implemented
            ? ""
            : $"{kind.DisplayName} is on the roadmap but is not built yet, so it cannot be saved as a destination. " +
              "A local or network folder and SharePoint both work today.";

        SaveButton.IsEnabled = kind.Implemented;
        UpdateSecretStatus();
        HideError();
    }

    private void OnSecretChanged(object sender, RoutedEventArgs e)
    {
        _secretEntered = SecretBox.Password.Length > 0;
        UpdateSecretStatus();
    }

    /// <summary>
    /// Says whether a secret is stored, never what it is (SPEC §7: confirmations are
    /// write-only). An existing destination with a stored secret explains that leaving
    /// the box empty keeps it, so nobody has to retype a secret to change a folder.
    /// </summary>
    private void UpdateSecretStatus()
    {
        if (SecretStatus is null)
        {
            return;
        }

        if (_secretEntered)
        {
            SecretStatus.Text = "Stored safely in Windows Credential Manager when you save — never in a file.";
            return;
        }

        // Check BOTH the entry settings point at and the one this destination's name
        // derives to. They can differ — a secret stored during an edit that was never
        // saved leaves the vault holding one that settings.json does not name — and
        // reporting "no secret stored" while one plainly is looks like the secret was
        // never taken.
        bool stored = (_editing?.CredentialName is { } recorded && CredentialVault.Exists(recorded))
            || (NameBox.Text.Trim().Length > 0
                && CredentialVault.Exists(DestinationSettings.CredentialNameFor(NameBox.Text)));

        SecretStatus.Text = stored
            ? "A secret is stored. Leave empty to keep it, or type a new one to replace it."
            : "The app registration's secret Value. Stored safely, never in a file.";
    }

    private void OnBrowseClicked(object sender, RoutedEventArgs e)
    {
        var dialog = new Microsoft.Win32.OpenFolderDialog
        {
            Title = "Choose the destination folder",
            InitialDirectory = Directory.Exists(FolderBox.Text) ? FolderBox.Text : "",
        };

        if (dialog.ShowDialog(this) == true)
        {
            FolderBox.Text = dialog.FolderName;
        }
    }

    private void OnSaveClicked(object sender, RoutedEventArgs e)
    {
        DestinationKindInfo kind = SelectedKind;
        string name = NameBox.Text.Trim();

        if (string.IsNullOrWhiteSpace(name))
        {
            ShowError("Give this destination a name — it is how the Transfers page identifies the transfer.");
            return;
        }

        if (kind.Kind == DestinationKind.Folder && string.IsNullOrWhiteSpace(FolderBox.Text))
        {
            ShowError("Choose the folder that finished recordings should be copied to.");
            return;
        }

        if (kind.Kind == DestinationKind.SharePoint && string.IsNullOrWhiteSpace(SiteBox.Text))
        {
            ShowError("Enter the SharePoint site URL, e.g. https://contoso.sharepoint.com/sites/Recordings.");
            return;
        }

        // Uploads are addressed by drive id — without one, every transfer would fail
        // with Graph's "invalid drive id". Refusing here, with the place to find it,
        // beats saving a destination that cannot work.
        if (kind.Kind == DestinationKind.SharePoint && string.IsNullOrWhiteSpace(DriveIdBox.Text))
        {
            ShowError("Enter the Drive ID — the document library's id from GET /sites/{site-id}/drives.");
            return;
        }

        if (OutputNamer.DescribePatternProblem(PatternBox.Text) is { } patternProblem)
        {
            ShowError(patternProblem);
            return;
        }

        if (OutputNamer.DescribeFolderPathProblem(
                kind.Kind == DestinationKind.Folder ? FolderBox.Text : SharePointFolderBox.Text) is { } folderProblem)
        {
            ShowError(folderProblem);
            return;
        }

        string? credentialName = kind.Kind == DestinationKind.SharePoint
            ? ResolveCredential(name)
            : null;

        Result = (_editing ?? new DestinationSettings { Name = name, Kind = kind.Kind }) with
        {
            Name = name,
            Kind = kind.Kind,
            Enabled = EnabledBox.IsChecked == true,
            FileNamePattern = Trimmed(PatternBox.Text),
            FolderPath = Trimmed(FolderBox.Text),
            SharePointSiteUrl = Trimmed(SiteBox.Text),
            SharePointDriveId = Trimmed(DriveIdBox.Text),
            SharePointFolder = Trimmed(SharePointFolderBox.Text),
            TenantId = Trimmed(TenantBox.Text),
            ClientId = Trimmed(ClientBox.Text),
            CredentialName = credentialName,
        };

        DialogResult = true;
    }

    /// <summary>
    /// Settles this destination's stored secret and returns the Credential Manager
    /// entry name to record in settings.
    /// </summary>
    /// <remarks>
    /// Three cases, and all three used to be handled wrongly:
    /// <list type="number">
    ///   <item><description><b>A secret was typed.</b> It replaces whatever was
    ///   stored, under the name the destination has NOW.</description></item>
    ///   <item><description><b>Nothing typed, and the destination was renamed.</b>
    ///   The stored secret MOVES to the new name. Previously the settings kept
    ///   pointing at the old entry, so the credential name and the destination name
    ///   drifted apart and stayed apart.</description></item>
    ///   <item><description><b>Nothing typed, same name.</b> Keep what is
    ///   stored.</description></item>
    /// </list>
    /// </remarks>
    private string? ResolveCredential(string destinationName)
    {
        string wanted = DestinationSettings.CredentialNameFor(destinationName);

        if (_secretEntered)
        {
            // The vault takes ownership of the buffer and zeroes it (SPEC §7).
            // Clearing the box keeps the plaintext out of the visual tree as well.
            CredentialVault.Store(wanted, Encoding.UTF8.GetBytes(SecretBox.Password));
            SecretBox.Clear();
            _secretEntered = false;

            // A rename WITH a new secret leaves the old entry behind; take it out
            // now rather than leaving a stale secret in the user's vault.
            if (_editing?.CredentialName is { } previous && previous != wanted)
            {
                CredentialVault.Delete(previous);
            }

            return wanted;
        }

        string? stored = _editing?.CredentialName;
        if (stored is not null && stored != wanted && CredentialVault.Rename(stored, wanted))
        {
            return wanted;
        }

        return CredentialVault.Exists(wanted) ? wanted : stored;
    }

    /// <summary>
    /// Proves the SharePoint details in the boxes RIGHT NOW — before anything is
    /// saved. A typed-but-unsaved secret is used directly and stored nowhere; with
    /// the box untouched, the stored secret (if any) is used, so an existing
    /// destination can be tested without retyping anything.
    /// </summary>
    private async void OnTestConnectionClicked(object sender, RoutedEventArgs e)
    {
        HideError();
        TestResultText.Visibility = Visibility.Collapsed;

        // The candidate the test would sign in as: the boxes as they stand, plus
        // whichever vault entry this destination's name points at.
        string name = NameBox.Text.Trim();
        var candidate = new DestinationSettings
        {
            Name = name.Length > 0 ? name : "unsaved destination",
            Kind = DestinationKind.SharePoint,
            SharePointSiteUrl = Trimmed(SiteBox.Text),
            SharePointDriveId = Trimmed(DriveIdBox.Text),
            TenantId = Trimmed(TenantBox.Text),
            ClientId = Trimmed(ClientBox.Text),
            CredentialName = _editing?.CredentialName
                ?? (name.Length > 0 ? DestinationSettings.CredentialNameFor(name) : null),
        };

        byte[]? typedSecret = _secretEntered ? Encoding.UTF8.GetBytes(SecretBox.Password) : null;

        TestButton.IsEnabled = false;
        TestResultText.Text = "Testing…";
        TestResultText.Visibility = Visibility.Visible;
        try
        {
            string result = await Captr.Core.Transfers.SharePointConnectionTest.RunAsync(
                candidate, typedSecret, graphHttp: null, CancellationToken.None);
            TestResultText.Text = result;
        }
        catch (Captr.Core.Transfers.TransferException exception)
        {
            TestResultText.Visibility = Visibility.Collapsed;
            ShowError(exception.Message);
        }
        catch (HttpRequestException exception)
        {
            TestResultText.Visibility = Visibility.Collapsed;
            ShowError("Could not reach Microsoft Graph: " + exception.Message);
        }
        finally
        {
            // The plaintext copy made for the test is not left in memory.
            if (typedSecret is not null)
            {
                Array.Clear(typedSecret);
            }

            TestButton.IsEnabled = true;
        }
    }

    private void OnCancelClicked(object sender, RoutedEventArgs e) => DialogResult = false;

    private void ShowError(string message)
    {
        ErrorText.Text = message;
        ErrorText.Visibility = Visibility.Visible;
    }

    private void HideError() => ErrorText.Visibility = Visibility.Collapsed;

    /// <summary>Empty boxes are stored as null, not "", so an unused field is
    /// genuinely absent from settings.json rather than present and blank.</summary>
    private static string? Trimmed(string value) =>
        string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    private static Visibility Visible(bool visible) => visible ? Visibility.Visible : Visibility.Collapsed;
}
