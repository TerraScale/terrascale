using System.Net;
using System.Text.Json;
using Bogus;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.IdentityModel.JsonWebTokens;
using TerraScale.Authentication.Contracts;
using TerraScale.Authentication.Features.Identity;
using TerraScale.Authentication.Features.Oidc;
using TerraScale.Authentication.Store;
using TerraScale.Authentication.Tests;
using TerraScale.DotNet.TerraBase;
using static OpenIddict.Abstractions.OpenIddictConstants;

namespace TerraScale.Authentication.Fast.Oidc;

/// <summary>Real TerraBase pagination, conditional claim cleanup and preservation of replay evidence.</summary>
[ClassDataSource<TerraBaseFixture>(Shared = SharedType.PerTestSession)]
public sealed partial class OidcRetentionTests(TerraBaseFixture fixture)
{
    [Test]
    public async Task Concurrent_bounded_pages_clean_expired_records_and_exact_claims_across_tenants()
    {
        var (_, database) = await fixture.CreateDatabaseAsync();
        var clock = new StoreTestClock();
        var store = new AuthStore(database, clock);
        var tokens = new List<OidcToken>();
        for (var index = 0; index <= OidcRetention.PageSize; index++)
        {
            var token = Token(index % 2 == 0 ? "tenant-a" : "tenant-b", clock.UtcNow - OidcRetention.ProofLifetime);
            await SeedAsync(store, token);
            tokens.Add(token);
        }

        var live = Token("tenant-a", clock.UtcNow.AddDays(-1));
        await SeedAsync(store, live);
        var unknownAge = Token("tenant-b", clock.UtcNow.AddDays(-60));
        unknownAge.CreationDate = null;
        await SeedAsync(store, unknownAge);
        var first = new OidcRetention(store, database, clock);
        var second = new OidcRetention(new AuthStore(database, clock), database, clock);
        var passes = await Task.WhenAll(first.PruneTokensAsync(clock.UtcNow, null, CancellationToken.None).AsTask(),
            second.PruneTokensAsync(clock.UtcNow, null, CancellationToken.None).AsTask());
        await Assert.That(passes.All(count => count <= OidcRetention.PageSize)).IsTrue();
        var total = passes.Sum();
        for (var pass = 0; pass < 4; pass++)
        {
            total += await first.PruneTokensAsync(clock.UtcNow, null, CancellationToken.None);
        }

        await Assert.That(total).IsEqualTo(tokens.Count);
        await Assert.That((await database.ListDocumentsAsync(AuthStore.OidcTokenCollection, limit: 1000)).Items.Count).IsEqualTo(2);
        foreach (var token in tokens)
        {
            await Assert.That(await store.FindUniqueAsync(token.InstanceId!, "oidc_token_reference", token.ReferenceId!)).IsNull();
            await Assert.That(await store.FindUniqueAsync(token.InstanceId!, "credential:oidc", token.Id!)).IsNull();
        }

        // A fresh worker starts from the beginning; retained live and undated records still cannot be removed.
        var restarted = new OidcRetention(new AuthStore(database, clock), database, clock);
        await Assert.That(await restarted.PruneTokensAsync(clock.UtcNow, null, CancellationToken.None)).IsEqualTo(0);
        await Assert.That(await store.FindUniqueAsync(live.InstanceId!, "oidc_token_reference", live.ReferenceId!)).IsNotNull();
    }

