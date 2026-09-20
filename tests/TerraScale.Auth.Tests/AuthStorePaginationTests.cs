namespace TerraScale.Auth.Tests;

using TerraScale.Auth.Store;

/// <summary>
/// Pagination probes for the store's listing helpers. Engine pages are
/// id-ordered and ids are random tokens, so a bounded page is an arbitrary
/// subset — never "the first N" in any caller-meaningful order. Every listing
/// that a caller filters, orders, or takes "the latest" from must first see
/// the COMPLETE matching set.
/// </summary>
public sealed class AuthStorePaginationTests
{
    /// <summary>One past the store's 100-document page size.</summary>
    private const int BeyondPage = 105;

    private static async Task<(AuthStore Store, string InstanceId)> ArrangeAsync()
    {
        var service = await AuthServiceFixture.GetAsync();
        var (instanceId, _, _) = await service.CreateInstanceAsync("paging-" + Guid.NewGuid().ToString("N")[..8]);
        return (service.Store, instanceId);
    }

    [Test]
    public async Task UserAuthenticationCleanupWalksPagesAndPreservesOtherScopes()
    {
        var (store, instanceId) = await ArrangeAsync();
        var userId = "usr_" + Guid.NewGuid().ToString("N");
        var prefix = Guid.NewGuid().ToString("N");
        var template = new VerificationToken
        {
            Id = "vt_" + prefix,
            InstanceId = instanceId,
            UserId = userId,
            Purpose = "reset_password",
            TokenKey = prefix,
            ExpiresAt = DateTimeOffset.UtcNow.AddHours(1).ToUnixTimeSeconds(),
        };
        for (var i = 0; i < BeyondPage; i++)
            await store.CreateVerificationTokenAsync(template with { Id = $"vt_{prefix}_{i}" });
        var otherInstance = template with { Id = $"vt_{prefix}_other_instance", InstanceId = instanceId + "_other" };
        var otherUser = template with { Id = $"vt_{prefix}_other_user", UserId = userId + "_other" };
        await store.CreateVerificationTokenAsync(otherInstance);
        await store.CreateVerificationTokenAsync(otherUser);

        var result = await store.DeleteUserAuthenticationArtifactsAsync(instanceId, userId);
        await Assert.That(result.VerificationTokens).IsEqualTo(BeyondPage);
        for (var i = 0; i < BeyondPage; i++)
            await Assert.That(await store.GetVerificationTokenAsync($"vt_{prefix}_{i}")).IsNull();
        await Assert.That(await store.GetVerificationTokenAsync(otherInstance.Id)).IsNotNull();
        await Assert.That(await store.GetVerificationTokenAsync(otherUser.Id)).IsNotNull();
        await Assert.That((await store.DeleteUserAuthenticationArtifactsAsync(instanceId, userId)).VerificationTokens).IsEqualTo(0);
    }

    [Test]
    public async Task VerificationFailureCounterIsDurableAndInstanceScoped()
    {
        var (store, instanceId) = await ArrangeAsync();
        var userId = "usr_" + Guid.NewGuid().ToString("N");
        var tokenId = "vt_counter_" + Guid.NewGuid().ToString("N");
        await store.CreateVerificationTokenAsync(new VerificationToken
        {
            Id = tokenId,
            InstanceId = instanceId,
            UserId = userId,
            Purpose = "passcode",
            TokenKey = "counter-" + tokenId,
            ExpiresAt = DateTimeOffset.UtcNow.AddMinutes(10).ToUnixTimeSeconds(),
        });

        await Assert.That(await store.RecordVerificationFailureAsync(instanceId, userId, "passcode")).IsEqualTo(1);
        await Assert.That(await store.RecordVerificationFailureAsync(instanceId, userId, "passcode")).IsEqualTo(2);
        await Assert.That((await store.GetVerificationTokenAsync(tokenId))!.FailedAttempts).IsEqualTo(2);
        await Assert.That(await store.RecordVerificationFailureAsync(instanceId + "-other", userId, "passcode")).IsEqualTo(0);
    }

