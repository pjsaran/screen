using Captr.Core.Cli;
using Captr.Core.Settings;

using Shouldly;

namespace Captr.Core.Tests.Settings;

/// <summary>
/// `captr settings import` says what it changes about where recordings go. A shared
/// settings file can move the working folder and add destinations - where every
/// future recording ends up - and used to do so with nothing but "Imported and saved."
/// </summary>
public class ImportSummaryTests
{
    private static DestinationSettings Folder(string name, string path) =>
        new() { Name = name, Kind = DestinationKind.Folder, FolderPath = path };

    [Fact]
    public void A_moved_working_folder_and_added_changed_and_removed_destinations_are_all_named()
    {
        CaptrSettings before = CaptrSettings.CreateDefault() with
        {
            WorkingFolder = @"C:\Rec",
            Destinations = [Folder("nas", @"\nas\rec"), Folder("old", @"D:\old")],
        };
        CaptrSettings after = before with
        {
            WorkingFolder = @"\somewhere\else",
            Destinations = [Folder("nas", @"\other\rec"), Folder("new", @"\attacker\share")],
        };

        IReadOnlyList<string> changes = SettingsEditor.DescribeWhereRecordingsGo(before, after);

        changes.ShouldContain(c => c.Contains("Working folder") && c.Contains(@"\somewhere\else"));
        changes.ShouldContain(c => c.Contains("Destination added: new") && c.Contains(@"\attacker\share"));
        changes.ShouldContain(c => c.Contains("Destination changed: nas") && c.Contains(@"\other\rec"));
        changes.ShouldContain("Destination removed: old");
    }

    [Fact]
    public void Nothing_is_said_when_nothing_about_where_recordings_go_changes()
    {
        CaptrSettings settings = CaptrSettings.CreateDefault() with { Destinations = [Folder("nas", @"\nas\rec")] };

        SettingsEditor.DescribeWhereRecordingsGo(settings, settings with { FrameRate = 5 }).ShouldBeEmpty();
    }
}