    [Test]
    public async Task Malformed_records_are_retained_without_blocking_eligible_records_on_the_page()
    {
        var (_, database) = await fixture.CreateDatabaseAsync();
        var clock = new StoreTestClock();
        var store = new AuthStore(database, clock);
        (string Field, string Value)[] malformed = [("id", ""), ("id", " "), ("instanceId", ""), ("instanceId", " "),
            ("referenceId", ""), ("referenceId", " "), ("creationDate", "invalid-date")];
        foreach (var (field, value) in malformed)
        {
            var token = Token("tenant-a", clock.UtcNow - OidcRetention.ProofLifetime);
            var document = JsonSerializer.SerializeToNode(token, OidcJson.Default.OidcToken)!.AsObject();
            document[field] = value;
            using var content = JsonDocument.Parse(document.ToJsonString());
            await database.PutDocumentAsync(AuthStore.OidcTokenCollection,
                AuthStore.NaturalKeyId(token.InstanceId!, AuthStore.OidcTokenCollection, token.Id!), content.RootElement, TerraBasePrecondition.Absent);
            if (field == "referenceId")
            {
                continue;
            }

            var grant = new OidcAuthorization
            {
                Id = AuthStore.NewId(),
                InstanceId = token.InstanceId,
                CreationDate = token.CreationDate,
                Type = AuthorizationTypes.AdHoc,
            };
            var authorization = JsonSerializer.SerializeToNode(grant, OidcJson.Default.OidcAuthorization)!.AsObject();
            authorization[field] = value;
            using var grantContent = JsonDocument.Parse(authorization.ToJsonString());
            await database.PutDocumentAsync(AuthStore.OidcAuthorizationCollection,
                AuthStore.NaturalKeyId(grant.InstanceId!, AuthStore.OidcAuthorizationCollection, grant.Id!), grantContent.RootElement, TerraBasePrecondition.Absent);
        }

        var eligible = Token("tenant-a", clock.UtcNow - OidcRetention.ProofLifetime);
        await SeedAsync(store, eligible);
        var id = AuthStore.NewId();
        await store.CreateAsync(AuthStore.OidcAuthorizationCollection, eligible.InstanceId!, id,
            JsonSerializer.SerializeToElement(new OidcAuthorization
            {
                Id = id,
                InstanceId = eligible.InstanceId,
                CreationDate = eligible.CreationDate,
                Type = AuthorizationTypes.AdHoc,
            }, OidcJson.Default.OidcAuthorization));
        var retention = new OidcRetention(store, database, clock);
        await Assert.That(await retention.PruneTokensAsync(clock.UtcNow, null, CancellationToken.None)).IsEqualTo(1);
        await Assert.That(await retention.PruneAuthorizationsAsync(clock.UtcNow, null, CancellationToken.None)).IsEqualTo(1);
        await Assert.That((await database.ListDocumentsAsync(AuthStore.OidcTokenCollection)).Items.Count).IsEqualTo(malformed.Length);
        await Assert.That((await database.ListDocumentsAsync(AuthStore.OidcAuthorizationCollection)).Items.Count).IsEqualTo(malformed.Length - 2);
    }

