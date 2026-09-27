using System.IO.Pipes;
using System.Security.AccessControl;
using System.Security.Principal;

using Captr.Core.Ipc;

using NSubstitute;

using Serilog.Core;

using Shouldly;

namespace Captr.Core.Tests.Ipc;

/// <summary>
/// The host must own its command pipe outright (SPEC §4: "restricted to the current
/// user's SID; reject any other identity").
/// </summary>
/// <remarks>
/// A pipe's security descriptor is fixed by whoever creates its FIRST instance, and
/// the name is predictable — a hash of the user's SID, which is public. So another
/// user could create the name first with an "Everyone: full control" ACL, and the
/// host used to join it happily: its own "current user only" rule was silently
/// discarded, and the squatter could drive the host (start a recording of someone
/// else's screen, point its destinations at a share it can read) or impersonate it
/// to that user's CLI. These tests stand in for the other user with a squatter
/// created by this one; the ACL is what matters, not whose token made it.
/// </remarks>
public class PipeOwnershipTests
{
    private readonly string _suffix = "test-" + Guid.NewGuid().ToString("N")[..8];

    private static PipeSecurity EveryoneFullControl()
    {
        var security = new PipeSecurity();
        security.AddAccessRule(new PipeAccessRule(
            new SecurityIdentifier(WellKnownSidType.WorldSid, null), PipeAccessRights.FullControl, AccessControlType.Allow));
        return security;
    }

    [Fact]
    public async Task A_host_refuses_to_serve_under_a_pipe_name_someone_else_created_first()
    {
        await using NamedPipeServerStream squatter = NamedPipeServerStreamAcl.Create(
            IpcProtocol.PipeName(_suffix), PipeDirection.InOut, NamedPipeServerStream.MaxAllowedServerInstances,
            PipeTransmissionMode.Byte, PipeOptions.Asynchronous, 0, 0, EveryoneFullControl());

        await using var server = new IpcServer(Substitute.For<IHostOperations>(), "test", Logger.None, _suffix);

        IOException refusal = Should.Throw<IOException>(() => server.Start());
        refusal.Message.ShouldContain("already");
    }

    [Fact]
    public async Task While_a_host_serves_the_name_nobody_else_can_claim_it()
    {
        await using var server = new IpcServer(Substitute.For<IHostOperations>(), "test", Logger.None, _suffix);
        server.Start();

        // A would-be squatter asking to be the FIRST instance must be refused, and
        // one joining as a later instance must be refused by the host's own ACL
        // (it grants nobody but this user the right to create instances).
        Should.Throw<UnauthorizedAccessException>(() => NamedPipeServerStreamAcl.Create(
            IpcProtocol.PipeName(_suffix), PipeDirection.InOut, 1,
            PipeTransmissionMode.Byte, PipeOptions.Asynchronous | PipeOptions.FirstPipeInstance, 0, 0, EveryoneFullControl()));
    }

    [Fact]
    public async Task The_host_pipe_is_owned_by_the_user_and_grants_access_to_nobody_else()
    {
        await using var server = new IpcServer(Substitute.For<IHostOperations>(), "test", Logger.None, _suffix);
        server.Start();

        await using var probe = new NamedPipeClientStream(
            ".", IpcProtocol.PipeName(_suffix), PipeDirection.InOut, PipeOptions.CurrentUserOnly);
        await probe.ConnectAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);

        using var me = WindowsIdentity.GetCurrent();
        PipeSecurity security = probe.GetAccessControl();
        security.GetOwner(typeof(SecurityIdentifier)).ShouldBe(me.User);
        security.GetAccessRules(true, true, typeof(SecurityIdentifier))
            .Cast<PipeAccessRule>()
            .Where(rule => rule.AccessControlType == AccessControlType.Allow)
            .Select(rule => rule.IdentityReference)
            .ShouldAllBe(identity => identity.Equals(me.User));
    }
}
