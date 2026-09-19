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