    [Test]
    public async Task Final_project_access_token_and_its_proofs_survive_pruning_after_the_thirty_day_family_deadline()
    {
        var clock = new StoreTestClock { UtcNow = DateTimeOffset.FromUnixTimeSeconds(DateTimeOffset.UtcNow.ToUnixTimeSeconds()) };
        await using var oidc = await OidcHarness.StartAsync(fixture, clock: clock);
        var (_, owner) = await oidc.SignInUserAsync(new Faker().Internet.Email());
        var project = await oidc.CreateProjectAsync(owner, await oidc.CreateOrganizationAsync(owner), "Retention project");
        await oidc.OkAsync(HttpMethod.Put, $"/v1/{project}/settings", owner,
            new { configuration = new { redirect_urls = new[] { OidcHarness.RedirectUri }, session_lifetime_seconds = 2592000 } });
        var client = (await oidc.OkAsync(HttpMethod.Post, $"/v1/{project}/clients", owner,
            new { name = "Retention app", type = "public", redirect_uris = new[] { OidcHarness.RedirectUri } }, HttpStatusCode.Created))
            .GetProperty("client_id").GetString()!;
        var user = (await oidc.Server.App.Services.GetRequiredService<Users>().CreateAsync(project, new Faker().Internet.Email()))!;
        var session = await oidc.Server.App.Services.GetRequiredService<Sessions>().CreateAsync(user);
        var (verifier, challenge) = OidcHarness.Pkce();
        using var request = new HttpRequestMessage(HttpMethod.Get, OidcHarness.AuthorizePath(challenge, clientId: client)
            .Replace($"/{OidcIssuer.Slug}/", $"/{project}/", StringComparison.Ordinal));
        request.Headers.Add("Cookie", $"tsauth_session_{project}={session.Token}");
        using var authorized = await oidc.Client.SendAsync(request);
        using var issued = await oidc.PostTokenAsync(new()
        {
            ["grant_type"] = "authorization_code",
            ["code"] = OidcHarness.Query(authorized.Headers.Location!)["code"]!,
            ["client_id"] = client,
            ["redirect_uri"] = OidcHarness.RedirectUri,
            ["code_verifier"] = verifier,
        }, null, null, tenant: project);
        await Assert.That(issued.StatusCode).IsEqualTo(HttpStatusCode.OK);
        var tokens = await OidcHarness.JsonAsync(issued);
        var records = (await oidc.Database.ListDocumentsAsync(AuthStore.OidcTokenCollection, limit: 100)).Items
            .Select(item => item.Document.Deserialize(OidcJson.Default.OidcToken)!).Where(token => token.InstanceId == project).ToArray();
        var code = records.Single(token => token.Type == TokenTypeIdentifiers.Private.AuthorizationCode);
        var deadline = session.Session.CreatedAt.AddDays(30);
        await Assert.That(records.Single(token => token.Type == TokenTypeIdentifiers.RefreshToken).ExpirationDate).IsEqualTo(deadline);
        clock.UtcNow = deadline - TimeSpan.FromMinutes(1);
        using var refreshed = await oidc.PostTokenAsync(new()
        {
            ["grant_type"] = "refresh_token",
            ["refresh_token"] = tokens.GetProperty("refresh_token").GetString()!,
            ["client_id"] = client,
        }, null, null, tenant: project);
        await Assert.That(refreshed.StatusCode).IsEqualTo(HttpStatusCode.OK);
        var access = (await OidcHarness.JsonAsync(refreshed)).GetProperty("access_token").GetString()!;
        clock.UtcNow = deadline.AddSeconds(1);
        await Assert.That(code.CreationDate <= clock.UtcNow - TimeSpan.FromDays(30)).IsTrue();
        await Assert.That(new JsonWebToken(access).GetPayloadValue<long>("exp") > clock.UtcNow.ToUnixTimeSeconds()).IsTrue();
        using var before = await oidc.CallAsync(HttpMethod.Get, $"/{project}/oauth/userinfo", access);
        await Assert.That(before.StatusCode).IsEqualTo(HttpStatusCode.OK);
        var retention = oidc.Server.App.Services.GetRequiredService<OidcRetention>();
        await Assert.That(await retention.PruneTokensAsync(clock.UtcNow, null, CancellationToken.None)).IsEqualTo(0);
        await Assert.That(await retention.PruneAuthorizationsAsync(clock.UtcNow, null, CancellationToken.None)).IsEqualTo(0);
        var store = oidc.Server.App.Services.GetRequiredService<AuthStore>();
        var authorization = await store.GetAsync(AuthStore.OidcAuthorizationCollection, project, code.AuthorizationId!);
        await Assert.That(authorization).IsNotNull();
        await Assert.That(authorization!.Document.GetProperty("creationDate").GetDateTimeOffset().AddDays(30) < clock.UtcNow).IsTrue();
        await Assert.That(await store.GetAsync(AuthStore.OidcTokenCollection, project, code.Id!)).IsNotNull();
        await Assert.That(await store.FindUniqueAsync(project, "credential:oidc", code.Id!)).IsNotNull();
        using var after = await oidc.CallAsync(HttpMethod.Get, $"/{project}/oauth/userinfo", access);
        await Assert.That(after.StatusCode).IsEqualTo(HttpStatusCode.OK);
    }

