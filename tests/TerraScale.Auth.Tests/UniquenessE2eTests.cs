namespace TerraScale.Auth.Tests;

using TerraScale.Auth.Store;
using TerraScale.DotNet;

/// <summary>
/// The atomic uniqueness layer: claims are CreateOnly documents on the engine,
/// so every test here goes through the raw store and bypasses the endpoints'
/// friendly pre-checks — proving the race-safe floor, not the UI.
/// </summary>
[NotInParallel]
public sealed class UniquenessE2eTests
{
    private static async Task<(AuthStore Store, string InstanceId)> ArrangeAsync()
    {
        var service = await AuthServiceFixture.GetAsync();
        var (instanceId, _, _) = await service.CreateInstanceAsync("uniques-" + Guid.NewGuid().ToString("N")[..8]);
        return (service.Store, instanceId);
    }

    private static User NewUser(string instanceId, string email, string id) => new()
    {
        Id = id,
        InstanceId = instanceId,
        Email = email,
        EmailKey = $"{instanceId}:{email}",
        CreatedAt = DateTimeOffset.UtcNow.ToUnixTimeSeconds(),
        UpdatedAt = DateTimeOffset.UtcNow.ToUnixTimeSeconds(),
    };

    [Test]
    public async Task DuplicateEmailIsRejectedByTheClaimLayer()
    {
        var (store, instanceId) = await ArrangeAsync();
        await store.CreateUserAsync(NewUser(instanceId, "dupe@example.com", "usr_first"));

        var conflict = await Assert.ThrowsAsync<TerraScaleApiException>(() => store.CreateUserAsync(NewUser(instanceId, "dupe@example.com", "usr_second")));
        await Assert.That(conflict!.StatusCode is System.Net.HttpStatusCode.Conflict or System.Net.HttpStatusCode.PreconditionFailed).IsTrue();
    }

    [Test]
    public async Task SameEmailAcrossInstancesIsAllowed()
    {
        var (store, instanceId) = await ArrangeAsync();
        var (_, otherInstance) = await ArrangeAsync();
        await store.CreateUserAsync(NewUser(instanceId, "shared@example.com", "usr_a"));
        await store.CreateUserAsync(NewUser(otherInstance, "shared@example.com", "usr_b"));
    }

    [Test]
    public async Task FailedCreateRollsBackItsClaim()
    {
        var (store, instanceId) = await ArrangeAsync();
        await store.CreateUserAsync(NewUser(instanceId, "rb@example.com", "usr_taken"));
        // Same email AND same user id: the claim conflicts first, so this is a
        // different path; force the rollback path by re-creating an existing id
        // with a fresh email — the document create fails, the claim must unwind.
        var conflict = await Assert.ThrowsAsync<TerraScaleApiException>(() =>
            store.CreateUserAsync(NewUser(instanceId, "fresh@example.com", "usr_taken")));
        await Assert.That(conflict!.StatusCode is System.Net.HttpStatusCode.Conflict or System.Net.HttpStatusCode.PreconditionFailed).IsTrue();

        // The fresh email claim was rolled back, so it is claimable now.
        await store.CreateUserAsync(NewUser(instanceId, "fresh@example.com", "usr_fresh"));
    }

    [Test]
    public async Task UsernameConflictRollsBackTheEmailClaim()
    {
        var (store, instanceId) = await ArrangeAsync();
        var usernameKey = $"{instanceId}:taken-name";
        await store.CreateUserAsync(new User
        {
            Id = "usr_uname_owner", InstanceId = instanceId, Email = "owner@example.com",
            EmailKey = $"{instanceId}:owner@example.com", UsernameKey = usernameKey,
            CreatedAt = DateTimeOffset.UtcNow.ToUnixTimeSeconds(),
            UpdatedAt = DateTimeOffset.UtcNow.ToUnixTimeSeconds(),
        });

        // Fresh email but a taken username: the email claim succeeds, the username
        // claim conflicts. The email claim of this failed create must unwind, or
        // that address reads as "already registered" forever with no user behind it.
        var conflict = await Assert.ThrowsAsync<TerraScaleApiException>(() =>
            store.CreateUserAsync(new User
            {
                Id = "usr_uname_victim", InstanceId = instanceId, Email = "fresh-uname@example.com",
                EmailKey = $"{instanceId}:fresh-uname@example.com", UsernameKey = usernameKey,
                CreatedAt = DateTimeOffset.UtcNow.ToUnixTimeSeconds(),
                UpdatedAt = DateTimeOffset.UtcNow.ToUnixTimeSeconds(),
            }));
        await Assert.That(conflict!.StatusCode is System.Net.HttpStatusCode.Conflict or System.Net.HttpStatusCode.PreconditionFailed).IsTrue();

        await store.CreateUserAsync(NewUser(instanceId, "fresh-uname@example.com", "usr_uname_fresh"));
    }

