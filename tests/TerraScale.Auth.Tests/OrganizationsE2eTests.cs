namespace TerraScale.Auth.Tests;

using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using TerraScale.Auth.Crypto;
using TerraScale.Auth.Store;
using TerraScale.Auth.Ui;
using TerraScale.Auth.Users;

/// <summary>Organizations, memberships, and invitations end to end over the real service.</summary>
[NotInParallel]
public sealed class OrganizationsE2eTests
{
    private static async Task<(AuthServiceFixture Service, string InstanceId, string Slug, string ApiKey)> StartAsync()
    {
        var service = await AuthServiceFixture.GetAsync();
        var (instanceId, slug, _) = await service.CreateInstanceAsync("orgs-" + Guid.NewGuid().ToString("N")[..8]);

        // Instance creation stores sha256(secret-part) while InstanceResolver hashes the whole
        // bearer string, so the operator-issued key cannot resolve; arrange one that does.
        var secret = "sk_" + SecureTokens.RandomToken(40);
        await service.Store.CreateApiKeyAsync(new ApiKey
        {
            Id = "key_" + SecureTokens.RandomToken(20),
            InstanceId = instanceId,
            Name = "e2e",
            SecretKey = SecureTokens.Sha256Hex(secret),
            Scopes = ["*"],
            CreatedAt = DateTimeOffset.UtcNow.ToUnixTimeSeconds(),
        });
        return (service, instanceId, slug, secret);
    }

    private static HttpRequestMessage Request(HttpMethod method, string apiKey, string url, object? body = null)
    {
        var request = new HttpRequestMessage(method, url);
        if (apiKey.Length > 0)
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", apiKey);
        if (body is not null)
            request.Content = JsonContent.Create(body);
        return request;
    }

    private static async Task<JsonElement> SendAsync(HttpClient http, HttpMethod method, string apiKey, string url, object? body, HttpStatusCode expected)
    {
        using var response = await http.SendAsync(Request(method, apiKey, url, body));
        await Assert.That(response.StatusCode).IsEqualTo(expected);
        return (await response.Content.ReadFromJsonAsync<JsonElement>())!;
    }

    private static async Task<JsonElement> CreateOrganizationAsync(HttpClient http, string apiKey, string slug, string name, string? orgSlug = null)
    {
        var organization = await SendAsync(http, HttpMethod.Post, apiKey, $"/v1/{slug}/organizations", orgSlug is null ? new { name } : new { name, slug = orgSlug }, HttpStatusCode.OK);
        await Assert.That(organization.GetProperty("id").GetString()!.StartsWith("org_")).IsTrue();
        return organization;
    }

    [Test]
    public async Task OrganizationLifecycleCreatesGetsPatchesListsAndDeletes()
    {
        var (service, _, slug, apiKey) = await StartAsync();
        var http = service.Http;

        var created = await CreateOrganizationAsync(http, apiKey, slug, "Acme Corp & Sons!!");
        var orgId = created.GetProperty("id").GetString()!;
        await Assert.That(created.GetProperty("name").GetString()).IsEqualTo("Acme Corp & Sons!!");
        await Assert.That(created.GetProperty("slug").GetString()).IsEqualTo("acme-corp-sons");
        await Assert.That(created.GetProperty("created_at").GetInt64()).IsGreaterThan(0L);

        var detail = await SendAsync(http, HttpMethod.Get, apiKey, $"/v1/{slug}/organizations/{orgId}", null, HttpStatusCode.OK);
        await Assert.That(detail.GetProperty("name").GetString()).IsEqualTo("Acme Corp & Sons!!");

        var patched = await SendAsync(http, HttpMethod.Patch, apiKey, $"/v1/{slug}/organizations/{orgId}", new { name = "Acme Inc" }, HttpStatusCode.OK);
        await Assert.That(patched.GetProperty("name").GetString()).IsEqualTo("Acme Inc");

        var listed = await SendAsync(http, HttpMethod.Get, apiKey, $"/v1/{slug}/organizations", null, HttpStatusCode.OK);
        var entry = listed.EnumerateArray().Single(item => item.GetProperty("id").GetString() == orgId);
        await Assert.That(entry.GetProperty("slug").GetString()).IsEqualTo("acme-corp-sons");

        using var deleted = await http.SendAsync(Request(HttpMethod.Delete, apiKey, $"/v1/{slug}/organizations/{orgId}"));
        await Assert.That(deleted.StatusCode).IsEqualTo(HttpStatusCode.NoContent);
        await Assert.That(await service.Store.GetOrganizationAsync(orgId)).IsNull();
    }