    [Test]
    public async Task Expired_ancestor_and_revocation_proof_survive_until_the_conservative_horizon()
    {
        var (_, database) = await fixture.CreateDatabaseAsync();
        var clock = new StoreTestClock();
        var store = new AuthStore(database, clock);
        var token = Token("tenant-a", clock.UtcNow - OidcRetention.ProofLifetime + TimeSpan.FromTicks(1));
        var binding = await SeedAsync(store, token);
        var authorization = new OidcAuthorization
        {
            Id = AuthStore.NewId(),
            InstanceId = token.InstanceId,
            CreationDate = token.CreationDate,
            Status = Statuses.Revoked,
            Type = AuthorizationTypes.AdHoc,
        };
        await store.CreateAsync(AuthStore.OidcAuthorizationCollection, authorization.InstanceId!, authorization.Id!,
            JsonSerializer.SerializeToElement(authorization, OidcJson.Default.OidcAuthorization));
        var retention = new OidcRetention(store, database, clock);
        await Assert.That(await retention.PruneTokensAsync(clock.UtcNow, null, CancellationToken.None)).IsEqualTo(0);
        await Assert.That(await retention.PruneAuthorizationsAsync(clock.UtcNow, null, CancellationToken.None)).IsEqualTo(0);
        await Assert.That(await store.AreCredentialsCurrentAsync(token.InstanceId!, [binding])).IsTrue();
        await Assert.That(await store.AreCredentialsCurrentAsync(token.InstanceId!, [AuthStore.AuthorizationBinding(authorization.Id!)])).IsFalse();
        clock.UtcNow += TimeSpan.FromTicks(1);
        await Assert.That(await retention.PruneTokensAsync(clock.UtcNow, "other-tenant", CancellationToken.None)).IsEqualTo(0);
        await Assert.That(await retention.PruneTokensAsync(clock.UtcNow, token.InstanceId, CancellationToken.None)).IsEqualTo(1);
        await Assert.That(await retention.PruneAuthorizationsAsync(clock.UtcNow, token.InstanceId, CancellationToken.None)).IsEqualTo(1);
        await Assert.That(await store.AreCredentialsCurrentAsync(token.InstanceId!, [binding])).IsFalse();
        await Assert.That(await store.AreCredentialsCurrentAsync(token.InstanceId!, [AuthStore.AuthorizationBinding(authorization.Id!)])).IsFalse();
    }

    [Test]
    [Arguments(false)]
    [Arguments(true)]
    public async Task Changed_token_or_claim_version_preserves_the_record_and_all_claims_atomically(bool changeClaim)
    {
        var (tenant, database) = await fixture.CreateDatabaseAsync();
        var clock = new StoreTestClock();
        var store = new AuthStore(database, clock);
        var token = Token("tenant-a", clock.UtcNow.AddDays(-31));
        var binding = await SeedAsync(store, token);
        using var pause = new PausingTransactions { InnerHandler = TerraBaseFixture.NewHandler() };
        using var http = new HttpClient(pause);
        var pausedDatabase = new TerraBaseDocumentsClient(http, new TerraBaseDocumentsClientOptions { BaseAddress = fixture.Server.InternalAddress })
            .Database(tenant.Project.Id, "auth");
        var retention = new OidcRetention(new AuthStore(pausedDatabase, clock), pausedDatabase, clock);
        var pruning = await pause.HoldNextAsync(() => retention.PruneTokensAsync(clock.UtcNow, null, CancellationToken.None).AsTask());
        long removed;
        try
        {
            if (changeClaim)
            {
                var claim = (await store.FindUniqueAsync(token.InstanceId!, binding.Kind, binding.Key))!.Value;
                await Assert.That(await store.TransactAsync([AuthStore.Reclaim(token.InstanceId!, binding.Kind, binding.Key, AuthStore.NewId(), claim.Version)])).IsNull();
            }
            else
            {
                var document = (await store.GetAsync(AuthStore.OidcTokenCollection, token.InstanceId!, token.Id!))!;
                token.ExpirationDate = clock.UtcNow.AddDays(1);
                await Assert.That(await store.CompareAndPutAsync(AuthStore.OidcTokenCollection, token.InstanceId!, token.Id!,
                    JsonSerializer.SerializeToElement(token, OidcJson.Default.OidcToken), document.Version)).IsTrue();
            }
        }
        finally
        {
            removed = await pause.ReleaseAsync(pruning);
        }

        await Assert.That(removed).IsEqualTo(0);
        await Assert.That(await store.GetAsync(AuthStore.OidcTokenCollection, token.InstanceId!, token.Id!)).IsNotNull();
        await Assert.That(await store.FindUniqueAsync(token.InstanceId!, "oidc_token_reference", token.ReferenceId!)).IsNotNull();
        await Assert.That(await store.FindUniqueAsync(token.InstanceId!, binding.Kind, binding.Key)).IsNotNull();
        await Assert.That(await store.AreCredentialsCurrentAsync(token.InstanceId!, [binding])).IsEqualTo(!changeClaim);
        if (!changeClaim)
        {
            await Assert.That(await retention.PruneTokensAsync(clock.UtcNow, null, CancellationToken.None)).IsEqualTo(0);
        }
    }

