using System.Security.AccessControl;
using System.Security.Principal;

namespace Captr.Core.Common;

/// <summary>
/// Creates a recording's own folder so that only the person recording (and SYSTEM
/// and Administrators) can open it. Owns the one decision of who may read footage
/// while it is on this PC.
/// </summary>
/// <remarks>
/// <para>
/// Folders were created bare, inheriting whatever the parent allowed. Under
/// %LOCALAPPDATA% that is private, but the settings validator suggests C:\Recordings
/// and the install guide D:\Recordings - and a drive root grants "Authenticated
/// Users: Modify" to everything below it. Every other account on the PC could watch,
/// alter, or delete the recordings, and plant files (a journal, an integrity record,
/// a crafted segment) that the recorder would then trust and feed to FFmpeg.
/// </para>
/// <para>
/// Only the folder Captr creates for ONE recording is protected. The working folder
/// the person chose keeps the permissions they gave it, and an existing folder is
/// never changed. Where Windows will not apply the list (some network shares), the
/// folder is created as before and the reason logged by the caller's own handling:
/// a recording is never refused over this.
/// </para>
/// </remarks>
public static class PrivateFolder
{
    /// <summary>Creates <paramref name="path"/> (and any missing parents, normally) if
    /// it does not exist, with a protected access list: the current user, SYSTEM and
    /// Administrators, full control, inherited by everything inside.</summary>
    public static void Create(string path)
    {
        if (Directory.Exists(path))
        {
            return;
        }

        if (Path.GetDirectoryName(Path.GetFullPath(path)) is { } parent)
        {
            Directory.CreateDirectory(parent);
        }

        try
        {
            new DirectoryInfo(path).Create(OwnerOnly());
        }
        catch (Exception exception) when (exception is UnauthorizedAccessException or PrivilegeNotHeldException
                                               or PlatformNotSupportedException or InvalidOperationException)
        {
            // A share that will not take the list: the recording matters more.
            Directory.CreateDirectory(path);
        }
    }

    private static DirectorySecurity OwnerOnly()
    {
        var security = new DirectorySecurity();
        security.SetAccessRuleProtection(isProtected: true, preserveInheritance: false);

        using WindowsIdentity current = WindowsIdentity.GetCurrent();
        foreach (IdentityReference who in new IdentityReference[]
                 {
                     current.User!,
                     new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null),
                     new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid, null),
                 })
        {
            security.AddAccessRule(new FileSystemAccessRule(
                who, FileSystemRights.FullControl,
                InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit,
                PropagationFlags.None, AccessControlType.Allow));
        }

        return security;
    }
}