    [Test]
    public async Task DuplicateOrganizationSlugConflicts()
    {
        var (service, _, slug, apiKey) = await StartAsync();
        var http = service.Http;

        await CreateOrganizationAsync(http, apiKey, slug, "Acme Corp");

        var explicitDuplicate = await SendAsync(http, HttpMethod.Post, apiKey, $"/v1/{slug}/organizations", new { name = "Different Name", slug = "acme-corp" }, HttpStatusCode.Conflict);
        await Assert.That(explicitDuplicate.GetProperty("error").GetProperty("code").GetString()).IsEqualTo("organization_exists");

        var derivedDuplicate = await SendAsync(http, HttpMethod.Post, apiKey, $"/v1/{slug}/organizations", new { name = "ACME CORP!" }, HttpStatusCode.Conflict);
        await Assert.That(derivedDuplicate.GetProperty("error").GetProperty("code").GetString()).IsEqualTo("organization_exists");

        await SendAsync(http, HttpMethod.Post, apiKey, $"/v1/{slug}/organizations", new { name = "Whatever", slug = "ab" }, HttpStatusCode.BadRequest);
    }

    [Test]
    public async Task MembershipCrudJoinsUserSummariesAndListsUserOrganizations()
    {
        var (service, instanceId, slug, apiKey) = await StartAsync();
        var http = service.Http;
        var store = service.Store;
        var sessions = new SessionService(store);

        var orgId = (await CreateOrganizationAsync(http, apiKey, slug, "Acme Corp")).GetProperty("id").GetString()!;
        var email = $"member-{Guid.NewGuid().ToString("N")[..6]}@example.com";
        var user = await sessions.CreateUserAsync(instanceId, email, null, null, "Member One");

        await SendAsync(http, HttpMethod.Post, apiKey, $"/v1/{slug}/organizations/{orgId}/memberships", new { user_id = "usr_missing", role = "member" }, HttpStatusCode.NotFound);
        await SendAsync(http, HttpMethod.Post, apiKey, $"/v1/{slug}/organizations/{orgId}/memberships", new { user_id = user.Id, role = new string('x', 65) }, HttpStatusCode.BadRequest);

        var membership = await SendAsync(http, HttpMethod.Post, apiKey, $"/v1/{slug}/organizations/{orgId}/memberships", new { user_id = user.Id, role = "member" }, HttpStatusCode.OK);
        var membershipId = membership.GetProperty("id").GetString()!;
        await Assert.That(membershipId.StartsWith("mem_")).IsTrue();
        await Assert.That(membership.GetProperty("role").GetString()).IsEqualTo("member");

        var duplicate = await SendAsync(http, HttpMethod.Post, apiKey, $"/v1/{slug}/organizations/{orgId}/memberships", new { user_id = user.Id, role = "admin" }, HttpStatusCode.Conflict);
        await Assert.That(duplicate.GetProperty("error").GetProperty("code").GetString()).IsEqualTo("already_a_member");

        var listed = await SendAsync(http, HttpMethod.Get, apiKey, $"/v1/{slug}/organizations/{orgId}/memberships", null, HttpStatusCode.OK);
        var summary = listed.EnumerateArray().Single(item => item.GetProperty("id").GetString() == membershipId);
        await Assert.That(summary.GetProperty("user_id").GetString()).IsEqualTo(user.Id);
        await Assert.That(summary.GetProperty("user").GetProperty("id").GetString()).IsEqualTo(user.Id);
        await Assert.That(summary.GetProperty("user").GetProperty("email").GetString()).IsEqualTo(email);
        await Assert.That(summary.GetProperty("user").GetProperty("name").GetString()).IsEqualTo("Member One");

        var patched = await SendAsync(http, HttpMethod.Patch, apiKey, $"/v1/{slug}/organizations/{orgId}/memberships/{membershipId}", new { role = "custom-role" }, HttpStatusCode.OK);
        await Assert.That(patched.GetProperty("role").GetString()).IsEqualTo("custom-role");
        await Assert.That((await store.GetMembershipAsync(membershipId))!.Role).IsEqualTo("custom-role");

        var userOrganizations = await SendAsync(http, HttpMethod.Get, apiKey, $"/v1/{slug}/users/{user.Id}/organizations", null, HttpStatusCode.OK);
        var membershipEntry = userOrganizations.EnumerateArray().Single();
        await Assert.That(membershipEntry.GetProperty("role").GetString()).IsEqualTo("custom-role");
        await Assert.That(membershipEntry.GetProperty("organization").GetProperty("id").GetString()).IsEqualTo(orgId);
        await Assert.That(membershipEntry.GetProperty("organization").GetProperty("slug").GetString()).IsEqualTo("acme-corp");

        using var removed = await http.SendAsync(Request(HttpMethod.Delete, apiKey, $"/v1/{slug}/organizations/{orgId}/memberships/{membershipId}"));
        await Assert.That(removed.StatusCode).IsEqualTo(HttpStatusCode.NoContent);
        await Assert.That(await store.GetMembershipAsync(membershipId)).IsNull();
    }