    [Test]
    public async Task Expired_redeemed_code_proofs_are_kept_while_a_real_refresh_descendant_is_live()
    {
        var clock = new StoreTestClock { UtcNow = DateTimeOffset.UtcNow };
        await using var oidc = await OidcHarness.StartAsync(fixture, clock: clock);
        var (_, _, issued) = await oidc.SignInAndRedeemAsync();
        var store = oidc.Server.App.Services.GetRequiredService<AuthStore>();
        clock.UtcNow += TimeSpan.FromMinutes(2);
        var record = (await oidc.Database.ListDocumentsAsync(AuthStore.OidcTokenCollection, limit: 100)).Items
            .Single(item => item.Document.GetProperty("type").GetString() == TokenTypeIdentifiers.Private.AuthorizationCode);
        var code = record.Document.Deserialize(OidcJson.Default.OidcToken)!;
        await Assert.That(code.ExpirationDate <= clock.UtcNow).IsTrue();
        var retention = oidc.Server.App.Services.GetRequiredService<OidcRetention>();
        await Assert.That(await retention.PruneTokensAsync(clock.UtcNow, null, CancellationToken.None)).IsEqualTo(0);
        await Assert.That(await retention.PruneAuthorizationsAsync(clock.UtcNow, null, CancellationToken.None)).IsEqualTo(0);
        await Assert.That(await store.FindUniqueAsync(code.InstanceId!, "credential:oidc", code.Id!)).IsNotNull();
        using var refreshed = await oidc.RefreshAsync(issued.GetProperty("refresh_token").GetString()!);
        await Assert.That(refreshed.StatusCode).IsEqualTo(HttpStatusCode.OK);
    }

