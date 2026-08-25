using System.Runtime.InteropServices;
using System.Security.Cryptography;

using Windows.Win32;
using Windows.Win32.Security.Credentials;

namespace Captr.Core.Secrets;

/// <summary>
/// The single place secrets live (SPEC §7). Stores each secret in Windows
/// Credential Manager, DPAPI-wrapped under the current user with a fixed
/// application entropy — an exfiltrated blob is useless on another machine or
/// account. Owns every byte of secret material in the process: callers hand bytes
/// in (wiped immediately) and borrow them back only inside a callback whose buffer
/// is zeroed in a finally block. Nothing here ever converts a secret to a string.
/// </summary>
public static class CredentialVault
{
    /// <summary>Prefix namespacing Captr's entries in the Credential Manager.</summary>
    private const string TargetPrefix = "Captr/";

    /// <summary>
    /// Fixed application entropy mixed into DPAPI (SPEC §7). Not itself a secret —
    /// its job is binding the blob to THIS application's protect/unprotect pair on
    /// top of DPAPI's user binding, so a blob lifted from the Credential Manager
    /// cannot be unwrapped by generic DPAPI tooling under the same account.
    /// </summary>
    private static readonly byte[] ApplicationEntropy =
        [0x43, 0x61, 0x70, 0x74, 0x72, 0x21, 0x37, 0x0B, 0xB5, 0x21, 0x9E, 0x4C, 0xD1, 0x0A, 0x77, 0xE3];

    /// <summary>
    /// Stores a secret under the given name, overwriting any previous value.
    /// The caller's buffer is ZEROED before this method returns — the vault takes
    /// ownership of the material, the caller keeps nothing.
    /// </summary>
    public static unsafe void Store(string name, byte[] secret)
    {
        try
        {
            byte[] wrapped = ProtectedData.Protect(secret, ApplicationEntropy, DataProtectionScope.CurrentUser);
            try
            {
                fixed (byte* blobPtr = wrapped)
                fixed (char* targetPtr = TargetPrefix + name)
                fixed (char* userPtr = Environment.UserName)
                {
                    var credential = new CREDENTIALW
                    {
                        Type = CRED_TYPE.CRED_TYPE_GENERIC,
                        TargetName = targetPtr,
                        CredentialBlob = blobPtr,
                        CredentialBlobSize = (uint)wrapped.Length,
                        Persist = CRED_PERSIST.CRED_PERSIST_LOCAL_MACHINE,
                        UserName = userPtr,
                    };
                    if (!PInvoke.CredWrite(&credential, 0))
                    {
                        throw new InvalidOperationException(
                            $"Failed to store the credential '{name}' (error {Marshal.GetLastPInvokeError()}).");
                    }
                }
            }
            finally
            {
                CryptographicOperations.ZeroMemory(wrapped);
            }
        }
        finally
        {
            CryptographicOperations.ZeroMemory(secret);
        }
    }

    /// <summary>
    /// Borrows the secret for the duration of <paramref name="use"/>. The buffer is
    /// zeroed in a finally block the moment the callback returns — do not copy it
    /// out, do not convert it to a string.
    /// </summary>
    public static unsafe T Use<T>(string name, Func<byte[], T> use)
    {
        if (!PInvoke.CredRead(TargetPrefix + name, CRED_TYPE.CRED_TYPE_GENERIC, out CREDENTIALW* credential))
        {
            throw new CredentialNotFoundException(
                $"No credential named '{name}' is stored. Provision it with: captr auth set-secret {name}");
        }

        byte[] wrapped;
        try
        {
            wrapped = new Span<byte>(credential->CredentialBlob, (int)credential->CredentialBlobSize).ToArray();
        }
        finally
        {
            PInvoke.CredFree(credential);
        }

        byte[] secret = [];
        try
        {
            secret = ProtectedData.Unprotect(wrapped, ApplicationEntropy, DataProtectionScope.CurrentUser);
            return use(secret);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(secret);
            CryptographicOperations.ZeroMemory(wrapped);
        }
    }

    /// <summary>Whether a credential exists, WITHOUT touching its material — the
    /// write-only UI shows "a secret is stored" and nothing else (SPEC §7).</summary>
    public static unsafe bool Exists(string name)
    {
        if (!PInvoke.CredRead(TargetPrefix + name, CRED_TYPE.CRED_TYPE_GENERIC, out CREDENTIALW* credential))
        {
            return false;
        }

        PInvoke.CredFree(credential);
        return true;
    }

    /// <summary>Deletes a stored credential; true when one existed.</summary>
    public static unsafe bool Delete(string name)
    {
        fixed (char* targetPtr = TargetPrefix + name)
        {
            return PInvoke.CredDelete(targetPtr, CRED_TYPE.CRED_TYPE_GENERIC, 0);
        }
    }

    /// <summary>
    /// The NAMES of every credential Captr has stored, without touching any secret
    /// material. Used by diagnostics to report entries no destination points at any
    /// more — the residue of a Captr that did not clean up after itself.
    /// </summary>
    /// <remarks>
    /// The names returned are Captr's own, with the <see cref="TargetPrefix"/>
    /// stripped, so they can be compared directly with
    /// <c>DestinationSettings.CredentialName</c>.
    /// </remarks>
    public static unsafe IReadOnlyList<string> ListNames()
    {
        var names = new List<string>();

        uint count = 0;
        CREDENTIALW** entries = null;

        // The filter is a wildcard over the target name, so this only ever sees
        // Captr's own entries — never the rest of the user's vault.
        fixed (char* filter = TargetPrefix + "*")
        {
            if (!PInvoke.CredEnumerate(filter, 0, &count, &entries))
            {
                return names;
            }
        }

        try
        {
            for (uint i = 0; i < count; i++)
            {
                string target = new(entries[i]->TargetName);
                if (target.StartsWith(TargetPrefix, StringComparison.Ordinal))
                {
                    names.Add(target[TargetPrefix.Length..]);
                }
            }
        }
        finally
        {
            PInvoke.CredFree(entries);
        }

        return names;
    }

    /// <summary>
    /// Moves a stored secret to a new name, deleting the old entry. Returns false
    /// when nothing was stored under <paramref name="fromName"/>, in which case
    /// neither entry is touched.
    /// </summary>
    /// <remarks>
    /// This lives in the vault rather than in the caller ON PURPOSE. Renaming means
    /// briefly holding a second copy of the secret, and this module is the only place
    /// allowed to do that — the copy is handed straight to <see cref="Store"/>, which
    /// takes ownership and zeroes it. A caller doing the same thing with
    /// <see cref="Use"/> would be copying secret material out of the borrowed buffer,
    /// which the vault's contract forbids.
    /// </remarks>
    public static bool Rename(string fromName, string toName)
    {
        if (string.Equals(fromName, toName, StringComparison.Ordinal))
        {
            return Exists(fromName);
        }

        try
        {
            Use(fromName, secret =>
            {
                Store(toName, [.. secret]);
                return true;
            });
        }
        catch (CredentialNotFoundException)
        {
            return false;
        }

        Delete(fromName);
        return true;
    }
}

/// <summary>The named credential is not provisioned — the message tells the user
/// the exact command to fix it.</summary>
public sealed class CredentialNotFoundException(string message) : Exception(message);