    [Test]
    public async Task InvitationCreateListRevokeWithStoreStateAndAudit()
    {
        var (service, instanceId, slug, apiKey) = await StartAsync();
        var http = service.Http;
        var store = service.Store;

        var orgId = (await CreateOrganizationAsync(http, apiKey, slug, "Acme Corp")).GetProperty("id").GetString()!;
        var email = $"invitee-{Guid.NewGuid().ToString("N")[..6]}@example.com";
        var invitation = await SendAsync(http, HttpMethod.Post, apiKey, $"/v1/{slug}/organizations/{orgId}/invitations", new { email, role = "admin" }, HttpStatusCode.OK);
        var invitationId = invitation.GetProperty("id").GetString()!;
        await Assert.That(invitationId.StartsWith("inv_")).IsTrue();
        await Assert.That(invitation.GetProperty("status").GetString()).IsEqualTo("pending");
        await Assert.That(invitation.GetProperty("expires_at").GetInt64()).IsGreaterThan(DateTimeOffset.UtcNow.AddHours(6 * 24).ToUnixTimeSeconds());

        var stored = await store.GetInvitationAsync(invitationId);
        await Assert.That(stored!.Status).IsEqualTo("pending");
        await Assert.That(stored.InstanceId).IsEqualTo(instanceId);
        await Assert.That(stored.Role).IsEqualTo("admin");

        var tokens = await store.ListVerificationTokensAsync(instanceId, "invitation", 100);
        var token = tokens.Single(t => t.InstanceId == instanceId && t.UserId == invitationId);
        await Assert.That(token.Purpose).IsEqualTo("invitation");
        await Assert.That(token.Consumed).IsFalse();

        var audit = await store.ListAuditEventsAsync(instanceId, 100);
        await Assert.That(audit.Any(e => e.Type == "invitation.created")).IsTrue();

        var listed = await SendAsync(http, HttpMethod.Get, apiKey, $"/v1/{slug}/organizations/{orgId}/invitations", null, HttpStatusCode.OK);
        await Assert.That(listed.EnumerateArray().Any(item => item.GetProperty("id").GetString() == invitationId)).IsTrue();

        var revoked = await SendAsync(http, HttpMethod.Post, apiKey, $"/v1/{slug}/organizations/{orgId}/invitations/{invitationId}/revoke", null, HttpStatusCode.OK);
        await Assert.That(revoked.GetProperty("status").GetString()).IsEqualTo("revoked");
        await SendAsync(http, HttpMethod.Post, apiKey, $"/v1/{slug}/organizations/{orgId}/invitations/{invitationId}/revoke", null, HttpStatusCode.Conflict);
    }

