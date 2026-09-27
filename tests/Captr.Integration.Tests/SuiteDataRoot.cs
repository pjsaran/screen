using System.Runtime.CompilerServices;

using Captr.Core.Common;

namespace Captr.Integration.Tests;

/// <summary>
/// Gives the whole integration suite a data root of its own, before any test runs.
/// Every process a test starts — the CLI, the recorder, the window — inherits it.
/// </summary>
/// <remarks>
/// The end-to-end suites already relocated themselves, but the older tests that
/// drive the real CLI and recorder did not: they read and wrote the developer's own
/// %LOCALAPPDATA%\Captr\settings.json (one left it pointing at a deleted temp
/// folder) and talked to the developer's own running recorder, if there was one.
/// Tests that need a root of their own still set one per process; tests that check
/// the unrelocated default clear the variable and restore <see cref="Path"/> after.
/// </remarks>
internal static class SuiteDataRoot
{
    /// <summary>The suite's data root for this run.</summary>
    public static string Path { get; private set; } = "";

    [ModuleInitializer]
    [System.Diagnostics.CodeAnalysis.SuppressMessage("Usage", "CA2255:The 'ModuleInitializer' attribute should not be used in libraries",
        Justification = "A test assembly: this must run before the first test touches Captr's paths, whichever test that is.")]
    internal static void Relocate()
    {
        Path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "captr-suite-" + Environment.ProcessId);
        Environment.SetEnvironmentVariable(CaptrPaths.DataRootVariable, Path);

        // Best effort: a recorder a test started may still be in its idle linger
        // and holding a log open; whatever is left is only temp files.
        AppDomain.CurrentDomain.ProcessExit += (_, _) =>
        {
            try
            {
                Directory.Delete(Path, recursive: true);
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
            }
        };
    }
}
