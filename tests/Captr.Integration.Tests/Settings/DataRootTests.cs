using Captr.Core.Common;
using Captr.Core.Ipc;
using Captr.Core.Settings;

using Shouldly;

namespace Captr.Integration.Tests.Settings;

/// <summary>
/// CAPTR_DATA_ROOT moves everything Captr keeps — and its recorder — somewhere else,
/// so a relocated Captr shares nothing with the user's own.
/// </summary>
/// <remarks>
/// The end-to-end suites depend on this to drive the real, installed product without
/// touching the developer's settings or talking to the developer's own running
/// recorder. Before it existed, tests that drove the real CLI had to back up and
/// restore the one real settings file by hand — and once, when its location moved,
/// silently destroyed it on every run.
/// </remarks>
[Trait("Category", "Os")]
public sealed class DataRootTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "captr-data-root-" + Guid.NewGuid().ToString("N")[..8]);

    public DataRootTests() => Environment.SetEnvironmentVariable(CaptrPaths.DataRootVariable, _root);

    public void Dispose() => Environment.SetEnvironmentVariable(CaptrPaths.DataRootVariable, SuiteDataRoot.Path);

    [Fact]
    public void Every_piece_of_state_follows_the_relocated_root()
    {
        CaptrPaths.IsRelocated.ShouldBeTrue();
        SettingsStore.DefaultSettingsPath().ShouldBe(Path.Combine(_root, "settings.json"));
        CaptrSettings.CreateDefault().WorkingFolder.ShouldBe(Path.Combine(_root, "Sessions"));
        CaptrPaths.Logs.ShouldBe(Path.Combine(_root, "logs"));
    }

    [Fact]
    public void A_relocated_captr_has_a_recorder_of_its_own()
    {
        string relocated = IpcProtocol.PipeName();
        Environment.SetEnvironmentVariable(CaptrPaths.DataRootVariable, null);
        string normal = IpcProtocol.PipeName();

        relocated.ShouldNotBe(normal);
        relocated.ShouldStartWith(normal);
    }

    [Fact]
    public void Without_the_variable_everything_is_where_it_always_was()
    {
        Environment.SetEnvironmentVariable(CaptrPaths.DataRootVariable, null);

        CaptrPaths.DataRoot.ShouldBe(Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Captr"));
        CaptrPaths.InstanceSuffix.ShouldBeNull();
    }
}