    [Test]
    public async Task InvitationAcceptWithMatchingSessionCreatesMembershipAndConsumesToken()
    {
        var (service, instanceId, slug, apiKey) = await StartAsync();
        var http = service.Http;
        var store = service.Store;
        var sessions = new SessionService(store);

        var orgId = (await CreateOrganizationAsync(http, apiKey, slug, "Acme Corp")).GetProperty("id").GetString()!;
        var email = $"invitee-{Guid.NewGuid().ToString("N")[..6]}@example.com";
        var invitationId = (await SendAsync(http, HttpMethod.Post, apiKey, $"/v1/{slug}/organizations/{orgId}/invitations", new { email, role = "admin" }, HttpStatusCode.OK))
            .GetProperty("id").GetString()!;

        var user = await sessions.CreateUserAsync(instanceId, email, null, null, "Invitee");
        var (_, cookieToken) = await sessions.CreateSessionAsync(instanceId, user.Id, null, null);

        // The endpoint's token is unrecoverable from its hash, so mint an identical one:
        // same purpose, same invitation id in UserId, token key over a known secret.
        var knownToken = SecureTokens.RandomToken(48);
        var tokenId = "vtok_" + SecureTokens.RandomToken(20);
        await store.CreateVerificationTokenAsync(new VerificationToken
        {
            Id = tokenId,
            InstanceId = instanceId,
            UserId = invitationId,
            Purpose = "invitation",
            TokenKey = SecureTokens.Sha256Hex(knownToken),
            ExpiresAt = DateTimeOffset.UtcNow.AddHours(1).ToUnixTimeSeconds(),
            CreatedAt = DateTimeOffset.UtcNow.ToUnixTimeSeconds(),
        });

        using var accept = new HttpRequestMessage(HttpMethod.Get, $"/ui/{slug}/invitations/{invitationId}/accept?token={knownToken}");
        accept.Headers.TryAddWithoutValidation("Cookie", $"{SessionCookies.Name}={cookieToken}");
        using var page = await http.SendAsync(accept);
        await Assert.That(page.StatusCode).IsEqualTo(HttpStatusCode.OK);
        var html = await page.Content.ReadAsStringAsync();
        await Assert.That(html).Contains("joined Acme Corp");

        var membership = (await store.ListMembershipsByOrgAsync(orgId)).Single();
        await Assert.That(membership.UserId).IsEqualTo(user.Id);
        await Assert.That(membership.MemberKey).IsEqualTo($"{orgId}:{user.Id}");
        await Assert.That(membership.Role).IsEqualTo("admin");

        await Assert.That((await store.GetInvitationAsync(invitationId))!.Status).IsEqualTo("accepted");
        await Assert.That((await store.GetVerificationTokenAsync(tokenId))!.Consumed).IsTrue();

        var audit = await store.ListAuditEventsAsync(instanceId, 100);
        var accepted = audit.Single(e => e.Type == "invitation.accepted");
        await Assert.That(accepted.ActorId).IsEqualTo(user.Id);
    }

