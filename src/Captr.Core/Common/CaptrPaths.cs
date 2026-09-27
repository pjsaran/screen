using System.Security.Cryptography;
using System.Text;

namespace Captr.Core.Common;

/// <summary>
/// Where Captr keeps its state — settings, logs, the transfer queue, the encoder
/// and display caches, the token cache, and the default working folder — and the
/// names that make one Captr "the same" as another (its pipe and single-instance
/// lock). Owns the one place those are derived, so nothing can disagree about them.
/// </summary>
/// <remarks>
/// <para>
/// Normally everything lives in <c>%LOCALAPPDATA%\Captr</c> (see
/// <see cref="Settings.SettingsStore.DefaultSettingsPath"/> for why local rather than
/// roaming), and every Captr for this user shares one recorder.
/// </para>
/// <para>
/// <see cref="DataRootVariable"/> relocates ALL of it — and, with it, the pipe and
/// the lock — so a copy of Captr started with it set is entirely separate from the
/// user's own: its own settings, its own recorder, nothing shared. The end-to-end
/// test suite relies on this: it installs and drives the real product without
/// touching the developer's configuration, and without ever being answered by the
/// developer's own running recorder. Setting it grants nothing an ordinary user
/// could not already do; it only moves where that user's own files go.
/// </para>
/// </remarks>
public static class CaptrPaths
{
    /// <summary>The environment variable that relocates Captr's state.</summary>
    public const string DataRootVariable = "CAPTR_DATA_ROOT";

    /// <summary>The folder holding everything Captr keeps for this user.</summary>
    public static string DataRoot =>
        Override() ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Captr");

    /// <summary>True when <see cref="DataRootVariable"/> relocated the state.</summary>
    public static bool IsRelocated => Override() is not null;

    /// <summary>Host and window logs.</summary>
    public static string Logs => Path.Combine(DataRoot, "logs");

    /// <summary>
    /// Appended to the pipe and single-instance names when the state is relocated, so
    /// a relocated Captr has a recorder of its own; null otherwise.
    /// </summary>
    public static string? InstanceSuffix
    {
        get
        {
            string? relocated = Override();
            if (relocated is null)
            {
                return null;
            }

            byte[] hash = SHA256.HashData(Encoding.UTF8.GetBytes(relocated.ToUpperInvariant()));
            return "r" + Convert.ToHexStringLower(hash)[..12];
        }
    }

    private static string? Override()
    {
        string? value = Environment.GetEnvironmentVariable(DataRootVariable);
        return string.IsNullOrWhiteSpace(value) ? null : Path.GetFullPath(value);
    }
}
