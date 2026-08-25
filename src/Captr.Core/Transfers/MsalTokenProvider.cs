using Captr.Core.Secrets;
using Captr.Core.Settings;

using Microsoft.Identity.Client;
using Microsoft.Identity.Client.Extensions.Msal;

namespace Captr.Core.Transfers;

/// <summary>
/// Bearer tokens for Graph via MSAL with the application-only (client-credential)
/// flow — the mode suited to unattended machines (SPEC §7). The client secret is
/// borrowed from <see cref="CredentialVault"/> only for the instant MSAL needs it;
/// the token cache is DPAPI-backed on disk so tokens survive a host restart
/// (SPEC §7: "use the DPAPI-backed token cache … rather than its default in-memory
/// cache").
/// </summary>
/// <remarks>
/// The interactive delegated flow (a signed-in user instead of an app identity) is
/// initiated from the UI, which can show the sign-in window; it shares this cache.
/// </remarks>
public sealed class MsalTokenProvider : IAccessTokenProvider
{
    private static readonly string[] GraphScopes = ["https://graph.microsoft.com/.default"];

    private readonly DestinationSettings _destination;
    private readonly byte[]? _secretOverride;
    private IConfidentialClientApplication? _app;

    public MsalTokenProvider(DestinationSettings destination) => _destination = destination;

    /// <summary>
    /// Test seam for the editor's "Test connection" button: uses the secret the user
    /// has just TYPED instead of the vault. Nothing is stored — the point of the
    /// button is to prove the details before the user commits to saving them.
    /// </summary>
    public MsalTokenProvider(DestinationSettings destination, byte[]? typedSecret)
        : this(destination) => _secretOverride = typedSecret;

    public async Task<string> GetAccessTokenAsync(CancellationToken cancellationToken)
    {
        bool hasSecretSource = _secretOverride is not null || _destination.CredentialName is not null;
        if (_destination.TenantId is null || _destination.ClientId is null || !hasSecretSource)
        {
            throw new TransferException(FailureKind.Permanent,
                $"Destination '{_destination.Name}' is missing tenantId, clientId, or a stored credential name.");
        }

        _app ??= await BuildAppAsync().ConfigureAwait(false);

        try
        {
            AuthenticationResult result = await _app.AcquireTokenForClient(GraphScopes)
                .ExecuteAsync(cancellationToken).ConfigureAwait(false);
            return result.AccessToken;
        }
        catch (MsalServiceException exception) when (exception.StatusCode is 400 or 401)
        {
            // Wrong/expired secret: the queue pauses this destination until the
            // credential is re-provisioned (SPEC §7).
            throw new TransferException(FailureKind.AuthExpired, exception.Message);
        }
    }

    private async Task<IConfidentialClientApplication> BuildAppAsync()
    {
        // The secret exists as a string ONLY inside MSAL's builder call, borrowed
        // via the vault's zero-on-return callback. MSAL itself requires a string;
        // this is the single sanctioned crossing point, confined to this module.
        // A typed (not yet stored) secret from "Test connection" takes the same
        // narrow path, just without the vault round-trip.
        IConfidentialClientApplication app = _secretOverride is { } typed
            ? BuildWith(typed)
            : CredentialVault.Use(_destination.CredentialName!, BuildWith);

        IConfidentialClientApplication BuildWith(byte[] secretBytes) =>
            ConfidentialClientApplicationBuilder
                .Create(_destination.ClientId)
                .WithTenantId(_destination.TenantId)
                .WithClientSecret(System.Text.Encoding.UTF8.GetString(secretBytes))
                .Build();

        // DPAPI-protected on-disk token cache (SPEC §7).
        var cacheProperties = new StorageCreationPropertiesBuilder(
                "captr-msal-cache.bin",
                Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Captr"))
            .Build();
        MsalCacheHelper cacheHelper = await MsalCacheHelper.CreateAsync(cacheProperties).ConfigureAwait(false);
        cacheHelper.RegisterCache(app.AppTokenCache);
        return app;
    }
}