    [Test]
    public async Task InvitationAcceptPageExplainsEveryRejectedState()
    {
        var (service, instanceId, slug, apiKey) = await StartAsync();
        var http = service.Http;
        var store = service.Store;
        var sessions = new SessionService(store);

        var orgId = (await CreateOrganizationAsync(http, apiKey, slug, "Acme Corp")).GetProperty("id").GetString()!;

        async Task<string> InviteAsync(string email)
        {
            var invitation = await SendAsync(http, HttpMethod.Post, apiKey, $"/v1/{slug}/organizations/{orgId}/invitations", new { email, role = "member" }, HttpStatusCode.OK);
            return invitation.GetProperty("id").GetString()!;
        }

        // Mirrors what the invitation endpoint stores: invitation id in UserId, sha256 token key.
        async Task<string> MintTokenAsync(string invitationId)
        {
            var secret = SecureTokens.RandomToken(48);
            await store.CreateVerificationTokenAsync(new VerificationToken
            {
                Id = "vtok_" + SecureTokens.RandomToken(20),
                InstanceId = instanceId,
                UserId = invitationId,
                Purpose = "invitation",
                TokenKey = SecureTokens.Sha256Hex(secret),
                ExpiresAt = DateTimeOffset.UtcNow.AddHours(1).ToUnixTimeSeconds(),
                CreatedAt = DateTimeOffset.UtcNow.ToUnixTimeSeconds(),
            });
            return secret;
        }

        // Expired invitation: page rejects it and nothing is created.
        var expiredId = await InviteAsync($"invitee-{Guid.NewGuid().ToString("N")[..6]}@example.com");
        await store.UpsertInvitationAsync((await store.GetInvitationAsync(expiredId))! with { ExpiresAt = DateTimeOffset.UtcNow.AddSeconds(-60).ToUnixTimeSeconds() });
        var expiredPage = await http.GetAsync($"/ui/{slug}/invitations/{expiredId}/accept?token={await MintTokenAsync(expiredId)}");
        await Assert.That(expiredPage.StatusCode).IsEqualTo(HttpStatusCode.BadRequest);
        await Assert.That(await expiredPage.Content.ReadAsStringAsync()).Contains("expired or was revoked");
        await Assert.That((await store.ListMembershipsByOrgAsync(orgId)).Count).IsEqualTo(0);

        // No account for the invited email: the page points at sign-up.
        var noUserId = await InviteAsync($"nobody-{Guid.NewGuid().ToString("N")[..6]}@example.com");
        var noUserPage = await http.GetAsync($"/ui/{slug}/invitations/{noUserId}/accept?token={await MintTokenAsync(noUserId)}");
        await Assert.That(noUserPage.StatusCode).IsEqualTo(HttpStatusCode.OK);
        await Assert.That(await noUserPage.Content.ReadAsStringAsync()).Contains("Go to sign-up");

        // Account exists but nobody is signed in: the page points at sign-in.
        var signedOutEmail = $"invitee-{Guid.NewGuid().ToString("N")[..6]}@example.com";
        var signedOutUser = await sessions.CreateUserAsync(instanceId, signedOutEmail, null, null, null);
        var signedOutId = await InviteAsync(signedOutEmail);
        var signedOutPage = await http.GetAsync($"/ui/{slug}/invitations/{signedOutId}/accept?token={await MintTokenAsync(signedOutId)}");
        await Assert.That(await signedOutPage.Content.ReadAsStringAsync()).Contains("Sign in");

        // Signed in as a different user: the page explains and links sign-out; invitation stays pending.
        var otherEmail = $"other-{Guid.NewGuid().ToString("N")[..6]}@example.com";
        var otherUser = await sessions.CreateUserAsync(instanceId, otherEmail, null, null, null);
        var (_, otherCookie) = await sessions.CreateSessionAsync(instanceId, otherUser.Id, null, null);
        var targetEmail = $"invitee-{Guid.NewGuid().ToString("N")[..6]}@example.com";
        await sessions.CreateUserAsync(instanceId, targetEmail, null, null, null);
        var wrongUserId = await InviteAsync(targetEmail);
        using var wrongUserRequest = new HttpRequestMessage(HttpMethod.Get, $"/ui/{slug}/invitations/{wrongUserId}/accept?token={await MintTokenAsync(wrongUserId)}");
        wrongUserRequest.Headers.TryAddWithoutValidation("Cookie", $"{SessionCookies.Name}={otherCookie}");
        var wrongUserPage = await http.SendAsync(wrongUserRequest);
        var wrongUserHtml = await wrongUserPage.Content.ReadAsStringAsync();
        await Assert.That(wrongUserHtml).Contains(otherEmail);
        await Assert.That(wrongUserHtml).Contains("Sign out");
        await Assert.That((await store.GetInvitationAsync(wrongUserId))!.Status).IsEqualTo("pending");

        // Invalid token (matching session, pending invitation): rejected.
        var invalidTokenId = await InviteAsync(signedOutEmail);
        using var invalidTokenRequest = new HttpRequestMessage(HttpMethod.Get, $"/ui/{slug}/invitations/{invalidTokenId}/accept?token=not-the-real-token");
        invalidTokenRequest.Headers.TryAddWithoutValidation("Cookie", $"{SessionCookies.Name}={(await sessions.CreateSessionAsync(instanceId, signedOutUser.Id, null, null)).Token}");
        var invalidTokenPage = await http.SendAsync(invalidTokenRequest);
        await Assert.That(invalidTokenPage.StatusCode).IsEqualTo(HttpStatusCode.BadRequest);
        await Assert.That(await invalidTokenPage.Content.ReadAsStringAsync()).Contains("invalid or has already been used");
    }

