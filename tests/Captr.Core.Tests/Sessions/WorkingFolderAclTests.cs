using System.Security.AccessControl;
using System.Security.Principal;

using Captr.Core.Sessions;

using Shouldly;

namespace Captr.Core.Tests.Sessions;

/// <summary>
/// A recording's folder is private to the person recording, even when the working
/// folder sits somewhere every account may write (a drive root grants
/// "Authenticated Users: Modify" to everything below it).
/// </summary>
public sealed class WorkingFolderAclTests : IDisposable
{
    private readonly string _parent = Directory.CreateTempSubdirectory("captr-acl-").FullName;

    public WorkingFolderAclTests()
    {
        // Make the parent behave like C:\ does for new folders.
        var info = new DirectoryInfo(_parent);
        DirectorySecurity security = info.GetAccessControl();
        security.AddAccessRule(new FileSystemAccessRule(
            new SecurityIdentifier(WellKnownSidType.AuthenticatedUserSid, null), FileSystemRights.Modify,
            InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit, PropagationFlags.None, AccessControlType.Allow));
        info.SetAccessControl(security);
    }

    public void Dispose() => Directory.Delete(_parent, recursive: true);

    private static readonly SecurityIdentifier[] Others =
    [
        new(WellKnownSidType.AuthenticatedUserSid, null),
        new(WellKnownSidType.WorldSid, null),
        new(WellKnownSidType.BuiltinUsersSid, null),
    ];

    [Fact]
    public void A_new_session_folder_grants_nobody_else_access()
    {
        string folder = Path.Combine(_parent, "Sessions", "20260928-120000-abcd1234");

        using (SessionJournal.CreateNew(folder, new SessionStarted
        {
            TimestampUtc = DateTimeOffset.UtcNow,
            SessionId = Guid.NewGuid(),
            LocalTimeZoneId = "UTC",
            MachineName = "M",
            UserName = "U",
            AppVersion = "t",
            FfmpegBuildId = "t",
            Displays = [],
            CanvasWidth = 1,
            CanvasHeight = 1,
            FrameRate = 15,
            EncoderName = "e",
            Quality = "balanced",
            SpeedPreset = "veryfast",
            EncoderArguments = [],
            WorkingFolder = folder,
        }))
        {
        }

        AuthorizationRuleCollection rules = new DirectoryInfo(folder).GetAccessControl()
            .GetAccessRules(includeExplicit: true, includeInherited: true, typeof(SecurityIdentifier));
        foreach (FileSystemAccessRule rule in rules)
        {
            if (rule.AccessControlType == AccessControlType.Allow)
            {
                Others.ShouldNotContain((SecurityIdentifier)rule.IdentityReference,
                    $"{rule.IdentityReference} may {rule.FileSystemRights} the recording");
            }
        }

        using WindowsIdentity me = WindowsIdentity.GetCurrent();
        rules.Cast<FileSystemAccessRule>().ShouldContain(rule => rule.IdentityReference == me.User,
            "the person recording keeps full access");
    }

    [Fact]
    public void A_folder_that_already_exists_keeps_the_permissions_it_has()
    {
        // The working folder the person chose is theirs to configure.
        string existing = Path.Combine(_parent, "chosen");
        Directory.CreateDirectory(existing);
        int before = new DirectoryInfo(existing).GetAccessControl()
            .GetAccessRules(true, true, typeof(SecurityIdentifier)).Count;

        Captr.Core.Common.PrivateFolder.Create(existing);

        new DirectoryInfo(existing).GetAccessControl().GetAccessRules(true, true, typeof(SecurityIdentifier)).Count
            .ShouldBe(before);
    }
}
