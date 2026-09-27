using System.Text.RegularExpressions;

using Shouldly;

namespace Captr.Integration.Tests.Build;

/// <summary>
/// Rules for build/installer/captr.iss that cannot be exercised without an elevated,
/// per-machine install on a second account — checked in the script itself. The
/// behaviour they protect is exercised end to end by the Installer category and, for
/// the elevated half, by the release-verification runbook.
/// </summary>
[Trait("Category", "Os")]
public partial class InstallerScriptTests
{
    private static string Script() =>
        File.ReadAllText(BuildScriptHarness.Script(@"build\installer\captr.iss"));

    /// <summary>The body of one Pascal function or procedure.</summary>
    private static string Body(string script, string name)
    {
        Match match = Regex.Match(script, $@"(function|procedure) {name}\b.*?\nend;", RegexOptions.Singleline);
        match.Success.ShouldBeTrue($"{name} not found in captr.iss");
        return match.Value;
    }

    [Fact]
    public void The_installed_cli_is_only_ever_run_as_the_person_who_started_setup()
    {
        // An elevated setup used to Exec the captr.exe named by an uninstall key it
        // reads from HKCU first - a place any program the user runs can write. That
        // ran an arbitrary captr.exe as administrator.
        string script = Script();

        Body(script, "RunInstalledCli").ShouldContain("ExecAsOriginalUser(Cli");
        foreach (string caller in new[] { "RecorderState", "TryStopRecording" })
        {
            Body(script, caller).ShouldNotContain("Exec(Cli");
        }
    }

    [Fact]
    public void Setup_never_ends_other_accounts_recorders_unless_told_to()
    {
        // "taskkill /F /IM Captr.App.exe", elevated, ended every account's recorder -
        // including recordings the recording check could not see.
        string prepare = Body(Script(), "PrepareToInstall");

        prepare.ShouldContain("ExecAsOriginalUser(ExpandConstant('{sys}\\taskkill.exe')");
        int forced = prepare.IndexOf("FORCESTOP", StringComparison.Ordinal);
        int elevatedKill = prepare.IndexOf("    Exec(ExpandConstant('{sys}\\taskkill.exe')", StringComparison.Ordinal);
        forced.ShouldBeGreaterThan(0);
        elevatedKill.ShouldBeGreaterThan(forced, "the machine-wide kill happens only behind /FORCESTOP=yes");
        Body(Script(), "CurStepChanged").ShouldNotContain("taskkill");
    }

    [Fact]
    public void A_silent_install_never_waits_for_a_message_box()
    {
        // SuppressibleMsgBox only answers by itself with /SUPPRESSMSGBOXES; a plain
        // /VERYSILENT upgrade waited for ever on an invisible "Upgrade now?".
        string script = Script();

        Body(script, "ConfirmAgainstInstalledVersion").ShouldContain("if WizardSilent() then begin");
        Body(script, "InitializeSetup").ShouldNotContain("SuppressibleMsgBox");
        Body(script, "Tell").ShouldContain("if not WizardSilent() then");
    }

    [Fact]
    public void Path_changes_are_announced_to_running_programs()
    {
        Script().ShouldContain("ChangesEnvironment=yes");
    }

    [Fact]
    public void Uninstall_refuses_under_a_running_recording()
    {
        Body(Script(), "InitializeUninstall").ShouldContain("FORCESTOP");
    }
}