    [Test]
    public async Task ConcurrentOrganizationCreatesWithTheSameSlugProduceExactlyOneWinner()
    {
        var (service, instanceId, slug, apiKey) = await StartAsync();
        var http = service.Http;

        // Both derive "acme-corp": the pre-check may miss under concurrency, the claim layer may not.
        var responses = await Task.WhenAll(
            http.SendAsync(Request(HttpMethod.Post, apiKey, $"/v1/{slug}/organizations", new { name = "Acme Corp" })),
            http.SendAsync(Request(HttpMethod.Post, apiKey, $"/v1/{slug}/organizations", new { name = "ACME CORP!" })));
        var statuses = responses.Select(r => r.StatusCode).OrderBy(s => (int)s).ToList();
        await Assert.That(statuses).IsEquivalentTo([HttpStatusCode.OK, HttpStatusCode.Conflict]);

        using var loser = responses.Single(r => r.StatusCode == HttpStatusCode.Conflict);
        var error = (await loser.Content.ReadFromJsonAsync<JsonElement>())!.GetProperty("error");
        await Assert.That(error.GetProperty("code").GetString()).IsEqualTo("organization_exists");

        var withSlug = (await service.Store.ListOrganizationsAsync(instanceId)).Where(o => o.SlugKey == $"{instanceId}:acme-corp").ToList();
        await Assert.That(withSlug.Count).IsEqualTo(1);
    }

    [Test]
    public async Task ConcurrentMemberAddsProduceExactlyOneMembershipAndAConflict()
    {
        var (service, instanceId, slug, apiKey) = await StartAsync();
        var http = service.Http;
        var store = service.Store;
        var sessions = new SessionService(store);

        var orgId = (await CreateOrganizationAsync(http, apiKey, slug, "Acme Corp")).GetProperty("id").GetString()!;
        var user = await sessions.CreateUserAsync(instanceId, $"race-{Guid.NewGuid().ToString("N")[..6]}@example.com", null, null, null);
        var add = new { user_id = user.Id, role = "member" };

        var responses = await Task.WhenAll(
            http.SendAsync(Request(HttpMethod.Post, apiKey, $"/v1/{slug}/organizations/{orgId}/memberships", add)),
            http.SendAsync(Request(HttpMethod.Post, apiKey, $"/v1/{slug}/organizations/{orgId}/memberships", add)));
        var statuses = responses.Select(r => r.StatusCode).OrderBy(s => (int)s).ToList();
        await Assert.That(statuses).IsEquivalentTo([HttpStatusCode.OK, HttpStatusCode.Conflict]);

        using var loser = responses.Single(r => r.StatusCode == HttpStatusCode.Conflict);
        var error = (await loser.Content.ReadFromJsonAsync<JsonElement>())!.GetProperty("error");
        await Assert.That(error.GetProperty("code").GetString()).IsEqualTo("already_a_member");

        await Assert.That((await store.ListMembershipsByOrgAsync(orgId)).Count).IsEqualTo(1);
    }

