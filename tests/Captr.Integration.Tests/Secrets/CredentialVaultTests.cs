using Captr.Core.Secrets;

using Shouldly;

namespace Captr.Integration.Tests.Secrets;

/// <summary>
/// The vault against the real Windows Credential Manager (SPEC §14: "a stored
/// credential survives a host restart and appears in no file"). Test entries use a
/// unique name and are deleted afterwards.
/// </summary>
public class CredentialVaultTests : IDisposable
{
    private readonly string _name = "test-" + Guid.NewGuid().ToString("N")[..12];

    [Fact]
    public void A_secret_round_trips_and_the_callers_buffer_is_wiped()
    {
        byte[] secret = "hunter2-vault-roundtrip"u8.ToArray();
        byte[] original = (byte[])secret.Clone();

        CredentialVault.Store(_name, secret);

        // The vault took ownership: the caller's copy is zeroed (SPEC §7).
        secret.ShouldAllBe(b => b == 0);

        byte[] borrowed = CredentialVault.Use(_name, bytes => (byte[])bytes.Clone());
        borrowed.ShouldBe(original);
    }

    [Fact]
    public void The_secret_survives_across_instances_like_a_host_restart()
    {
        CredentialVault.Store(_name, "persistent-secret"u8.ToArray());

        // A "new host" (same process is fine — the store is the OS, not memory).
        CredentialVault.Exists(_name).ShouldBeTrue();
        CredentialVault.Use(_name, bytes => System.Text.Encoding.UTF8.GetString(bytes))
            .ShouldBe("persistent-secret");
    }

    [Fact]
    public void Existence_can_be_checked_without_touching_the_material()
    {
        CredentialVault.Exists(_name).ShouldBeFalse();
        CredentialVault.Store(_name, "x"u8.ToArray());
        CredentialVault.Exists(_name).ShouldBeTrue();
    }

    [Fact]
    public void A_missing_credential_fails_with_the_provisioning_command()
    {
        CredentialNotFoundException exception = Should.Throw<CredentialNotFoundException>(() =>
            CredentialVault.Use("does-not-exist-" + _name, bytes => 0));

        exception.Message.ShouldContain("captr auth set-secret");
    }

    [Fact]
    public void Deleting_removes_the_entry()
    {
        CredentialVault.Store(_name, "gone"u8.ToArray());
        CredentialVault.Delete(_name).ShouldBeTrue();
        CredentialVault.Exists(_name).ShouldBeFalse();
    }

    /// <summary>
    /// Typing a new secret for an existing destination must actually replace the old
    /// one. Credential Manager shows nothing readable for a DPAPI blob, so a store
    /// that silently failed to overwrite would look exactly like one that worked.
    /// </summary>
    [Fact]
    public void Storing_again_replaces_the_previous_secret()
    {
        CredentialVault.Store(_name, "first-secret"u8.ToArray());
        CredentialVault.Store(_name, "second-secret"u8.ToArray());

        CredentialVault.Use(_name, bytes => System.Text.Encoding.UTF8.GetString(bytes))
            .ShouldBe("second-secret");
    }

    /// <summary>
    /// Renaming a destination has to take its secret with it: the entry name is
    /// derived from the destination's name, so leaving the secret behind orphans it
    /// AND leaves the renamed destination with no credential at all.
    /// </summary>
    [Fact]
    public void Renaming_moves_the_secret_and_removes_the_old_entry()
    {
        string newName = _name + "-renamed";
        try
        {
            CredentialVault.Store(_name, "moves-with-me"u8.ToArray());

            CredentialVault.Rename(_name, newName).ShouldBeTrue();

            CredentialVault.Exists(_name).ShouldBeFalse("the old entry must not be left behind");
            CredentialVault.Use(newName, bytes => System.Text.Encoding.UTF8.GetString(bytes))
                .ShouldBe("moves-with-me");
        }
        finally
        {
            CredentialVault.Delete(newName);
        }
    }

    [Fact]
    public void Renaming_something_that_was_never_stored_changes_nothing()
    {
        CredentialVault.Rename(_name, _name + "-nope").ShouldBeFalse();
        CredentialVault.Exists(_name + "-nope").ShouldBeFalse();
    }

    public void Dispose()
    {
        CredentialVault.Delete(_name);
        GC.SuppressFinalize(this);
    }
}
