using Captr.Core.Settings;

namespace Captr.Integration.Tests;

/// <summary>
/// Protects the developer's OWN Captr settings from tests that drive the real CLI.
/// </summary>
/// <remarks>
/// <para>
/// A handful of integration tests exercise the real `captr` executable, which reads
/// and writes the real settings file — there is no way to point the installed CLI at
/// a different one. So those tests must put the file back exactly as they found it,
/// including the case where there was no file at all.
/// </para>
/// <para>
/// <b>This exists because the previous version did not work.</b> Three tests each had
/// their own copy of a backup/restore helper with the settings path written out by
/// hand, and when settings moved from roaming to local application data those copies
/// were not updated. They then backed up a file that did not exist, let the test
/// overwrite the real one, and "restored" by deleting the path they had been
/// watching — silently destroying the developer's working folder, destinations, and
/// hotkeys on every `-Full` run.
/// </para>
/// <para>
/// The fix is not "update the path in three places": it is to have no path here at
/// all. <see cref="SettingsStore.DefaultSettingsPath"/> is the product's own answer,
/// so this can never drift from it again.
/// </para>
/// </remarks>
public sealed class RealSettingsGuard : IDisposable
{
    private readonly string _path;
    private readonly string? _original;

    public RealSettingsGuard()
    {
        _path = SettingsStore.DefaultSettingsPath();
        _original = File.Exists(_path) ? File.ReadAllText(_path) : null;
    }

    /// <summary>Puts the file back byte for byte — or removes it again, if the
    /// machine genuinely had none before the test ran.</summary>
    public void Dispose()
    {
        try
        {
            if (_original is not null)
            {
                Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
                File.WriteAllText(_path, _original);
            }
            else if (File.Exists(_path))
            {
                File.Delete(_path);
            }
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            // Never fail a test in teardown over this, but do not stay quiet either:
            // somebody's real settings are now wrong and they need to know why.
            Console.Error.WriteLine(
                $"WARNING: could not restore {_path} after the test: {exception.Message}");
        }
    }
}