    [Test]
    public async Task OrgMembershipListingReachesBeyondTheFirstPage()
    {
        var (store, instanceId) = await ArrangeAsync();
        var suffix = Guid.NewGuid().ToString("N")[..6];
        var org = await store.CreateOrganizationAsync(new Organization
        {
            Id = "org_" + suffix,
            InstanceId = instanceId,
            Name = "Paging",
            SlugKey = $"{instanceId}:paging-{suffix}",
            CreatedAt = DateTimeOffset.UtcNow.ToUnixTimeSeconds(),
        });
        for (var i = 0; i < BeyondPage; i++)
            await store.CreateMembershipAsync(new OrgMembership
            {
                Id = $"mem_{suffix}_{i}",
                InstanceId = instanceId,
                OrgId = org.Id,
                UserId = $"usr_{suffix}_{i}",
                MemberKey = $"{org.Id}:usr_{suffix}_{i}",
                Role = "member",
                CreatedAt = DateTimeOffset.UtcNow.ToUnixTimeSeconds(),
            });

        // Every membership must be visible — the admin-portal admin check and
        // the already-member gate ride on this listing.
        var memberships = await store.ListMembershipsByOrgAsync(org.Id, CancellationToken.None);
        await Assert.That(memberships.Count).IsEqualTo(BeyondPage);
        await Assert.That(memberships.Any(m => m.UserId == $"usr_{suffix}_{BeyondPage - 1}")).IsTrue();
    }

    [Test]
    public async Task UserVerificationTokensAreCompleteForThePurpose()
    {
        var (store, instanceId) = await ArrangeAsync();
        var userId = "usr_" + Guid.NewGuid().ToString("N")[..10];
        var prefix = Guid.NewGuid().ToString("N")[..8];
        for (var i = 0; i < BeyondPage; i++)
            await store.CreateVerificationTokenAsync(new VerificationToken
            {
                Id = $"vtok_{prefix}_{i}",
                InstanceId = instanceId,
                UserId = userId,
                Purpose = "passcode",
                TokenKey = $"probe_{prefix}_{i}",
                ExpiresAt = DateTimeOffset.UtcNow.AddHours(1).ToUnixTimeSeconds(),
                CreatedAt = DateTimeOffset.UtcNow.ToUnixTimeSeconds(),
            }, CancellationToken.None);

        // The passcode resend throttle rides on this listing being complete.
        var tokens = await store.ListUserVerificationTokensAsync(userId, "passcode", CancellationToken.None);
        await Assert.That(tokens.Count).IsEqualTo(BeyondPage);
    }

    [Test]
    public async Task InvalidateVerificationTokensConsumesEveryMatch()
    {
        var (store, instanceId) = await ArrangeAsync();
        var userId = "usr_" + Guid.NewGuid().ToString("N")[..10];
        var prefix = Guid.NewGuid().ToString("N")[..8];
        var ids = new List<string>();
        for (var i = 0; i < BeyondPage; i++)
        {
            var id = $"vtok_{prefix}_{i}";
            ids.Add(id);
            await store.CreateVerificationTokenAsync(new VerificationToken
            {
                Id = id,
                InstanceId = instanceId,
                UserId = userId,
                Purpose = "verify_email",
                TokenKey = $"probe_{prefix}_{i}",
                ExpiresAt = DateTimeOffset.UtcNow.AddHours(1).ToUnixTimeSeconds(),
                CreatedAt = DateTimeOffset.UtcNow.ToUnixTimeSeconds(),
            }, CancellationToken.None);
        }

        await store.InvalidateVerificationTokensAsync(userId, "verify_email", CancellationToken.None);

        // Every outstanding token of the purpose must be consumed — the
        // email-change invalidation is a security guarantee, not a best effort.
        foreach (var id in ids)
        {
            var token = await store.GetVerificationTokenAsync(id, CancellationToken.None);
            await Assert.That(token is { Consumed: true }).IsTrue();
        }
    }

    [Test]
    public async Task AuditListingReturnsTheNewestEvents()
    {
        var (store, instanceId) = await ArrangeAsync();
        var suffix = Guid.NewGuid().ToString("N")[..8];
        const long baseSeconds = 1_700_000_000;
        var newest = new List<string>();
        for (var i = 0; i < BeyondPage; i++)
        {
            var id = $"evt_{suffix}_{i}";
            if (i >= BeyondPage - 3)
                newest.Add(id);
            await store.CreateAuditEventAsync(new AuditEvent
            {
                Id = id,
                InstanceId = instanceId,
                Type = "probe.event",
                CreatedAt = baseSeconds + i,
            }, CancellationToken.None);
        }

        // Newest-first: the three highest CreatedAt, descending.
        var events = await store.ListAuditEventsAsync(instanceId, 3, CancellationToken.None);
        await Assert.That(events.Select(e => e.Id).ToArray()).IsEquivalentTo(newest.ToArray());
    }

