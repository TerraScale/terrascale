namespace TerraScale.Auth.Tests;

using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using TerraScale.Auth.Crypto;
using TerraScale.Auth.Store;

/// <summary>SCIM directory sync end to end: management directory API, bearer-authenticated
/// provisioning of users/groups, and the SCIM discovery documents.</summary>
[NotInParallel]
public sealed class ScimE2eTests
{
    private static async Task<(AuthServiceFixture Service, string InstanceId, string Slug, string ApiKey)> StartAsync()
    {
        var service = await AuthServiceFixture.GetAsync();
        var (instanceId, slug, apiKey) = await service.CreateInstanceAsync("scim-" + Guid.NewGuid().ToString("N")[..8]);
        return (service, instanceId, slug, apiKey);
    }

    private static HttpRequestMessage Request(HttpMethod method, string bearer, string url) =>
        Request<object>(method, bearer, url, null);

    private static HttpRequestMessage Request<TBody>(HttpMethod method, string bearer, string url, TBody? body, string mediaType = "application/json") where TBody : class
    {
        var request = new HttpRequestMessage(method, url);
        if (bearer.Length > 0)
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", bearer);
        if (body is not null)
            request.Content = JsonContent.Create(body, MediaTypeHeaderValue.Parse(mediaType));
        return request;
    }

    private static async Task<JsonElement> SendAsync(HttpClient http, HttpMethod method, string bearer, string url, object? body, HttpStatusCode expected, string mediaType = "application/json")
    {
        using var response = await http.SendAsync(Request(method, bearer, url, body, mediaType));
        await Assert.That(response.StatusCode).IsEqualTo(expected);
        return (await response.Content.ReadFromJsonAsync<JsonElement>())!;
    }

    private static async Task<(string DirectoryId, string Token)> CreateDirectoryAsync(HttpClient http, string apiKey, string slug, string? orgId = null)
    {
        var directory = await SendAsync(http, HttpMethod.Post, apiKey, $"/v1/{slug}/directories", orgId is null ? new { } : new { org_id = orgId }, HttpStatusCode.Created);
        return (directory.GetProperty("id").GetString()!, directory.GetProperty("token").GetString()!);
    }

    private static async Task<string> CreateOrganizationAsync(HttpClient http, string apiKey, string slug, string name)
    {
        var organization = await SendAsync(http, HttpMethod.Post, apiKey, $"/v1/{slug}/organizations", new { name }, HttpStatusCode.OK);
        return organization.GetProperty("id").GetString()!;
    }

    /// <summary>Provisions a user through the directory and returns (userId, email).</summary>
    private static async Task<(string UserId, string Email)> ProvisionAsync(HttpClient http, string token, string directoryId, string? email = null, string? externalId = null)
    {
        var local = email ?? $"scim-{Guid.NewGuid().ToString("N")[..8]}@example.com";
        var created = await SendAsync(http, HttpMethod.Post, token, $"/scim/{directoryId}/Users", new
        {
            userName = local,
            name = new { givenName = "Ada", familyName = "Lovelace" },
            externalId = externalId ?? "ext-" + Guid.NewGuid().ToString("N")[..8],
            active = true,
        }, HttpStatusCode.Created, "application/scim+json");
        return (created.GetProperty("id").GetString()!, local);
    }

    [Test]
    public async Task DirectoryManagementCreatesListsRotatesAndDeletes()
    {
        var (service, instanceId, slug, apiKey) = await StartAsync();
        var http = service.Http;
        var store = service.Store;

        var orgId = await CreateOrganizationAsync(http, apiKey, slug, "Acme Corp");
        var (directoryId, token) = await CreateDirectoryAsync(http, apiKey, slug, orgId);
        await Assert.That(directoryId.StartsWith("dir_")).IsTrue();
        await Assert.That(token.StartsWith("scim_")).IsTrue();
        await Assert.That(token.Length).IsEqualTo("scim_".Length + 40);
        await Assert.That((await store.GetScimDirectoryAsync(directoryId))!.OrgId).IsEqualTo(orgId);
        await Assert.That((await store.GetScimDirectoryAsync(directoryId))!.TokenKey).IsEqualTo(SecureTokens.Sha256Hex(token));

        // Listed directories never expose tokens.
        var listed = await SendAsync(http, HttpMethod.Get, apiKey, $"/v1/{slug}/directories", null, HttpStatusCode.OK);
        var entry = listed.GetProperty("data").EnumerateArray().Single(item => item.GetProperty("id").GetString() == directoryId);
        await Assert.That(entry.TryGetProperty("token", out _)).IsFalse();
        await Assert.That(entry.GetProperty("org_id").GetString()).IsEqualTo(orgId);

        // Rotation returns a fresh token once; the old one stops authenticating.
        var rotated = await SendAsync(http, HttpMethod.Post, apiKey, $"/v1/{slug}/directories/{directoryId}/rotate", null, HttpStatusCode.OK);
        var newToken = rotated.GetProperty("token").GetString()!;
        await Assert.That(newToken.StartsWith("scim_")).IsTrue();
        await Assert.That(newToken).IsNotEqualTo(token);
        await SendAsync(http, HttpMethod.Get, newToken, $"/scim/{directoryId}/Users", null, HttpStatusCode.OK, "application/scim+json");
        await SendAsync(http, HttpMethod.Get, token, $"/scim/{directoryId}/Users", null, HttpStatusCode.Unauthorized, "application/scim+json");

        // Unknown org → 404; deleting revokes the token entirely.
        await SendAsync(http, HttpMethod.Post, apiKey, $"/v1/{slug}/directories", new { org_id = "org_missing" }, HttpStatusCode.NotFound);
        using var deleted = await http.SendAsync(Request(HttpMethod.Delete, apiKey, $"/v1/{slug}/directories/{directoryId}"));
        await Assert.That(deleted.StatusCode).IsEqualTo(HttpStatusCode.NoContent);
        await SendAsync(http, HttpMethod.Get, newToken, $"/scim/{directoryId}/Users", null, HttpStatusCode.Unauthorized, "application/scim+json");
        var afterDelete = await SendAsync(http, HttpMethod.Get, apiKey, $"/v1/{slug}/directories", null, HttpStatusCode.OK);
        await Assert.That(afterDelete.GetProperty("data").EnumerateArray().Any(item => item.GetProperty("id").GetString() == directoryId)).IsFalse();

        var audit = await store.ListAuditEventsAsync(instanceId, 100);
        await Assert.That(audit.Any(e => e.Type == "directory.created")).IsTrue();
        await Assert.That(audit.Any(e => e.Type == "directory.token_rotated")).IsTrue();
        await Assert.That(audit.Any(e => e.Type == "directory.deleted")).IsTrue();
    }

