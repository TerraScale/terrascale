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
        var (instanceId, slug, _) = await service.CreateInstanceAsync("scim-" + Guid.NewGuid().ToString("N")[..8]);

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
        await Assert.That(missingBody.GetProperty("status").GetInt32()).IsEqualTo(401);

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
        await Assert.That(duplicate.GetProperty("status").GetInt32()).IsEqualTo(409);

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

        // The claim moved: old email free, new email resolves, and both are usable again.
        await Assert.That(await store.FindUserByEmailAsync((await store.GetScimDirectoryAsync(directoryId))!.InstanceId, email.ToLowerInvariant())).IsNull();
        await Assert.That((await store.FindUserByEmailAsync((await store.GetScimDirectoryAsync(directoryId))!.InstanceId, newEmail.ToLowerInvariant()))!.Id).IsEqualTo(userId);
        await ProvisionAsync(http, token, directoryId, email);

        // Renaming onto an email another user now holds conflicts.
        var other = (await ProvisionAsync(http, token, directoryId, $"held-{Guid.NewGuid().ToString("N")[..8]}@example.com"));
        var conflict = await SendAsync(http, HttpMethod.Put, token, $"/scim/{directoryId}/Users/{userId}", new { userName = other.Email }, HttpStatusCode.Conflict, "application/scim+json");
        await Assert.That(conflict.GetProperty("status").GetInt32()).IsEqualTo(409);
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

        using var deleted = await http.SendAsync(Request(HttpMethod.Delete, token, $"/scim/{directoryId}/Users/{userId}"));
        await Assert.That(deleted.StatusCode).IsEqualTo(HttpStatusCode.NoContent);

        await SendAsync(http, HttpMethod.Get, token, $"/scim/{directoryId}/Users/{userId}", null, HttpStatusCode.NotFound, "application/scim+json");

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