    [Test]
    public async Task SlugDerivationRejectsUnderivableNamesAndTruncatesLongOnes()
    {
        var (service, _, slug, apiKey) = await StartAsync();
        var http = service.Http;

        // No ASCII alphanumerics to build a slug from.
        var rejected = await SendAsync(http, HttpMethod.Post, apiKey, $"/v1/{slug}/organizations", new { name = "日本語の組織です" }, HttpStatusCode.BadRequest);
        await Assert.That(rejected.GetProperty("error").GetProperty("code").GetString()).IsEqualTo("invalid_slug");

        // Two alphanumerics cannot reach the 3-character minimum.
        await SendAsync(http, HttpMethod.Post, apiKey, $"/v1/{slug}/organizations", new { name = "ab" }, HttpStatusCode.BadRequest);

        // Whitespace-only and empty names never reach derivation.
        await SendAsync(http, HttpMethod.Post, apiKey, $"/v1/{slug}/organizations", new { name = "   " }, HttpStatusCode.BadRequest);
        await SendAsync(http, HttpMethod.Post, apiKey, $"/v1/{slug}/organizations", new { name = (string?)null }, HttpStatusCode.BadRequest);

        // A 60-character name derives a slug truncated to the 48-character maximum.
        var longName = new string('a', 60);
        var created = await SendAsync(http, HttpMethod.Post, apiKey, $"/v1/{slug}/organizations", new { name = longName }, HttpStatusCode.OK);
        await Assert.That(created.GetProperty("slug").GetString()).IsEqualTo(new string('a', 48));

        // Explicit slugs are normalized to lowercase.
        var normalized = await SendAsync(http, HttpMethod.Post, apiKey, $"/v1/{slug}/organizations", new { name = "Other", slug = "MiXeD-Case" }, HttpStatusCode.OK);
        await Assert.That(normalized.GetProperty("slug").GetString()).IsEqualTo("mixed-case");
    }

    [Test]
    public async Task RolePatchKeepsMembershipIdentityAndNeverReleasesTheClaim()
    {
        var (service, instanceId, slug, apiKey) = await StartAsync();
        var http = service.Http;
        var store = service.Store;
        var sessions = new SessionService(store);

        var orgId = (await CreateOrganizationAsync(http, apiKey, slug, "Acme Corp")).GetProperty("id").GetString()!;
        var user = await sessions.CreateUserAsync(instanceId, $"patch-{Guid.NewGuid().ToString("N")[..6]}@example.com", null, null, null);
        var membership = await SendAsync(http, HttpMethod.Post, apiKey, $"/v1/{slug}/organizations/{orgId}/memberships", new { user_id = user.Id, role = "member" }, HttpStatusCode.OK);
        var membershipId = membership.GetProperty("id").GetString()!;
        var createdAt = membership.GetProperty("created_at").GetInt64();

        var patched = await SendAsync(http, HttpMethod.Patch, apiKey, $"/v1/{slug}/organizations/{orgId}/memberships/{membershipId}", new { role = "admin" }, HttpStatusCode.OK);
        await Assert.That(patched.GetProperty("id").GetString()).IsEqualTo(membershipId);
        await Assert.That(patched.GetProperty("created_at").GetInt64()).IsEqualTo(createdAt);
        await Assert.That(patched.GetProperty("role").GetString()).IsEqualTo("admin");

        var stored = await store.GetMembershipAsync(membershipId);
        await Assert.That(stored!.Role).IsEqualTo("admin");
        await Assert.That(stored.CreatedAt).IsEqualTo(createdAt);
        await Assert.That(stored.MemberKey).IsEqualTo($"{orgId}:{user.Id}");

        // The memberKey claim was held throughout: a concurrent-style re-add must conflict,
        // and the org still lists exactly the one original membership.
        var readd = await SendAsync(http, HttpMethod.Post, apiKey, $"/v1/{slug}/organizations/{orgId}/memberships", new { user_id = user.Id, role = "member" }, HttpStatusCode.Conflict);
        await Assert.That(readd.GetProperty("error").GetProperty("code").GetString()).IsEqualTo("already_a_member");
        var rows = await store.ListMembershipsByOrgAsync(orgId);
        await Assert.That(rows.Count).IsEqualTo(1);
        await Assert.That(rows[0].Id).IsEqualTo(membershipId);
    }