    [Test]
    public async Task ScimEndpointsRequireTheDirectoryBearerToken()
    {
        var (service, _, slug, apiKey) = await StartAsync();
        var http = service.Http;
        var (directoryId, token) = await CreateDirectoryAsync(http, apiKey, slug);

        // No token: SCIM error envelope, application/scim+json.
        using var missing = await http.GetAsync($"/scim/{directoryId}/Users");
        await Assert.That(missing.StatusCode).IsEqualTo(HttpStatusCode.Unauthorized);
        await Assert.That(missing.Content.Headers.ContentType!.MediaType).IsEqualTo("application/scim+json");
        var missingBody = await missing.Content.ReadFromJsonAsync<JsonElement>();
        await Assert.That(missingBody.GetProperty("schemas").EnumerateArray().Single().GetString())
            .IsEqualTo("urn:ietf:params:scim:api:messages:2.0:Error");
        await Assert.That(missingBody.GetProperty("status").GetString()).IsEqualTo("401");

        await SendAsync(http, HttpMethod.Get, "scim_not-a-real-token", $"/scim/{directoryId}/Users", null, HttpStatusCode.Unauthorized, "application/scim+json");
        await SendAsync(http, HttpMethod.Get, apiKey, $"/scim/{directoryId}/Users", null, HttpStatusCode.Unauthorized, "application/scim+json");

        // Valid token, wrong directory id.
        await SendAsync(http, HttpMethod.Get, token, "/scim/dir_notthisone/Users", null, HttpStatusCode.Unauthorized, "application/scim+json");
    }

    [Test]
    public async Task ProvisionUserCreatesUserMembershipAuditAndReadsBack()
    {
        var (service, instanceId, slug, apiKey) = await StartAsync();
        var http = service.Http;
        var store = service.Store;

        var orgId = await CreateOrganizationAsync(http, apiKey, slug, "Acme Corp");
        var (directoryId, token) = await CreateDirectoryAsync(http, apiKey, slug, orgId);
        var email = $"ada-{Guid.NewGuid().ToString("N")[..8]}@example.com";
        var externalId = "ext-ada-1";

        var created = await SendAsync(http, HttpMethod.Post, token, $"/scim/{directoryId}/Users", new
        {
            userName = email,
            name = new { givenName = "Ada", familyName = "Lovelace" },
            externalId,
        }, HttpStatusCode.Created, "application/scim+json");
        var userId = created.GetProperty("id").GetString()!;
        await Assert.That(userId.StartsWith("usr_")).IsTrue();
        await Assert.That(created.GetProperty("schemas").EnumerateArray().Single().GetString())
            .IsEqualTo("urn:ietf:params:scim:schemas:core:2.0:User");
        await Assert.That(created.GetProperty("userName").GetString()).IsEqualTo(email);
        await Assert.That(created.GetProperty("active").GetBoolean()).IsTrue();
        await Assert.That(created.GetProperty("externalId").GetString()).IsEqualTo(externalId);
        await Assert.That(created.GetProperty("name").GetProperty("givenName").GetString()).IsEqualTo("Ada");
        await Assert.That(created.GetProperty("name").GetProperty("familyName").GetString()).IsEqualTo("Lovelace");
        await Assert.That(created.GetProperty("meta").GetProperty("location").GetString()).IsEqualTo($"/scim/{directoryId}/Users/{userId}");

        // Mapped onto a ts-auth user with the org membership and audit trail attached.
        var user = await store.GetUserAsync(userId);
        await Assert.That(user!.InstanceId).IsEqualTo(instanceId);
        await Assert.That(user.Email).IsEqualTo(email);
        await Assert.That(user.Name).IsEqualTo("Ada Lovelace");
        await Assert.That(user.PrivateMetadata["scimExternalId"].ToString()).IsEqualTo(externalId);
        await Assert.That(user.Deleted).IsFalse();

        var membership = (await store.ListMembershipsByOrgAsync(orgId)).Single(m => m.UserId == userId);
        await Assert.That(membership.Role).IsEqualTo("member");

        var audit = await store.ListAuditEventsAsync(instanceId, 100);
        var provisioned = audit.Single(e => e.Type == "directory.user_provisioned" && e.Data.TryGetValue("user_id", out var id) && id.ToString() == userId);
        await Assert.That(provisioned.Data["directory_id"].ToString()).IsEqualTo(directoryId);
        await Assert.That(provisioned.Data["external_id"].ToString()).IsEqualTo(externalId);

        // Single fetch + list + both supported filters.
        var single = await SendAsync(http, HttpMethod.Get, token, $"/scim/{directoryId}/Users/{userId}", null, HttpStatusCode.OK, "application/scim+json");
        await Assert.That(single.GetProperty("id").GetString()).IsEqualTo(userId);

        var list = await SendAsync(http, HttpMethod.Get, token, $"/scim/{directoryId}/Users", null, HttpStatusCode.OK, "application/scim+json");
        await Assert.That(list.GetProperty("Resources").EnumerateArray().Any(u => u.GetProperty("id").GetString() == userId)).IsTrue();
        await Assert.That(list.GetProperty("totalResults").GetInt32()).IsGreaterThanOrEqualTo(1);
        await Assert.That(list.GetProperty("startIndex").GetInt32()).IsEqualTo(1);

        var byUserName = await SendAsync(http, HttpMethod.Get, token,
            $"/scim/{directoryId}/Users?filter={Uri.EscapeDataString($"userName eq \"{email}\"")}", null, HttpStatusCode.OK, "application/scim+json");
        await Assert.That(byUserName.GetProperty("totalResults").GetInt32()).IsEqualTo(1);
        await Assert.That(byUserName.GetProperty("Resources").EnumerateArray().Single().GetProperty("id").GetString()).IsEqualTo(userId);