    [Test]
    public async Task AuditEventsAreSealedWithAnIntegrityDigest()
    {
        var (store, instanceId) = await ArrangeAsync();
        var audit = await store.CreateAuditEventAsync(new AuditEvent
        {
            Id = "evt_" + Guid.NewGuid().ToString("N"),
            InstanceId = instanceId,
            Type = "integrity.probe",
            ActorId = "operator",
            Data = new Dictionary<string, object> { ["z"] = "last", ["a"] = 1 },
            CreatedAt = DateTimeOffset.UtcNow.ToUnixTimeSeconds(),
        });

        await Assert.That(audit.HasValidIntegrity()).IsTrue();
        await Assert.That(audit.IntegrityVersion).IsEqualTo(1);
        await Assert.That(audit.IntegrityHash).IsNotNull();
        await Assert.That(audit.IntegritySignature).IsNotNull();
        await Assert.That(audit.IntegrityKeyFingerprint)
            .IsEqualTo(AuditEvent.ComputeKeyFingerprint(AuthServiceFixture.AuditSigningKey));
        await Assert.That(audit.HasAuthenticIntegrity(AuthServiceFixture.AuditSigningKey)).IsTrue();
        await Assert.That(audit.HasAuthenticIntegrity(new[] { new byte[32], AuthServiceFixture.AuditSigningKey })).IsTrue();
        await Assert.That(audit.HasAuthenticIntegrity(new byte[32])).IsFalse();
        await Assert.That(store.HasAuthenticAuditIntegrity(audit)).IsTrue();
        await Assert.That((await store.ListAuditEventsAsync(instanceId, 10)).Single(e => e.Id == audit.Id).HasValidIntegrity()).IsTrue();
        await Assert.That((audit with { Data = new Dictionary<string, object> { ["a"] = 2, ["z"] = "last" } }).HasValidIntegrity()).IsFalse();
    }

    [Test]
    public async Task AuditSinceReturnsEveryEventPastTheBoundary()
    {
        var (store, instanceId) = await ArrangeAsync();
        var suffix = Guid.NewGuid().ToString("N")[..8];
        const long baseSeconds = 1_700_000_000;
        const int boundary = 50;
        for (var i = 0; i < BeyondPage; i++)
            await store.CreateAuditEventAsync(new AuditEvent
            {
                Id = $"evt_{suffix}_{i}",
                InstanceId = instanceId,
                Type = "probe.event",
                CreatedAt = baseSeconds + i,
            }, CancellationToken.None);

        // Webhook delivery walks this listing; a small page must not cap the
        // set of events past the cursor.
        var events = await store.ListAuditEventsSinceAsync(instanceId, baseSeconds + boundary, 10, CancellationToken.None);
        await Assert.That(events.Count).IsEqualTo(BeyondPage - boundary);
        await Assert.That(events.Select(e => e.CreatedAt).SequenceEqual(events.Select(e => e.CreatedAt).OrderBy(c => c))).IsTrue();
    }

    [Test]
    public async Task OrgDomainListingReachesBeyondTheFirstPage()
    {
        var (store, instanceId) = await ArrangeAsync();
        var suffix = Guid.NewGuid().ToString("N")[..6];
        for (var i = 0; i < BeyondPage; i++)
            await store.CreateOrgDomainAsync(new OrgDomain
            {
                Id = $"dom_{suffix}_{i}",
                InstanceId = instanceId,
                OrgId = "org_" + suffix,
                Domain = $"probe-{suffix}-{i}.example.com",
                DomainKey = $"{instanceId}:probe-{suffix}-{i}.example.com",
                CreatedAt = DateTimeOffset.UtcNow.ToUnixTimeSeconds(),
            }, CancellationToken.None);

        var domains = await store.ListOrgDomainsAsync(instanceId, CancellationToken.None);
        await Assert.That(domains.Count).IsEqualTo(BeyondPage);
        await Assert.That(domains.Any(d => d.Domain == $"probe-{suffix}-{BeyondPage - 1}.example.com")).IsTrue();
    }
}