    [Test]
    public async Task ReleaseAllowsReclaim()
    {
        var (store, instanceId) = await ArrangeAsync();
        var key = $"{instanceId}:recycle@example.com";
        await store.ClaimUniqueAsync("user-email", key, "usr_old");
        await store.ReleaseUniqueAsync("user-email", key);
        await store.ClaimUniqueAsync("user-email", key, "usr_new");
        // Releasing a claim that does not exist is a no-op.
        await store.ReleaseUniqueAsync("user-email", key + "-absent");
    }

    [Test]
    public async Task MembershipDuplicatesAreRejected()
    {
        var (store, instanceId) = await ArrangeAsync();
        var org = await store.CreateOrganizationAsync(new Organization
        {
            Id = "org_" + Guid.NewGuid().ToString("N")[..10],
            InstanceId = instanceId,
            Name = "Org",
            SlugKey = $"{instanceId}:org-{Guid.NewGuid().ToString("N")[..6]}",
            CreatedAt = DateTimeOffset.UtcNow.ToUnixTimeSeconds(),
        });

        var memberKey = $"{org.Id}:usr_member";
        await store.CreateMembershipAsync(new OrgMembership
        {
            Id = "mem_1", InstanceId = instanceId, OrgId = org.Id, UserId = "usr_member",
            MemberKey = memberKey, Role = "member", CreatedAt = DateTimeOffset.UtcNow.ToUnixTimeSeconds(),
        });

        var conflict = await Assert.ThrowsAsync<TerraScaleApiException>(() =>
            store.CreateMembershipAsync(new OrgMembership
            {
                Id = "mem_2", InstanceId = instanceId, OrgId = org.Id, UserId = "usr_member",
                MemberKey = memberKey, Role = "admin", CreatedAt = DateTimeOffset.UtcNow.ToUnixTimeSeconds(),
            }));
        await Assert.That(conflict!.StatusCode is System.Net.HttpStatusCode.Conflict or System.Net.HttpStatusCode.PreconditionFailed).IsTrue();

        // Deleting the membership frees the member key for re-creation.
        await store.DeleteMembershipAsync("mem_1");
        await store.CreateMembershipAsync(new OrgMembership
        {
            Id = "mem_3", InstanceId = instanceId, OrgId = org.Id, UserId = "usr_member",
            MemberKey = memberKey, Role = "admin", CreatedAt = DateTimeOffset.UtcNow.ToUnixTimeSeconds(),
        });
    }

    [Test]
    public async Task OrganizationSlugConflictsAndFreesOnDelete()
    {
        var (store, instanceId) = await ArrangeAsync();
        var slugKey = $"{instanceId}:same-slug";
        var first = new Organization
        {
            Id = "org_first", InstanceId = instanceId, Name = "First", SlugKey = slugKey,
            CreatedAt = DateTimeOffset.UtcNow.ToUnixTimeSeconds(),
        };
        await store.CreateOrganizationAsync(first);

        var conflict = await Assert.ThrowsAsync<TerraScaleApiException>(() =>
            store.CreateOrganizationAsync(new Organization
            {
                Id = "org_second", InstanceId = instanceId, Name = "Second", SlugKey = slugKey,
                CreatedAt = DateTimeOffset.UtcNow.ToUnixTimeSeconds(),
            }));
        await Assert.That(conflict!.StatusCode is System.Net.HttpStatusCode.Conflict or System.Net.HttpStatusCode.PreconditionFailed).IsTrue();

        await store.DeleteOrganizationAsync("org_first");
        await store.CreateOrganizationAsync(new Organization
        {
            Id = "org_third", InstanceId = instanceId, Name = "Third", SlugKey = slugKey,
            CreatedAt = DateTimeOffset.UtcNow.ToUnixTimeSeconds(),
        });
    }

    [Test]
    public async Task OrganizationDeleteCascadesDomainsAndMemberships()
    {
        var (store, instanceId) = await ArrangeAsync();
        var suffix = Guid.NewGuid().ToString("N")[..6];
        var now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        var org = await store.CreateOrganizationAsync(new Organization
        {
            Id = "org_cascade", InstanceId = instanceId, Name = "Cascade",
            SlugKey = $"{instanceId}:cascade-{suffix}", CreatedAt = now,
        });
        var domainKey = $"{instanceId}:recycle-{suffix}.com";
        await store.CreateOrgDomainAsync(new OrgDomain
        {
            Id = "dom_cascade", InstanceId = instanceId, OrgId = org.Id,
            Domain = $"recycle-{suffix}.com", DomainKey = domainKey, CreatedAt = now,
        });
        await store.CreateMembershipAsync(new OrgMembership
        {
            Id = "mem_cascade", InstanceId = instanceId, OrgId = org.Id, UserId = "usr_cascade",
            MemberKey = $"{org.Id}:usr_cascade", Role = "member", CreatedAt = now,
        });

        await store.DeleteOrganizationAsync(org.Id);

        // The membership cascade must not leave orphan documents (or their claims).
        await Assert.That(await store.ListMembershipsByOrgAsync(org.Id, CancellationToken.None)).IsEmpty();
        // The domain claim was released with the org, so another org can enroll it.
        await store.CreateOrgDomainAsync(new OrgDomain
        {
            Id = "dom_reborn", InstanceId = instanceId, OrgId = "org_other",
            Domain = $"recycle-{suffix}.com", DomainKey = domainKey, CreatedAt = now,
        });
    }

