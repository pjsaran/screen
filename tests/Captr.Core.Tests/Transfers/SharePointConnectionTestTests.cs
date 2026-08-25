using Captr.Core.Settings;
using Captr.Core.Transfers;

using Shouldly;

namespace Captr.Core.Tests.Transfers;

/// <summary>
/// The connection test's pre-flight guards. They must catch an incomplete form
/// BEFORE any network or sign-in is attempted, and each refusal must name the field
/// to fill in — the button exists so a novice learns what is wrong from the message
/// alone.
/// </summary>
public class SharePointConnectionTestTests
{
    private static DestinationSettings Destination(string? tenant, string? client, string? driveId) => new()
    {
        Name = "sp",
        Kind = DestinationKind.SharePoint,
        SharePointSiteUrl = "https://contoso.sharepoint.com/sites/rec",
        TenantId = tenant,
        ClientId = client,
        SharePointDriveId = driveId,
    };

    [Fact]
    public async Task Missing_sign_in_details_are_refused_before_any_network_is_touched()
    {
        TransferException exception = await Should.ThrowAsync<TransferException>(() =>
            SharePointConnectionTest.RunAsync(
                Destination(tenant: null, client: null, driveId: "b!abc"),
                typedSecret: null, graphHttp: null, TestContext.Current.CancellationToken));

        exception.Message.ShouldContain("Tenant ID");
    }

    [Fact]
    public async Task A_missing_drive_id_is_refused_with_the_field_named()
    {
        TransferException exception = await Should.ThrowAsync<TransferException>(() =>
            SharePointConnectionTest.RunAsync(
                Destination(tenant: "tenant", client: "client", driveId: null),
                typedSecret: null, graphHttp: null, TestContext.Current.CancellationToken));

        exception.Message.ShouldContain("Drive ID");
    }
}