    [Test]
    public async Task ConcurrentRolePatchesNeverLoseTheMembership()
    {
        var (service, instanceId, slug, apiKey) = await StartAsync();
        var http = service.Http;
        var store = service.Store;
        var sessions = new SessionService(store);

        var orgId = (await CreateOrganizationAsync(http, apiKey, slug, "Acme Corp")).GetProperty("id").GetString()!;
        var user = await sessions.CreateUserAsync(instanceId, $"cp-{Guid.NewGuid().ToString("N")[..6]}@example.com", null, null, null);
        var membership = await SendAsync(http, HttpMethod.Post, apiKey, $"/v1/{slug}/organizations/{orgId}/memberships", new { user_id = user.Id, role = "member" }, HttpStatusCode.OK);
        var membershipId = membership.GetProperty("id").GetString()!;
        var createdAt = membership.GetProperty("created_at").GetInt64();

        var url = $"/v1/{slug}/organizations/{orgId}/memberships/{membershipId}";
        var responses = await Task.WhenAll(
            http.SendAsync(Request(HttpMethod.Patch, apiKey, url, new { role = "admin" })),
            http.SendAsync(Request(HttpMethod.Patch, apiKey, url, new { role = "custom-role" })));
        await Assert.That(responses.All(r => r.StatusCode == HttpStatusCode.OK)).IsTrue();

        var rows = await store.ListMembershipsByOrgAsync(orgId);
        await Assert.That(rows.Count).IsEqualTo(1);
        await Assert.That(rows[0].Id).IsEqualTo(membershipId);
        await Assert.That(rows[0].CreatedAt).IsEqualTo(createdAt);
        await Assert.That(rows[0].Role is "admin" or "custom-role").IsTrue();
    }

    [Test]
    public async Task ConcurrentMembershipDeletesAreIdempotentAndFreeTheClaim()
    {
        var (service, instanceId, slug, apiKey) = await StartAsync();
        var http = service.Http;
        var store = service.Store;
        var sessions = new SessionService(store);

        var orgId = (await CreateOrganizationAsync(http, apiKey, slug, "Acme Corp")).GetProperty("id").GetString()!;
        var user = await sessions.CreateUserAsync(instanceId, $"del-{Guid.NewGuid().ToString("N")[..6]}@example.com", null, null, null);
        var membershipId = (await SendAsync(http, HttpMethod.Post, apiKey, $"/v1/{slug}/organizations/{orgId}/memberships", new { user_id = user.Id, role = "member" }, HttpStatusCode.OK))
            .GetProperty("id").GetString()!;

        var url = $"/v1/{slug}/organizations/{orgId}/memberships/{membershipId}";
        var responses = await Task.WhenAll(
            http.SendAsync(Request(HttpMethod.Delete, apiKey, url)),
            http.SendAsync(Request(HttpMethod.Delete, apiKey, url)));
        // Both deletes settle: one wins with 204, the loser reports it gone — never a 500.
        await Assert.That(responses.All(r => r.StatusCode is HttpStatusCode.NoContent or HttpStatusCode.NotFound)).IsTrue();
        await Assert.That(await store.GetMembershipAsync(membershipId)).IsNull();

        // The claim was released, so the user can be re-added immediately.
        await SendAsync(http, HttpMethod.Post, apiKey, $"/v1/{slug}/organizations/{orgId}/memberships", new { user_id = user.Id, role = "member" }, HttpStatusCode.OK);
    }

    [Test]
    public async Task ManagementEndpointsRequireApiKeyForTheRouteInstance()
    {
        var (service, _, slug, apiKey) = await StartAsync();
        var http = service.Http;

        using var missingKey = await http.GetAsync($"/v1/{slug}/organizations");
        await Assert.That(missingKey.StatusCode).IsEqualTo(HttpStatusCode.Unauthorized);

        using var unknownSlug = await http.SendAsync(Request(HttpMethod.Get, apiKey, "/v1/does-not-exist-xyz/organizations"));
        await Assert.That(unknownSlug.StatusCode).IsEqualTo(HttpStatusCode.Unauthorized);

        var (_, _, otherSlug, otherApiKey) = await StartAsync();
        using var wrongInstance = await http.SendAsync(Request(HttpMethod.Get, otherApiKey, $"/v1/{slug}/organizations"));
        await Assert.That(wrongInstance.StatusCode).IsEqualTo(HttpStatusCode.Unauthorized);
        using var rightInstance = await http.SendAsync(Request(HttpMethod.Get, otherApiKey, $"/v1/{otherSlug}/organizations"));
        await Assert.That(rightInstance.StatusCode).IsEqualTo(HttpStatusCode.OK);
    }
}