    [Test]
    public async Task ClaimReleasingDeletesAreIdempotent()
    {
        var (store, instanceId) = await ArrangeAsync();
        var now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        var credentialKey = TerraScale.Auth.Crypto.SecureTokens.RandomToken(32);
        await store.CreatePasskeyAsync(new Passkey
        {
            Id = "pky_idem", InstanceId = instanceId, UserId = "usr_idem", CredentialKey = credentialKey,
            PublicKeyCborBase64 = "AAECAwQFBgcI", Algorithm = "ES256", SignCount = 1, Nickname = "k",
            CreatedAt = now,
        });
        await store.CreateOAuthClientAsync(new OAuthClient
        {
            Id = "cli_idem", InstanceId = instanceId, ClientId = "cid_idem",
            ClientKey = $"{instanceId}:cid_idem", Name = "idem", RedirectUris = [],
            TokenEndpointAuthMethod = "none", CreatedAt = now,
        });
        await store.CreateOrgDomainAsync(new OrgDomain
        {
            Id = "dom_idem", InstanceId = instanceId, OrgId = "org_idem",
            Domain = "idem.example.com", DomainKey = $"{instanceId}:idem.example.com", CreatedAt = now,
        });

        // A second delete of the same entity (or one that never existed) is a no-op,
        // and the released identifier is reclaimable either way.
        await store.DeletePasskeyAsync("pky_idem");
        await store.DeletePasskeyAsync("pky_idem");
        await store.DeletePasskeyAsync("pky_never_existed");
        await store.CreatePasskeyAsync(new Passkey
        {
            Id = "pky_reborn", InstanceId = instanceId, UserId = "usr_idem", CredentialKey = credentialKey,
            PublicKeyCborBase64 = "AAECAwQFBgcI", Algorithm = "ES256", SignCount = 1, Nickname = "k",
            CreatedAt = now,
        });

        await store.DeleteOAuthClientAsync("cli_idem");
        await store.DeleteOAuthClientAsync("cli_idem");
        await store.DeleteOAuthClientAsync("cli_never_existed");
        await store.CreateOAuthClientAsync(new OAuthClient
        {
            Id = "cli_reborn", InstanceId = instanceId, ClientId = "cid_idem",
            ClientKey = $"{instanceId}:cid_idem", Name = "idem", RedirectUris = [],
            TokenEndpointAuthMethod = "none", CreatedAt = now,
        });

        await store.DeleteOrgDomainAsync("dom_idem");
        await store.DeleteOrgDomainAsync("dom_idem");
        await store.DeleteOrgDomainAsync("dom_never_existed");
        await store.CreateOrgDomainAsync(new OrgDomain
        {
            Id = "dom_reborn2", InstanceId = instanceId, OrgId = "org_idem",
            Domain = "idem.example.com", DomainKey = $"{instanceId}:idem.example.com", CreatedAt = now,
        });
    }

    [Test]
    public async Task PasskeyCredentialKeyIsUniqueGlobally()
    {
        var (store, instanceId) = await ArrangeAsync();
        var (_, otherInstance) = await ArrangeAsync();
        var credentialKey = TerraScale.Auth.Crypto.SecureTokens.RandomToken(32);
        var passkey = new Passkey
        {
            Id = "pky_1", InstanceId = instanceId, UserId = "usr_pk", CredentialKey = credentialKey,
            PublicKeyCborBase64 = "AAECAwQFBgcI", Algorithm = "ES256", SignCount = 1, Nickname = "k",
            CreatedAt = DateTimeOffset.UtcNow.ToUnixTimeSeconds(),
        };
        await store.CreatePasskeyAsync(passkey);

        // Even in another instance, the same credential cannot be registered twice.
        var conflict = await Assert.ThrowsAsync<TerraScaleApiException>(() =>
            store.CreatePasskeyAsync(passkey with { Id = "pky_2", InstanceId = otherInstance }));
        await Assert.That(conflict!.StatusCode is System.Net.HttpStatusCode.Conflict or System.Net.HttpStatusCode.PreconditionFailed).IsTrue();

        await store.DeletePasskeyAsync("pky_1");
        await store.CreatePasskeyAsync(passkey with { Id = "pky_3", InstanceId = otherInstance });
    }
}