        var byExternalId = await SendAsync(http, HttpMethod.Get, token,
            $"/scim/{directoryId}/Users?filter={Uri.EscapeDataString($"externalId eq \"{externalId}\"")}", null, HttpStatusCode.OK, "application/scim+json");
        await Assert.That(byExternalId.GetProperty("Resources").EnumerateArray().Single().GetProperty("id").GetString()).IsEqualTo(userId);

        var unsupported = await SendAsync(http, HttpMethod.Get, token,
            $"/scim/{directoryId}/Users?filter={Uri.EscapeDataString("userName co \"ada\"")}", null, HttpStatusCode.BadRequest, "application/scim+json");
        await Assert.That(unsupported.GetProperty("schemas").EnumerateArray().Single().GetString())
            .IsEqualTo("urn:ietf:params:scim:api:messages:2.0:Error");

        await SendAsync(http, HttpMethod.Get, token, $"/scim/{directoryId}/Users/usr_missing", null, HttpStatusCode.NotFound, "application/scim+json");
    }

    [Test]
    public async Task DuplicateUserNameReturnsScimConflictOnlyForAnotherUser()
    {
        var (service, _, slug, apiKey) = await StartAsync();
        var http = service.Http;
        var store = service.Store;

        var (directoryId, token) = await CreateDirectoryAsync(http, apiKey, slug);
        var email = $"dupe-{Guid.NewGuid().ToString("N")[..8]}@example.com";
        await ProvisionAsync(http, token, directoryId, email);

        var duplicate = await SendAsync(http, HttpMethod.Post, token, $"/scim/{directoryId}/Users", new { userName = email }, HttpStatusCode.Conflict, "application/scim+json");
        await Assert.That(duplicate.GetProperty("schemas").EnumerateArray().Single().GetString())
            .IsEqualTo("urn:ietf:params:scim:api:messages:2.0:Error");
        await Assert.That(duplicate.GetProperty("status").GetString()).IsEqualTo("409");
        await Assert.That(duplicate.GetProperty("scimType").GetString()).IsEqualTo("uniqueness");

        await SendAsync(http, HttpMethod.Post, token, $"/scim/{directoryId}/Users", new { userName = "not-an-email" }, HttpStatusCode.BadRequest, "application/scim+json");
        await Assert.That((await store.FindUserByEmailAsync((await store.GetScimDirectoryAsync(directoryId))!.InstanceId, email.ToLowerInvariant()))).IsNotNull();
    }

    [Test]
    public async Task PutReplacesAttributesAndMovesTheEmailClaim()
    {
        var (service, _, slug, apiKey) = await StartAsync();
        var http = service.Http;
        var store = service.Store;

        var (directoryId, token) = await CreateDirectoryAsync(http, apiKey, slug);
        var (userId, email) = await ProvisionAsync(http, token, directoryId);
        var newEmail = $"renamed-{Guid.NewGuid().ToString("N")[..8]}@example.com";

        // A verification link pending for the old address must die with the rename.
        var verifyToken = "e2e" + SecureTokens.RandomToken(32);
        await store.CreateVerificationTokenAsync(new VerificationToken
        {
            Id = "vrf_" + SecureTokens.RandomToken(20),
            InstanceId = (await store.GetScimDirectoryAsync(directoryId))!.InstanceId,
            UserId = userId,
            Purpose = "verify_email",
            TokenKey = SecureTokens.Sha256Hex(verifyToken),
            ExpiresAt = DateTimeOffset.UtcNow.AddHours(24).ToUnixTimeSeconds(),
            CreatedAt = DateTimeOffset.UtcNow.ToUnixTimeSeconds(),
        });

        var updated = await SendAsync(http, HttpMethod.Put, token, $"/scim/{directoryId}/Users/{userId}", new
        {
            userName = newEmail,
            name = new { givenName = "Grace", familyName = "Hopper" },
            externalId = "ext-grace-1",
            active = true,
        }, HttpStatusCode.OK, "application/scim+json");
        await Assert.That(updated.GetProperty("userName").GetString()).IsEqualTo(newEmail);
        await Assert.That(updated.GetProperty("name").GetProperty("givenName").GetString()).IsEqualTo("Grace");
        await Assert.That(updated.GetProperty("externalId").GetString()).IsEqualTo("ext-grace-1");

        using var staleVerify = await http.GetAsync($"/ui/{slug}/verify-email?token={verifyToken}");
        await Assert.That(staleVerify.StatusCode).IsEqualTo(HttpStatusCode.BadRequest);
        await Assert.That((await store.GetUserAsync(userId))!.EmailVerified).IsFalse();

        // The claim moved: old email free, new email resolves, and both are usable again.
        await Assert.That(await store.FindUserByEmailAsync((await store.GetScimDirectoryAsync(directoryId))!.InstanceId, email.ToLowerInvariant())).IsNull();
        await Assert.That((await store.FindUserByEmailAsync((await store.GetScimDirectoryAsync(directoryId))!.InstanceId, newEmail.ToLowerInvariant()))!.Id).IsEqualTo(userId);
        await ProvisionAsync(http, token, directoryId, email);

        // Renaming onto an email another user now holds conflicts.
        var other = (await ProvisionAsync(http, token, directoryId, $"held-{Guid.NewGuid().ToString("N")[..8]}@example.com"));
        var conflict = await SendAsync(http, HttpMethod.Put, token, $"/scim/{directoryId}/Users/{userId}", new { userName = other.Email }, HttpStatusCode.Conflict, "application/scim+json");
        await Assert.That(conflict.GetProperty("status").GetString()).IsEqualTo("409");
        await Assert.That(conflict.GetProperty("scimType").GetString()).IsEqualTo("uniqueness");
        await SendAsync(http, HttpMethod.Put, token, $"/scim/{directoryId}/Users/{userId}", new { userName = "not-an-email" }, HttpStatusCode.BadRequest, "application/scim+json");
    }

    [Test]
    public async Task PatchTogglesActiveAndUserNameAndRejectsUnsupportedPaths()
    {
        var (service, _, slug, apiKey) = await StartAsync();
        var http = service.Http;
        var store = service.Store;

        var (directoryId, token) = await CreateDirectoryAsync(http, apiKey, slug);
        var instanceId = (await store.GetScimDirectoryAsync(directoryId))!.InstanceId;
        var (userId, email) = await ProvisionAsync(http, token, directoryId);

        // Deactivate → banned with the directory's reason.
        var deactivated = await SendAsync(http, HttpMethod.Patch, token, $"/scim/{directoryId}/Users/{userId}", new
        {
            Operations = new object[] { new { op = "replace", path = "active", value = false } },
        }, HttpStatusCode.OK, "application/scim+json");
        await Assert.That(deactivated.GetProperty("active").GetBoolean()).IsFalse();
        var banned = await store.GetUserAsync(userId);
        await Assert.That(banned!.Banned).IsTrue();
        await Assert.That(banned.BanReason).IsEqualTo("directory deprovisioned");
        await Assert.That((await store.ListAuditEventsAsync(instanceId, 100)).Any(e => e.Type == "directory.user_deprovisioned")).IsTrue();

        // Reactivate → clean unban; userName replace re-keys the email claim.
        var newEmail = $"moved-{Guid.NewGuid().ToString("N")[..8]}@example.com";
        var reactivated = await SendAsync(http, HttpMethod.Patch, token, $"/scim/{directoryId}/Users/{userId}", new
        {
            Operations = new object[]
            {
                new { op = "Replace", path = "active", value = true },
                new { op = "replace", path = "userName", value = newEmail },
            },
        }, HttpStatusCode.OK, "application/scim+json");
        await Assert.That(reactivated.GetProperty("active").GetBoolean()).IsTrue();
        await Assert.That(reactivated.GetProperty("userName").GetString()).IsEqualTo(newEmail);
        await Assert.That((await store.GetUserAsync(userId))!.Banned).IsFalse();
        await Assert.That(await store.FindUserByEmailAsync(instanceId, email.ToLowerInvariant())).IsNull();

        await SendAsync(http, HttpMethod.Patch, token, $"/scim/{directoryId}/Users/{userId}",
            new { Operations = new object[] { new { op = "replace", path = "name.givenName", value = "Nope" } } }, HttpStatusCode.BadRequest, "application/scim+json");
        await SendAsync(http, HttpMethod.Patch, token, $"/scim/{directoryId}/Users/{userId}",
            new { Operations = new object[] { new { op = "add", path = "active", value = true } } }, HttpStatusCode.BadRequest, "application/scim+json");
        await SendAsync(http, HttpMethod.Patch, token, $"/scim/{directoryId}/Users/usr_missing",
            new { Operations = new object[] { new { op = "replace", path = "active", value = true } } }, HttpStatusCode.NotFound, "application/scim+json");
    }

    [Test]
    public async Task DeleteSoftDeletesUserReleasesEmailAndDropsMembership()
    {
        var (service, instanceId, slug, apiKey) = await StartAsync();
        var http = service.Http;
        var store = service.Store;

        var orgId = await CreateOrganizationAsync(http, apiKey, slug, "Acme Corp");
        var (directoryId, token) = await CreateDirectoryAsync(http, apiKey, slug, orgId);
        var (userId, email) = await ProvisionAsync(http, token, directoryId);
        await Assert.That((await store.ListMembershipsByOrgAsync(orgId)).Count).IsEqualTo(1);

        var verificationId = "vt_" + Guid.NewGuid().ToString("N");
        await store.CreateVerificationTokenAsync(new VerificationToken
        {
            Id = verificationId,
            InstanceId = instanceId,
            UserId = userId,
            Purpose = "reset_password",
            TokenKey = verificationId,
            ExpiresAt = DateTimeOffset.UtcNow.AddHours(1).ToUnixTimeSeconds(),
        });

        using var deleted = await http.SendAsync(Request(HttpMethod.Delete, token, $"/scim/{directoryId}/Users/{userId}"));
        await Assert.That(deleted.StatusCode).IsEqualTo(HttpStatusCode.NoContent);

        await SendAsync(http, HttpMethod.Get, token, $"/scim/{directoryId}/Users/{userId}", null, HttpStatusCode.NotFound, "application/scim+json");

        await Assert.That(await store.GetVerificationTokenAsync(verificationId)).IsNull();
        var user = await store.GetUserAsync(userId);
        await Assert.That(user!.Deleted).IsTrue();
        await Assert.That(user.EmailKey).IsNull();
        await Assert.That(await store.FindUserByEmailAsync(instanceId, email.ToLowerInvariant())).IsNull();
        await Assert.That((await store.ListMembershipsByOrgAsync(orgId)).Count).IsEqualTo(0);
        await Assert.That((await store.ListAuditEventsAsync(instanceId, 100)).Any(e => e.Type == "directory.user_deprovisioned")).IsTrue();

        // The released email is immediately reusable by the next provisioning push.
        var (reusedId, _) = await ProvisionAsync(http, token, directoryId, email);
        await Assert.That(reusedId).IsNotEqualTo(userId);
    }

    [Test]
    public async Task GroupLifecycleCreatesListsPatchesAndDeletes()
    {
        var (service, _, slug, apiKey) = await StartAsync();
        var http = service.Http;

        var orgId = await CreateOrganizationAsync(http, apiKey, slug, "Acme Corp");
        var (directoryId, token) = await CreateDirectoryAsync(http, apiKey, slug, orgId);
        var (userA, _) = await ProvisionAsync(http, token, directoryId);
        var (userB, _) = await ProvisionAsync(http, token, directoryId);

        var created = await SendAsync(http, HttpMethod.Post, token, $"/scim/{directoryId}/Groups", new
        {
            displayName = "Engineers",
            members = new object[] { new { value = userA } },
        }, HttpStatusCode.Created, "application/scim+json");
        var groupId = created.GetProperty("id").GetString()!;
        await Assert.That(groupId.StartsWith("grp_")).IsTrue();
        await Assert.That(created.GetProperty("schemas").EnumerateArray().Single().GetString())
            .IsEqualTo("urn:ietf:params:scim:schemas:core:2.0:Group");
        await Assert.That(created.GetProperty("displayName").GetString()).IsEqualTo("Engineers");
        await Assert.That(created.GetProperty("members").EnumerateArray().Single().GetProperty("value").GetString()).IsEqualTo(userA);
        await Assert.That(created.GetProperty("meta").GetProperty("location").GetString()).IsEqualTo($"/scim/{directoryId}/Groups/{groupId}");

        var single = await SendAsync(http, HttpMethod.Get, token, $"/scim/{directoryId}/Groups/{groupId}", null, HttpStatusCode.OK, "application/scim+json");
        await Assert.That(single.GetProperty("displayName").GetString()).IsEqualTo("Engineers");
        var list = await SendAsync(http, HttpMethod.Get, token, $"/scim/{directoryId}/Groups", null, HttpStatusCode.OK, "application/scim+json");
        await Assert.That(list.GetProperty("Resources").EnumerateArray().Any(g => g.GetProperty("id").GetString() == groupId)).IsTrue();

        // Member add, then remove, via members replace.
        var added = await SendAsync(http, HttpMethod.Patch, token, $"/scim/{directoryId}/Groups/{groupId}", new
        {
            Operations = new object[] { new { op = "replace", path = "members", value = new object[] { new { value = userA }, new { value = userB } } } },
        }, HttpStatusCode.OK, "application/scim+json");
        await Assert.That(added.GetProperty("members").GetArrayLength()).IsEqualTo(2);
        var removed = await SendAsync(http, HttpMethod.Patch, token, $"/scim/{directoryId}/Groups/{groupId}", new
        {
            Operations = new object[] { new { op = "replace", path = "members", value = Array.Empty<object>() } },
        }, HttpStatusCode.OK, "application/scim+json");
        await Assert.That(removed.GetProperty("members").GetArrayLength()).IsEqualTo(0);

        var replaced = await SendAsync(http, HttpMethod.Put, token, $"/scim/{directoryId}/Groups/{groupId}", new
        {
            displayName = "Engineering",
            members = new object[] { new { value = userB } },
        }, HttpStatusCode.OK, "application/scim+json");
        await Assert.That(replaced.GetProperty("displayName").GetString()).IsEqualTo("Engineering");
        await Assert.That(replaced.GetProperty("members").EnumerateArray().Single().GetProperty("value").GetString()).IsEqualTo(userB);

        await SendAsync(http, HttpMethod.Post, token, $"/scim/{directoryId}/Groups", new { displayName = "Bad", members = new object[] { new { value = "usr_missing" } } }, HttpStatusCode.BadRequest, "application/scim+json");
        await SendAsync(http, HttpMethod.Post, token, $"/scim/{directoryId}/Groups", new { displayName = "" }, HttpStatusCode.BadRequest, "application/scim+json");

        using var deleted = await http.SendAsync(Request(HttpMethod.Delete, token, $"/scim/{directoryId}/Groups/{groupId}"));
        await Assert.That(deleted.StatusCode).IsEqualTo(HttpStatusCode.NoContent);
        await SendAsync(http, HttpMethod.Get, token, $"/scim/{directoryId}/Groups/{groupId}", null, HttpStatusCode.NotFound, "application/scim+json");
    }

    [Test]
    public async Task PaginationFollowsRfc7644ClampingRules()
    {
        var (service, _, slug, apiKey) = await StartAsync();
        var http = service.Http;
        var (directoryId, token) = await CreateDirectoryAsync(http, apiKey, slug);
        await ProvisionAsync(http, token, directoryId);
        await ProvisionAsync(http, token, directoryId);
        await ProvisionAsync(http, token, directoryId);

        // RFC 7644 3.4.2.4: startIndex below 1 reads as 1 — never a 400.
        var early = await SendAsync(http, HttpMethod.Get, token, $"/scim/{directoryId}/Users?startIndex=0", null, HttpStatusCode.OK, "application/scim+json");
        await Assert.That(early.GetProperty("startIndex").GetInt32()).IsEqualTo(1);
        await Assert.That(early.GetProperty("totalResults").GetInt32()).IsEqualTo(3);

        // count=0 and negative counts return no resources but still report totalResults.
        foreach (var query in new[] { "count=0", "count=-5" })
        {
            var empty = await SendAsync(http, HttpMethod.Get, token, $"/scim/{directoryId}/Users?{query}", null, HttpStatusCode.OK, "application/scim+json");
            await Assert.That(empty.GetProperty("totalResults").GetInt32()).IsEqualTo(3);
            await Assert.That(empty.GetProperty("Resources").GetArrayLength()).IsEqualTo(0);
            await Assert.That(empty.GetProperty("itemsPerPage").GetInt32()).IsEqualTo(0);
        }

        // A count above the server maximum is clamped, not rejected.
        var clamped = await SendAsync(http, HttpMethod.Get, token, $"/scim/{directoryId}/Users?count=1000", null, HttpStatusCode.OK, "application/scim+json");
        await Assert.That(clamped.GetProperty("Resources").GetArrayLength()).IsEqualTo(3);

        // Pages slice the full result set: totalResults stays 3 across pages.
        var page = await SendAsync(http, HttpMethod.Get, token, $"/scim/{directoryId}/Users?count=2&startIndex=2", null, HttpStatusCode.OK, "application/scim+json");
        await Assert.That(page.GetProperty("startIndex").GetInt32()).IsEqualTo(2);
        await Assert.That(page.GetProperty("itemsPerPage").GetInt32()).IsEqualTo(2);
        await Assert.That(page.GetProperty("Resources").GetArrayLength()).IsEqualTo(2);
        await Assert.That(page.GetProperty("totalResults").GetInt32()).IsEqualTo(3);
        var tail = await SendAsync(http, HttpMethod.Get, token, $"/scim/{directoryId}/Users?count=2&startIndex=3", null, HttpStatusCode.OK, "application/scim+json");
        await Assert.That(tail.GetProperty("Resources").GetArrayLength()).IsEqualTo(1);
        var firstPage = await SendAsync(http, HttpMethod.Get, token, $"/scim/{directoryId}/Users?count=2&startIndex=1", null, HttpStatusCode.OK, "application/scim+json");
        await Assert.That(tail.GetProperty("Resources").EnumerateArray().Select(u => u.GetProperty("id").GetString())
            .IntersectBy(firstPage.GetProperty("Resources").EnumerateArray().Select(u => u.GetProperty("id").GetString()), id => id)).IsEmpty();

        // Groups honor the same pagination contract; an unsupported filter is declined, not ignored.
        await SendAsync(http, HttpMethod.Post, token, $"/scim/{directoryId}/Groups", new { displayName = "G1" }, HttpStatusCode.Created, "application/scim+json");
        await SendAsync(http, HttpMethod.Post, token, $"/scim/{directoryId}/Groups", new { displayName = "G2" }, HttpStatusCode.Created, "application/scim+json");
        var groupPage = await SendAsync(http, HttpMethod.Get, token, $"/scim/{directoryId}/Groups?count=1&startIndex=2", null, HttpStatusCode.OK, "application/scim+json");
        await Assert.That(groupPage.GetProperty("Resources").GetArrayLength()).IsEqualTo(1);
        await Assert.That(groupPage.GetProperty("itemsPerPage").GetInt32()).IsEqualTo(1);
        await Assert.That(groupPage.GetProperty("totalResults").GetInt32()).IsEqualTo(2);
        var declined = await SendAsync(http, HttpMethod.Get, token,
            $"/scim/{directoryId}/Groups?filter={Uri.EscapeDataString("displayName eq \"G1\"")}", null, HttpStatusCode.BadRequest, "application/scim+json");
        await Assert.That(declined.GetProperty("scimType").GetString()).IsEqualTo("invalidFilter");
    }

    [Test]
    public async Task GroupListWalksEveryEnginePagePastTheFirstHundred()
    {
        var (service, instanceId, slug, apiKey) = await StartAsync();
        var http = service.Http;
        var (directoryId, token) = await CreateDirectoryAsync(http, apiKey, slug);

        // Seed directly through the store: 101 groups must not silently truncate at the engine's page size.
        var groups = new TerraScale.Auth.Scim.ScimGroups(service.Sdk, service.Database);
        var now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        for (var i = 0; i < 101; i++)
        {
            await groups.CreateAsync(new TerraScale.Auth.Scim.ScimGroup
            {
                Id = $"grp_bulk{i:000}_{SecureTokens.RandomToken(6)}",
                InstanceId = instanceId,
                DirectoryId = directoryId,
                DisplayName = $"Bulk {i:000}",
                MemberUserIds = [],
                CreatedAt = now,
                UpdatedAt = now,
            });
        }

        // Default paging: one 100-item page, but totalResults must count every match on every engine page.
        var list = await SendAsync(http, HttpMethod.Get, token, $"/scim/{directoryId}/Groups", null, HttpStatusCode.OK, "application/scim+json");
        await Assert.That(list.GetProperty("schemas").EnumerateArray().Single().GetString())
            .IsEqualTo("urn:ietf:params:scim:api:messages:2.0:ListResponse");
        await Assert.That(list.GetProperty("totalResults").GetInt32()).IsEqualTo(101);
        await Assert.That(list.GetProperty("Resources").GetArrayLength()).IsEqualTo(100);
        await Assert.That(list.GetProperty("startIndex").GetInt32()).IsEqualTo(1);
        await Assert.That(list.GetProperty("itemsPerPage").GetInt32()).IsEqualTo(100);

        // The 101st group is reachable on the next page instead of being lost to truncation.
        var next = await SendAsync(http, HttpMethod.Get, token, $"/scim/{directoryId}/Groups?count=100&startIndex=101", null, HttpStatusCode.OK, "application/scim+json");
        await Assert.That(next.GetProperty("Resources").GetArrayLength()).IsEqualTo(1);
        await Assert.That(next.GetProperty("totalResults").GetInt32()).IsEqualTo(101);
    }

    [Test]
    public async Task CreateResponsesCarryTheRfcLocationHeader()
    {
        var (service, _, slug, apiKey) = await StartAsync();
        var http = service.Http;
        var (directoryId, token) = await CreateDirectoryAsync(http, apiKey, slug);

        using var user = await http.SendAsync(Request(HttpMethod.Post, token, $"/scim/{directoryId}/Users",
            new { userName = $"loc-{Guid.NewGuid().ToString("N")[..8]}@example.com" }, "application/scim+json"));
        await Assert.That(user.StatusCode).IsEqualTo(HttpStatusCode.Created);
        var userId = (await user.Content.ReadFromJsonAsync<JsonElement>())!.GetProperty("id").GetString()!;
        await Assert.That(user.Headers.Location!.ToString()).IsEqualTo($"/scim/{directoryId}/Users/{userId}");

        using var group = await http.SendAsync(Request(HttpMethod.Post, token, $"/scim/{directoryId}/Groups",
            new { displayName = "Located" }, "application/scim+json"));
        await Assert.That(group.StatusCode).IsEqualTo(HttpStatusCode.Created);
        var groupId = (await group.Content.ReadFromJsonAsync<JsonElement>())!.GetProperty("id").GetString()!;
        await Assert.That(group.Headers.Location!.ToString()).IsEqualTo($"/scim/{directoryId}/Groups/{groupId}");
    }

    [Test]
    public async Task UnauthorizedResponsesAdvertiseTheBearerScheme()
    {
        var (service, _, slug, apiKey) = await StartAsync();
        var http = service.Http;
        var (directoryId, _) = await CreateDirectoryAsync(http, apiKey, slug);

        using var missing = await http.GetAsync($"/scim/{directoryId}/Users");
        await Assert.That(missing.StatusCode).IsEqualTo(HttpStatusCode.Unauthorized);
        await Assert.That(missing.Headers.WwwAuthenticate.Single(s => s.Scheme == "Bearer")).IsNotNull();
    }

    [Test]
    public async Task PatchWithoutPathReplacesAttributesOnTheWholeResource()
    {
        var (service, _, slug, apiKey) = await StartAsync();
        var http = service.Http;
        var store = service.Store;

        var (directoryId, token) = await CreateDirectoryAsync(http, apiKey, slug);
        var (userId, _) = await ProvisionAsync(http, token, directoryId);
        var (userA, _) = await ProvisionAsync(http, token, directoryId);

        // Path-less replace (RFC 7644 3.5.2): the value names the attributes to replace.
        var deactivated = await SendAsync(http, HttpMethod.Patch, token, $"/scim/{directoryId}/Users/{userId}", new
        {
            Operations = new object[] { new { op = "replace", value = new { active = false } } },
        }, HttpStatusCode.OK, "application/scim+json");
        await Assert.That(deactivated.GetProperty("active").GetBoolean()).IsFalse();
        await Assert.That((await store.GetUserAsync(userId))!.Banned).IsTrue();

        var group = await SendAsync(http, HttpMethod.Post, token, $"/scim/{directoryId}/Groups", new { displayName = "Engineers" }, HttpStatusCode.Created, "application/scim+json");
        var groupId = group.GetProperty("id").GetString()!;
        var renamed = await SendAsync(http, HttpMethod.Patch, token, $"/scim/{directoryId}/Groups/{groupId}", new
        {
            Operations = new object[] { new { op = "replace", value = new { displayName = "Renamed", members = new object[] { new { value = userA } } } } },
        }, HttpStatusCode.OK, "application/scim+json");
        await Assert.That(renamed.GetProperty("displayName").GetString()).IsEqualTo("Renamed");
        await Assert.That(renamed.GetProperty("members").EnumerateArray().Single().GetProperty("value").GetString()).IsEqualTo(userA);

        // Unknown attributes inside a path-less replace are rejected like unknown paths.
        await SendAsync(http, HttpMethod.Patch, token, $"/scim/{directoryId}/Users/{userId}",
            new { Operations = new object[] { new { op = "replace", value = new { nickName = "nope" } } } }, HttpStatusCode.BadRequest, "application/scim+json");
        var badPath = await SendAsync(http, HttpMethod.Patch, token, $"/scim/{directoryId}/Users/{userId}",
            new { Operations = new object[] { new { op = "replace", path = "nickName", value = "nope" } } }, HttpStatusCode.BadRequest, "application/scim+json");
        await Assert.That(badPath.GetProperty("scimType").GetString()).IsEqualTo("invalidPath");
    }

    [Test]
    public async Task NonJsonBodyIsABadRequestNotAServerError()
    {
        var (service, _, slug, apiKey) = await StartAsync();
        var http = service.Http;
        var (directoryId, token) = await CreateDirectoryAsync(http, apiKey, slug);
        var (userId, _) = await ProvisionAsync(http, token, directoryId);

        var request = new HttpRequestMessage(HttpMethod.Patch, $"/scim/{directoryId}/Users/{userId}")
        {
            Headers = { Authorization = new AuthenticationHeaderValue("Bearer", token) },
            Content = new StringContent("this is not json", System.Text.Encoding.UTF8, "text/plain"),
        };
        using var response = await http.SendAsync(request);
        await Assert.That(response.StatusCode).IsEqualTo(HttpStatusCode.BadRequest);
        var body = (await response.Content.ReadFromJsonAsync<JsonElement>())!;
        await Assert.That(body.GetProperty("schemas").EnumerateArray().Single().GetString())
            .IsEqualTo("urn:ietf:params:scim:api:messages:2.0:Error");
        await Assert.That(body.GetProperty("status").GetString()).IsEqualTo("400");
    }

    /// <summary>A group created through one directory, returning its id.</summary>
    private static async Task<string> CreateGroupAsync(HttpClient http, string token, string directoryId, string displayName, params string[] memberIds)
    {
        var created = await SendAsync(http, HttpMethod.Post, token, $"/scim/{directoryId}/Groups", new
        {
            displayName,
            members = memberIds.Select(id => new { value = id }).ToArray(),
        }, HttpStatusCode.Created, "application/scim+json");
        return created.GetProperty("id").GetString()!;
    }

    private static async Task<List<string>> GroupMembersAsync(HttpClient http, string token, string directoryId, string groupId)
    {
        var group = await SendAsync(http, HttpMethod.Get, token, $"/scim/{directoryId}/Groups/{groupId}", null, HttpStatusCode.OK, "application/scim+json");
        return group.GetProperty("members").EnumerateArray().Select(m => m.GetProperty("value").GetString()!).ToList();
    }

    private static async Task<HttpResponseMessage> GetAccountPageAsync(HttpClient http, string slug, string sessionToken)
    {
        var request = new HttpRequestMessage(HttpMethod.Get, $"/ui/{slug}/account");
        request.Headers.TryAddWithoutValidation("Cookie", $"tsauth_session={sessionToken}");
        return await http.SendAsync(request);
    }

    [Test]
    public async Task DeprovisionBansKillsSessionsDropsMembershipsGroupsAndClaims()
    {
        var (service, instanceId, slug, apiKey) = await StartAsync();
        var http = service.Http;
        var store = service.Store;

        var orgId = await CreateOrganizationAsync(http, apiKey, slug, "Acme Corp");
        var (directoryId, token) = await CreateDirectoryAsync(http, apiKey, slug, orgId);
        var (userId, email) = await ProvisionAsync(http, token, directoryId);
        var (otherId, _) = await ProvisionAsync(http, token, directoryId);

        // A live session and a username claim: both must die with the deprovision.
        var sessionToken = "e2e" + SecureTokens.RandomToken(32);
        var now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        await store.CreateSessionAsync(new Session
        {
            Id = "ses_" + SecureTokens.RandomToken(20),
            InstanceId = instanceId,
            UserId = userId,
            TokenKey = SecureTokens.Sha256Hex(sessionToken),
            Status = "active",
            CreatedAt = now,
            ExpiresAt = DateTimeOffset.UtcNow.AddHours(1).ToUnixTimeSeconds(),
            LastActiveAt = now,
        });
        await store.ClaimUniqueAsync("user-username", $"{instanceId}:legacy", userId);
        await store.UpsertUserAsync((await store.GetUserAsync(userId))! with { Username = "Legacy", UsernameKey = $"{instanceId}:legacy" });
        using (var authed = await GetAccountPageAsync(http, slug, sessionToken))
            await Assert.That(authed.StatusCode).IsEqualTo(HttpStatusCode.OK);

        // Groups in this directory and in a sibling directory of the same instance.
        var groupA = await CreateGroupAsync(http, token, directoryId, "Engineers", userId, otherId);
        var groupB = await CreateGroupAsync(http, token, directoryId, "Admins", userId);
        var (siblingDirectoryId, siblingToken) = await CreateDirectoryAsync(http, apiKey, slug);
        var groupC = await CreateGroupAsync(http, siblingToken, siblingDirectoryId, "Sibling", userId);

        await SendAsync(http, HttpMethod.Patch, token, $"/scim/{directoryId}/Users/{userId}", new
        {
            Operations = new object[] { new { op = "replace", path = "active", value = false } },
        }, HttpStatusCode.OK, "application/scim+json");

        // Banned, with the strings kept but both identifier claims stripped.
        var deprovisioned = (await store.GetUserAsync(userId))!;
        await Assert.That(deprovisioned.Banned).IsTrue();
        await Assert.That(deprovisioned.BanReason).IsEqualTo("directory deprovisioned");
        await Assert.That(deprovisioned.Email).IsEqualTo(email);
        await Assert.That(deprovisioned.EmailKey).IsNull();
        await Assert.That(deprovisioned.UsernameKey).IsNull();
        await Assert.That(await store.FindUserByEmailAsync(instanceId, email.ToLowerInvariant())).IsNull();

        // Every membership and group listing in the instance is dropped — including the sibling directory's.
        await Assert.That((await store.ListMembershipsByOrgAsync(orgId)).Any(m => m.UserId == userId)).IsFalse();
        await Assert.That(await GroupMembersAsync(http, token, directoryId, groupA)).IsEquivalentTo([otherId]);
        await Assert.That(await GroupMembersAsync(http, token, directoryId, groupB)).IsEmpty();
        await Assert.That(await GroupMembersAsync(http, siblingToken, siblingDirectoryId, groupC)).IsEmpty();

        // The live session no longer resolves.
        using (var dead = await GetAccountPageAsync(http, slug, sessionToken))
        {
            await Assert.That(dead.StatusCode).IsEqualTo(HttpStatusCode.Redirect);
            await Assert.That(dead.Headers.Location!.ToString()).Contains("sign-in");
        }

        // Both released identifiers are immediately reusable.
        var (reusedId, _) = await ProvisionAsync(http, token, directoryId, email);
        await Assert.That(reusedId).IsNotEqualTo(userId);
        await SendAsync(http, HttpMethod.Post, apiKey, $"/v1/{slug}/users", new
        {
            email = $"legacy-{Guid.NewGuid().ToString("N")[..8]}@example.com",
            username = "Legacy",
        }, HttpStatusCode.Created);

        // Reactivating the stripped user conflicts on the taken address instead of silently overlapping.
        var conflict = await SendAsync(http, HttpMethod.Patch, token, $"/scim/{directoryId}/Users/{userId}", new
        {
            Operations = new object[] { new { op = "replace", path = "active", value = true } },
        }, HttpStatusCode.Conflict, "application/scim+json");
        await Assert.That(conflict.GetProperty("scimType").GetString()).IsEqualTo("uniqueness");
    }

    [Test]
    public async Task ReactivationReClaimsTheReleasedIdentifiers()
    {
        var (service, instanceId, slug, apiKey) = await StartAsync();
        var http = service.Http;
        var store = service.Store;

        var orgId = await CreateOrganizationAsync(http, apiKey, slug, "Acme Corp");
        var (directoryId, token) = await CreateDirectoryAsync(http, apiKey, slug, orgId);

        // Deactivate via PUT (the other deprovision verb), reactivate via PATCH active=true.
        var (userId, email) = await ProvisionAsync(http, token, directoryId);
        await SendAsync(http, HttpMethod.Put, token, $"/scim/{directoryId}/Users/{userId}", new { userName = email, active = false }, HttpStatusCode.OK, "application/scim+json");
        await Assert.That((await store.GetUserAsync(userId))!.EmailKey).IsNull();
        await Assert.That((await store.ListMembershipsByOrgAsync(orgId)).Any(m => m.UserId == userId)).IsFalse();

        await SendAsync(http, HttpMethod.Patch, token, $"/scim/{directoryId}/Users/{userId}", new
        {
            Operations = new object[] { new { op = "replace", path = "active", value = true } },
        }, HttpStatusCode.OK, "application/scim+json");

        var reactivated = (await store.GetUserAsync(userId))!;
        await Assert.That(reactivated.Banned).IsFalse();
        await Assert.That(reactivated.EmailKey).IsEqualTo($"{instanceId}:{email.ToLowerInvariant()}");
        await Assert.That((await store.FindUserByEmailAsync(instanceId, email.ToLowerInvariant()))!.Id).IsEqualTo(userId);
        // The membership does not silently return; the directory re-pushes group state.
        await Assert.That((await store.ListMembershipsByOrgAsync(orgId)).Any(m => m.UserId == userId)).IsFalse();

        // A PUT repeating the same userName on a still-deprovisioned user restores the claim too.
        var (otherId, otherEmail) = await ProvisionAsync(http, token, directoryId);
        await SendAsync(http, HttpMethod.Patch, token, $"/scim/{directoryId}/Users/{otherId}", new
        {
            Operations = new object[] { new { op = "replace", path = "active", value = false } },
        }, HttpStatusCode.OK, "application/scim+json");
        await SendAsync(http, HttpMethod.Put, token, $"/scim/{directoryId}/Users/{otherId}", new { userName = otherEmail, active = true }, HttpStatusCode.OK, "application/scim+json");
        var restored = (await store.GetUserAsync(otherId))!;
        await Assert.That(restored.Banned).IsFalse();
        await Assert.That(restored.EmailKey).IsEqualTo($"{instanceId}:{otherEmail.ToLowerInvariant()}");
    }

    [Test]
    public async Task DeleteWalksEveryMembershipPastTheFirstHundredAndCleansGroupListings()
    {
        var (service, _, slug, apiKey) = await StartAsync();
        var http = service.Http;
        var store = service.Store;

        var orgId = await CreateOrganizationAsync(http, apiKey, slug, "Acme Corp");
        var (directoryId, token) = await CreateDirectoryAsync(http, apiKey, slug, orgId);
        var (userId, _) = await ProvisionAsync(http, token, directoryId);
        var (otherId, _) = await ProvisionAsync(http, token, directoryId);
        var groupId = await CreateGroupAsync(http, token, directoryId, "Engineers", userId, otherId);

        // Seed 101 memberships with ids that sort after the SCIM-provisioned one, so a
        // bounded first-page walk drops exactly the tail. The directory's own org is NOT
        // any of these: every seeded row is foreign to it.
        var now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        var tailOrgId = "";
        for (var i = 0; i <= 100; i++)
        {
            var seededOrgId = "org_" + SecureTokens.RandomToken(20);
            await store.CreateOrganizationAsync(new Organization
            {
                Id = seededOrgId,
                InstanceId = (await store.GetScimDirectoryAsync(directoryId))!.InstanceId,
                Name = $"Walk {i:000}",
                SlugKey = $"walk-{i:000}-{Guid.NewGuid().ToString("N")[..6]}",
                CreatedAt = now,
            });
            await store.CreateMembershipAsync(new OrgMembership
            {
                Id = $"mem_walk{i:000}_{Guid.NewGuid().ToString("N")[..6]}",
                InstanceId = (await store.GetScimDirectoryAsync(directoryId))!.InstanceId,
                OrgId = seededOrgId,
                UserId = userId,
                MemberKey = $"{seededOrgId}:{userId}",
                Role = "member",
                CreatedAt = now,
            }, default);
            if (i == 100)
                tailOrgId = seededOrgId;
        }
        await Assert.That((await store.ListMembershipsByUserAsync(userId)).Count).IsEqualTo(102);

        using var deleted = await http.SendAsync(Request(HttpMethod.Delete, token, $"/scim/{directoryId}/Users/{userId}"));
        await Assert.That(deleted.StatusCode).IsEqualTo(HttpStatusCode.NoContent);

        // Every membership is gone — including the 102nd that a bounded walk would spare.
        await Assert.That((await store.ListMembershipsByUserAsync(userId)).Count).IsEqualTo(0);
        await Assert.That((await store.ListMembershipsByOrgAsync(tailOrgId)).Count).IsEqualTo(0);
        await Assert.That((await store.ListMembershipsByOrgAsync(orgId)).Any(m => m.UserId == userId)).IsFalse();
        // The group listing no longer carries the deleted user.
        await Assert.That(await GroupMembersAsync(http, token, directoryId, groupId)).IsEquivalentTo([otherId]);
    }

    [Test]
    public async Task DiscoveryDocumentsExposeServiceProviderConfigResourceTypesAndSchemas()
    {
        var (service, _, slug, apiKey) = await StartAsync();
        var http = service.Http;
        var (directoryId, token) = await CreateDirectoryAsync(http, apiKey, slug);

        var config = await SendAsync(http, HttpMethod.Get, token, $"/scim/{directoryId}/ServiceProviderConfig", null, HttpStatusCode.OK, "application/scim+json");
        await Assert.That(config.GetProperty("schemas").EnumerateArray().Single().GetString())
            .IsEqualTo("urn:ietf:params:scim:schemas:core:2.0:ServiceProviderConfig");
        await Assert.That(config.GetProperty("patch").GetProperty("supported").GetBoolean()).IsTrue();
        await Assert.That(config.GetProperty("bulk").GetProperty("supported").GetBoolean()).IsFalse();
        await Assert.That(config.GetProperty("filter").GetProperty("supported").GetBoolean()).IsTrue();
        await Assert.That(config.GetProperty("filter").GetProperty("maxResults").GetInt32()).IsEqualTo(100);
        await Assert.That(config.GetProperty("meta").GetProperty("location").GetString()).IsEqualTo($"/scim/{directoryId}/ServiceProviderConfig");

        var resourceTypes = await SendAsync(http, HttpMethod.Get, token, $"/scim/{directoryId}/ResourceTypes", null, HttpStatusCode.OK, "application/scim+json");
        var resourceIds = resourceTypes.GetProperty("Resources").EnumerateArray().Select(r => r.GetProperty("id").GetString()!).ToList();
        await Assert.That(resourceIds).IsEquivalentTo(["User", "Group"]);
        await Assert.That(resourceTypes.GetProperty("totalResults").GetInt32()).IsEqualTo(2);

        var schemas = await SendAsync(http, HttpMethod.Get, token, $"/scim/{directoryId}/Schemas", null, HttpStatusCode.OK, "application/scim+json");
        var schemaIds = schemas.GetProperty("Resources").EnumerateArray().Select(s => s.GetProperty("id").GetString()!).ToList();
        await Assert.That(schemaIds).IsEquivalentTo(["urn:ietf:params:scim:schemas:core:2.0:User", "urn:ietf:params:scim:schemas:core:2.0:Group"]);
    }
}