    [Test]
    public async Task Pruned_revoked_credentials_stay_rejected_after_real_store_and_auth_restart()
    {
        await using var isolated = new TerraBaseFixture();
        await isolated.InitializeAsync();
        await using var oidc = await OidcHarness.StartAsync(isolated);
        var (_, _, issued) = await oidc.SignInAndRedeemAsync();
        using var revoked = await oidc.RevokeAsync(issued.GetProperty("refresh_token").GetString()!);
        await Assert.That(revoked.StatusCode).IsEqualTo(HttpStatusCode.OK);
        var store = oidc.Server.App.Services.GetRequiredService<AuthStore>();
        var now = DateTimeOffset.UtcNow;
        var issuedRecords = (await oidc.Database.ListDocumentsAsync(AuthStore.OidcTokenCollection, limit: 100)).Items;
        await Assert.That(issuedRecords.Select(record => record.Document.GetProperty("type").GetString() ?? string.Empty)).IsEquivalentTo(
            [TokenTypeIdentifiers.Private.AuthorizationCode, TokenTypeIdentifiers.RefreshToken, TokenTypeIdentifiers.AccessToken, TokenTypeIdentifiers.IdentityToken]);
        var eligibleIds = issuedRecords.Select(record => record.Id).ToArray();
        foreach (var record in issuedRecords)
        {
            var token = record.Document.Deserialize(OidcJson.Default.OidcToken)!;
            token.CreationDate = now - OidcRetention.ProofLifetime;
            token.ExpirationDate = now.AddDays(-1);
            await Assert.That(await store.CompareAndPutAsync(AuthStore.OidcTokenCollection, token.InstanceId!, token.Id!,
                JsonSerializer.SerializeToElement(token, OidcJson.Default.OidcToken), record.Version)).IsTrue();
        }

        foreach (var record in (await oidc.Database.ListDocumentsAsync(AuthStore.OidcAuthorizationCollection, limit: 100)).Items)
        {
            var grant = record.Document.Deserialize(OidcJson.Default.OidcAuthorization)!;
            grant.CreationDate = now - OidcRetention.ProofLifetime;
            await Assert.That(await store.CompareAndPutAsync(AuthStore.OidcAuthorizationCollection, grant.InstanceId!, grant.Id!,
                JsonSerializer.SerializeToElement(grant, OidcJson.Default.OidcAuthorization), record.Version)).IsTrue();
        }

        // Even old, expired unknown types are retained; a recognized token without finite expiry is retained too.
        var unknown = Token(OidcIssuer.InstanceId, now - OidcRetention.ProofLifetime);
        unknown.Type = "unknown-retention-test";
        await SeedAsync(store, unknown);
        var nonExpiring = Token(OidcIssuer.InstanceId, now - OidcRetention.ProofLifetime);
        nonExpiring.ExpirationDate = null;
        await SeedAsync(store, nonExpiring);
        var retained = (await oidc.Database.ListDocumentsAsync(AuthStore.OidcTokenCollection, limit: 100)).Items
            .Where(record => !eligibleIds.Contains(record.Id, StringComparer.Ordinal)).ToArray();
        using var before = await oidc.RefreshAsync(issued.GetProperty("refresh_token").GetString()!);
        await Assert.That(before.StatusCode).IsEqualTo(HttpStatusCode.BadRequest);
        var retention = oidc.Server.App.Services.GetRequiredService<OidcRetention>();
        await Assert.That(await retention.PruneTokensAsync(now, null, CancellationToken.None)).IsEqualTo((long)eligibleIds.Length);
        await Assert.That(await retention.PruneAuthorizationsAsync(now, null, CancellationToken.None)).IsGreaterThan(0);
        await isolated.Server.StopCleanlyAsync();
        await isolated.Server.RestartAsync();
        await using var replica = await oidc.StartReplicaAsync();
        using var after = await oidc.RefreshAsync(issued.GetProperty("refresh_token").GetString()!, replica.Client);
        await Assert.That(after.StatusCode).IsEqualTo(HttpStatusCode.BadRequest);
        using var access = await oidc.UserInfoAsync(issued.GetProperty("access_token").GetString()!, replica.Client);
        await Assert.That(access.StatusCode).IsEqualTo(HttpStatusCode.Unauthorized);
        foreach (var id in eligibleIds)
        {
            await Assert.That(await oidc.Database.GetDocumentAsync(AuthStore.OidcTokenCollection, id)).IsNull();
        }

        var remaining = (await oidc.Database.ListDocumentsAsync(AuthStore.OidcTokenCollection, limit: 100)).Items;
        await Assert.That(remaining.Select(record => (record.Id, record.Version))).IsEquivalentTo(retained.Select(record => (record.Id, record.Version)));
    }

    private static OidcToken Token(string tenant, DateTimeOffset created) => new()
    {
        Id = AuthStore.NewId(),
        InstanceId = tenant,
        CreationDate = created,
        ExpirationDate = created.AddDays(14),
        ReferenceId = AuthStore.NewId(),
        Status = Statuses.Redeemed,
        Type = TokenTypeIdentifiers.RefreshToken,
    };

    private static async Task<CredentialBinding> SeedAsync(AuthStore store, OidcToken token)
    {
        var binding = AuthStore.NewCredentialBinding("oidc", token.Id!);
        var failed = await store.TransactAsync([
            AuthStore.Create(AuthStore.OidcTokenCollection, token.InstanceId!, token.Id!, JsonSerializer.SerializeToElement(token, OidcJson.Default.OidcToken)),
            AuthStore.Claim(token.InstanceId!, "oidc_token_reference", token.ReferenceId!, token.Id!),
            AuthStore.CredentialClaim(token.InstanceId!, binding),
        ]);
        await Assert.That(failed).IsNull();
        return binding;
    }
}
