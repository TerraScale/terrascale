using System.Net;
using System.Text;
using System.Text.Json;
using TUnit.Assertions.Enums;
using TerraScale.Dashboard.Features.Accounts;
using TerraScale.Dashboard.Features.ApiKeys;

namespace TerraScale.Dashboard.E2E.Features.Data;

public sealed class ProjectApiKeyContractTests
{
    private const string OrganizationId = "01JA0000000000000000000002";
    private const string ProjectId = "01JA0000000000000000000001";
    private const string KeyId = "01JA0000000000000000000003";
    private const string DatabaseId = "01JA0000000000000000000004";
    private const string CreatorId = "01JA0000000000000000000009";

    [Test]
    [Arguments("documents:read", "Read-only key")]
    [Arguments("documents:write", "Write-only key")]
    [Arguments("documents:read,documents:write", "Read-write key")]
    [Arguments("documents:read,documents:write,databases:read,databases:write", "Full-access key")]
    public async Task CredentialLabelsMatchTheCanonicalPermissions(string scopes, string label)
    {
        var key = new ProjectCredential(KeyId, ProjectId, "Synthetic", scopes.Split(','), null, "", DateTimeOffset.UtcNow);
        await Assert.That(ApiKeysPage.CredentialType(key).Label).IsEqualTo(label);
    }

    [Test]
    [Arguments(CreatorId, true)]
    [Arguments(CreatorId, false)]
    [Arguments("", false)]
    public async Task CreatorProvenanceUsesIssuerIdentityAndOnlyTheScopedOrganizationMember(string createdBy, bool knownMember)
    {
        using var wire = new Wire { CreatedBy = createdBy };
        var context = Context();
        context.Organization!.Members["user"].Email = "current-user@example.test";
        if (knownMember)
            context.Organization!.Members.Add(CreatorId, new() { UserId = CreatorId, Email = "creator@example.test" });
        var service = new ApiKeyService(wire, TimeProvider.System);
        var listed = (await service.ListAsync(context, CancellationToken.None)).Single();
        var created = await service.CreateAsync(context, "api-key", "Synthetic", DatabaseId, ["documents:read", "documents:write"], 0, [], CancellationToken.None);
        var rotated = await service.RotateAsync(context, KeyId, 0, CancellationToken.None);
        foreach (var key in new[] { listed, created.Key, rotated.Key })
        {
            await Assert.That(key.CreatedByUserId).IsEqualTo(createdBy);
            await Assert.That(key.CreatedByEmail).IsEqualTo(knownMember ? "creator@example.test" : "");
            foreach (var metadata in new[] { ApiKeyService.Metadata(key), ApiKeyService.CredentialMetadata(key) })
            {
                await Assert.That(metadata.CreatedByUserId).IsEqualTo(createdBy);
                await Assert.That(metadata.CreatedByEmail).IsEqualTo(key.CreatedByEmail);
            }
        }
    }

    [Test]
    public async Task IssuanceUsesOriginalUserBearerAndCanonicalRestrictionsWithoutPersistingSecret()
    {
        using var wire = new Wire();
        var context = Context();
        var service = new ApiKeyService(wire, TimeProvider.System);
        var created = await service.CreateAsync(context, "api-key", "Synthetic", DatabaseId,
            ["documents:read", "documents:write"], 0, [], CancellationToken.None);
        using var request = JsonDocument.Parse(wire.Body!);
        await Assert.That(request.RootElement.TryGetProperty("expires_at", out var noExpiry) && noExpiry.ValueKind != JsonValueKind.Null).IsFalse();
        await Assert.That(request.RootElement.TryGetProperty("allowed_cidrs", out _)).IsFalse();
        await Assert.That(created.Key.AllowedCidrs).IsEquivalentTo(Array.Empty<string>());
        await Assert.That(request.RootElement.GetProperty("database").GetString()).IsEqualTo("app");
        await Assert.That(request.RootElement.GetProperty("scopes").EnumerateArray().Select(item => item.GetString()!))
            .IsEquivalentTo(new[] { "documents:read", "documents:write" });
        await Assert.That(created.Secret).IsEqualTo(KeyId + "_synthetic-secret");
        await Assert.That(created.Key.LastFour).IsEqualTo("");
        await Assert.That(ApiKeyService.Metadata(created.Key).DatabaseName).IsEqualTo("app");
        var listed = await service.ListAsync(context, CancellationToken.None);
        await Assert.That(wire.Query).IsEqualTo("?limit=100");
        await Assert.That(listed.Single().Id).IsEqualTo(KeyId);
        await Assert.That(JsonSerializer.Serialize(listed).Contains(created.Secret, StringComparison.Ordinal)).IsFalse();
        await Assert.That(wire.Requests).IsEqualTo(2);
    }

    [Test]
    [Arguments(30)]
    [Arguments(90)]
    [Arguments(365)]
    public async Task IssuanceSendsTheChosenExpiryAndChecksTheEchoedOne(int days)
    {
        var now = new DateTimeOffset(2026, 10, 23, 12, 0, 0, 750, TimeSpan.Zero);
        using var wire = new Wire();
        var created = await new ApiKeyService(wire, new FixedTime(now)).CreateAsync(Context(), "api-key", "Synthetic", DatabaseId,
            ["documents:read", "documents:write"], days, [], CancellationToken.None);
        using var request = JsonDocument.Parse(wire.Body!);
        var expected = new DateTimeOffset(2026, 10, 23, 12, 0, 0, TimeSpan.Zero).AddDays(days);
        await Assert.That(request.RootElement.GetProperty("expires_at").GetDateTimeOffset()).IsEqualTo(expected);
        await Assert.That(created.Key.ExpiresAt).IsEqualTo(expected);
        await Assert.That(ApiKeyService.Metadata(created.Key).ExpiresAt).IsEqualTo(expected.ToString("yyyy-MM-dd'T'HH:mm:ss'Z'", System.Globalization.CultureInfo.InvariantCulture));
    }

    [Test]
    [Arguments(-1)]
    [Arguments(7)]
    [Arguments(366)]
    public async Task IssuanceRefusesAnExpiryOutsideTheOfferedChoices(int days)
    {
        using var wire = new Wire();
        AccountAccessException? rejected = null;
        try { await new ApiKeyService(wire, TimeProvider.System).CreateAsync(Context(), "api-key", "Synthetic", "", ["documents:read"], days, [], CancellationToken.None); }
        catch (AccountAccessException error) { rejected = error; }
        await Assert.That(rejected?.StatusCode).IsEqualTo(422);
        await Assert.That(wire.Requests).IsEqualTo(0);
    }

    [Test]
    public async Task IssuanceRefusesADifferentEchoedExpiry()
    {
        using var wire = new Wire { Failure = "expiry" };
        HttpRequestException? rejected = null;
        try { await new ApiKeyService(wire, TimeProvider.System).CreateAsync(Context(), "api-key", "Synthetic", DatabaseId, ["documents:read", "documents:write"], 30, [], CancellationToken.None); }
        catch (HttpRequestException error) { rejected = error; }
        await Assert.That(rejected?.Message).IsEqualTo("Credential issuance returned different restrictions.");
    }

    [Test]
    [Arguments("2026-10-22T09:15:30+00:00", "2026-10-22T09:15:30Z")]
    [Arguments(null, "")]
    public async Task ListingMapsLastUsedIntoCredentialMetadata(string? lastUsed, string expected)
    {
        using var wire = new Wire { LastUsedAt = lastUsed };
        var key = (await new ApiKeyService(wire, TimeProvider.System).ListAsync(Context(), CancellationToken.None)).Single();
        await Assert.That(key.LastUsedAt).IsEqualTo(lastUsed is null ? null : DateTimeOffset.Parse(lastUsed, System.Globalization.CultureInfo.InvariantCulture));
        await Assert.That(ApiKeyService.CredentialMetadata(key).LastUsedAt).IsEqualTo(expected);
        await Assert.That(ApiKeyService.Metadata(key).LastUsedAt).IsEqualTo(expected);
    }

    [Test]
    public async Task RestrictionSurvivesMissingLocalDatabaseAndRevokeVerifiesKeyBeforeDelete()
    {
        using var wire = new Wire();
        var context = Context();
        context.Project!.Databases.Clear();
        var service = new ApiKeyService(wire, TimeProvider.System);
        var key = (await service.ListAsync(context, CancellationToken.None)).Single();
        await Assert.That(key.DatabaseId).IsNull();
        await Assert.That(ApiKeyService.CredentialMetadata(key).DatabaseName).IsEqualTo("app");
        await service.RevokeAsync(context, KeyId, CancellationToken.None);
        await Assert.That(wire.Methods).IsEquivalentTo(new[] { HttpMethod.Get, HttpMethod.Get, HttpMethod.Delete }, CollectionOrdering.Matching);
    }

    [Test]
    [Arguments("committed", true)]
    [Arguments("refused", false)]
    public async Task RevokeReportsTheKeysRealStateAfterAFailedAnswer(string failure, bool revoked)
    {
        using var wire = new Wire { Failure = failure };
        HttpRequestException? rejected = null;
        try { await new ApiKeyService(wire, TimeProvider.System).RevokeAsync(Context(), KeyId, CancellationToken.None); }
        catch (HttpRequestException error) { rejected = error; }
        await Assert.That(rejected is null).IsEqualTo(revoked);
        await Assert.That(rejected?.StatusCode).IsEqualTo(revoked ? null : HttpStatusCode.ServiceUnavailable);
        await Assert.That(wire.Methods).IsEquivalentTo(new[] { HttpMethod.Get, HttpMethod.Delete, HttpMethod.Get }, CollectionOrdering.Matching);
    }

    [Test]
    public async Task RotateVerifiesTheKeyAndSendsTheGracePeriodForTheOldSecret()
    {
        using var wire = new Wire();
        var rotated = await new ApiKeyService(wire, TimeProvider.System).RotateAsync(Context(), KeyId, 3600, CancellationToken.None);
        await Assert.That(wire.Methods).IsEquivalentTo(new[] { HttpMethod.Get, HttpMethod.Post }, CollectionOrdering.Matching);
        await Assert.That(wire.Path).IsEqualTo($"/v1/terrascale/organizations/{OrganizationId}/projects/{ProjectId}/api-keys/{KeyId}/rotate");
        using var request = JsonDocument.Parse(wire.Body!);
        await Assert.That(request.RootElement.GetProperty("previous_secret_ttl").GetInt32()).IsEqualTo(3600);
        await Assert.That(rotated.Key.Id).IsEqualTo(KeyId);
        await Assert.That(rotated.Secret).IsEqualTo(KeyId + "_synthetic-secret");
    }

    [Test]
    [Arguments("pending", 0, 409, 2)]
    [Arguments("", -1, 422, 0)]
    [Arguments("", 86401, 422, 0)]
    public async Task RotateRefusesAPendingGraceOrAnOutOfRangeGrace(string failure, int ttl, int status, int requests)
    {
        using var wire = new Wire { Failure = failure };
        AccountAccessException? rejected = null;
        try { await new ApiKeyService(wire, TimeProvider.System).RotateAsync(Context(), KeyId, ttl, CancellationToken.None); }
        catch (AccountAccessException error) { rejected = error; }
        await Assert.That(rejected?.StatusCode).IsEqualTo(status);
        await Assert.That(wire.Requests).IsEqualTo(requests);
    }

    [Test]
    [Arguments("foreign")]
    [Arguments("repeated")]
    public async Task ListingRefusesForeignIdentityOrRepeatedPagination(string failure)
    {
        using var wire = new Wire { Failure = failure };
        HttpRequestException? rejected = null;
        try { await new ApiKeyService(wire, TimeProvider.System).ListAsync(Context(), CancellationToken.None); }
        catch (HttpRequestException error) { rejected = error; }
        await Assert.That(rejected).IsNotNull();
        await Assert.That(wire.Requests).IsEqualTo(failure == "foreign" ? 1 : 2);
        await Assert.That(wire.Methods.All(method => method == HttpMethod.Get)).IsTrue();
    }

    [Test]
    public async Task IssuanceSendsAllowedCidrsAndKeepsTsAuthsCanonicalList()
    {
        using var wire = new Wire();
        var created = await new ApiKeyService(wire, TimeProvider.System).CreateAsync(Context(), "api-key", "Synthetic", DatabaseId,
            ["documents:read", "documents:write"], 0, ApiKeyService.ParseCidrs("203.0.113.0/24\n198.51.100.7, 2001:db8::/48"), CancellationToken.None);
        using var request = JsonDocument.Parse(wire.Body!);
        await Assert.That(request.RootElement.GetProperty("allowed_cidrs").EnumerateArray().Select(item => item.GetString()!))
            .IsEquivalentTo(new[] { "203.0.113.0/24", "198.51.100.7", "2001:db8::/48" }, CollectionOrdering.Matching);
        await Assert.That(created.Key.AllowedCidrs!).IsEquivalentTo(new[] { "203.0.113.0/24", "198.51.100.7/32", "2001:db8::/48" }, CollectionOrdering.Matching);
        await Assert.That(ApiKeyService.CredentialMetadata(created.Key).AllowedCidrs!).IsEquivalentTo(created.Key.AllowedCidrs!);
    }

    [Test]
    public async Task AllowlistReplacementPutsTheKeyAndProjectListsAndShowsTsAuthsRefusal()
    {
        using var wire = new Wire();
        var service = new ApiKeyService(wire, TimeProvider.System);
        var key = await service.SetAllowedCidrsAsync(Context(), KeyId, ["198.51.100.7"], CancellationToken.None);
        await Assert.That(wire.Methods.Single()).IsEqualTo(HttpMethod.Put);
        await Assert.That(wire.Path).IsEqualTo($"/v1/terrascale/organizations/{OrganizationId}/projects/{ProjectId}/api-keys/{KeyId}/allowed-cidrs");
        await Assert.That(key.AllowedCidrs!).IsEquivalentTo(new[] { "198.51.100.7/32" });
        var project = await service.SetProjectAllowedCidrsAsync(Context(), ["203.0.113.0/24"], CancellationToken.None);
        await Assert.That(wire.Path).IsEqualTo($"/v1/terrascale/organizations/{OrganizationId}/projects/{ProjectId}/allowed-cidrs");
        await Assert.That(project).IsEquivalentTo(new[] { "203.0.113.0/24" });
        await Assert.That(await service.ProjectAllowedCidrsAsync(Context(), CancellationToken.None)).IsEquivalentTo(Array.Empty<string>());

        using var refusing = new Wire { Failure = "bad-cidr" };
        AccountAccessException? rejected = null;
        try { await new ApiKeyService(refusing, TimeProvider.System).SetAllowedCidrsAsync(Context(), KeyId, ["10.0.0.0/33"], CancellationToken.None); }
        catch (AccountAccessException error) { rejected = error; }
        await Assert.That(rejected?.StatusCode).IsEqualTo(400);
        await Assert.That(rejected?.Message).IsEqualTo("allowed_cidrs[0] is not a valid CIDR");
        rejected = null;
        try { await service.SetAllowedCidrsAsync(Context(), KeyId, Enumerable.Range(0, 33).Select(index => "198.51.100." + index).ToArray(), CancellationToken.None); }
        catch (AccountAccessException error) { rejected = error; }
        await Assert.That(rejected?.StatusCode).IsEqualTo(422);
    }

    [Test]
    public async Task KeysExpiringWithinSevenDaysNotifyOnceWithTheirDate()
    {
        var now = new DateTimeOffset(2026, 10, 24, 12, 0, 0, TimeSpan.Zero);
        var context = Context();
        ProjectCredential Key(string id, string name, DateTimeOffset? expires) => new(id, ProjectId, name, ["documents:read"], null, "", now.AddDays(-30)) { ExpiresAt = expires };
        var keys = new[] { Key(KeyId, "soon", now.AddDays(3)), Key("01JA0000000000000000000006", "later", now.AddDays(8)),
            Key("01JA0000000000000000000007", "expired", now.AddMinutes(-1)), Key("01JA0000000000000000000008", "forever", null) };
        var due = ApiKeyExpiryNotifications.Due(context, keys, [], now);
        await Assert.That(due.Length).IsEqualTo(1);
        await Assert.That(due[0].Title).IsEqualTo("API key soon expires on Oct 27, 2026");
        await Assert.That(due[0].ProjectId).IsEqualTo(ProjectId);
        await Assert.That(due[0].VisibleTo(context)).IsTrue();
        await Assert.That(ApiKeyExpiryNotifications.Due(context, keys, due, now.AddHours(1)).Length).IsEqualTo(0);
        await Assert.That(keys.Select(key => ApiKeyService.ExpiresSoon(key, now))).IsEquivalentTo(new[] { true, false, false, false }, CollectionOrdering.Matching);
    }

    private static AccountContext Context()
    {
        var project = new ProjectRecord { Id = ProjectId, OrganizationId = OrganizationId, Name = "project" };
        project.Databases.Add(DatabaseId, new() { Id = DatabaseId, ProjectId = ProjectId, Name = "app" });
        var organization = new OrganizationRecord { Id = OrganizationId, Name = "org", OwnerUserId = "user" };
        organization.Members.Add("user", new() { UserId = "user", Role = "owner", Status = "active" });
        organization.Projects.Add(ProjectId, project);
        return new(new("user", "Synthetic", "synthetic@example.test", "original-user-token", null, DateTimeOffset.UtcNow.AddMinutes(5)),
            new() { UserId = "user" }, [organization], organization, project);
    }

    private sealed class FixedTime(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }

    private sealed class Wire : HttpMessageHandler, IHttpClientFactory
    {
        public string Failure { get; init; } = "";
        public string CreatedBy { get; init; } = "user";
        public string? LastUsedAt { get; init; }
        public int Requests { get; private set; }
        public string? Body { get; private set; }
        public string? Query { get; private set; }
        public string? Path { get; private set; }
        public List<HttpMethod> Methods { get; } = [];
        private bool Deleted { get; set; }
        private static HttpResponseMessage Error(HttpStatusCode status, string code) =>
            new(status) { Content = new StringContent(JsonSerializer.Serialize(new { error = new { code, message = "Synthetic failure" } }), Encoding.UTF8, "application/json") };
        public HttpClient CreateClient(string name) => new(this, false);
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var root = $"/v1/terrascale/organizations/{OrganizationId}/projects/{ProjectId}/api-keys";
            if (request.RequestUri!.Host != "auth.terrascale.tech" || request.Headers.Authorization?.Parameter != "original-user-token" ||
                request.RequestUri.AbsolutePath is not { } path || path != root && path != root + "/" + KeyId && path != root + "/" + KeyId + "/rotate" &&
                path != root + "/" + KeyId + "/allowed-cidrs" && path != root[..^"/api-keys".Length] && path != root[..^"/api-keys".Length] + "/allowed-cidrs")
                throw new InvalidOperationException("Keys must use the original user authority at ts-auth's project boundary.");
            Requests++;
            Methods.Add(request.Method);
            Query = request.RequestUri.Query;
            Path = request.RequestUri.AbsolutePath;
            Body = request.Content is null ? null : await request.Content.ReadAsStringAsync(cancellationToken);
            if (Deleted) return Error(HttpStatusCode.NotFound, "api_key_not_found");
            if (request.Method == HttpMethod.Delete)
            {
                // "committed": the delete lands but its answer is lost; "refused": nothing changes.
                Deleted = Failure != "refused";
                return Failure is "committed" or "refused" ? Error(HttpStatusCode.ServiceUnavailable, "temporarily_unavailable") : new(HttpStatusCode.NoContent);
            }
            var rotate = Path.EndsWith("/rotate", StringComparison.Ordinal);
            string? expiresAt = null;
            if (request.Method == HttpMethod.Post && !rotate)
            {
                using var created = JsonDocument.Parse(Body!);
                // Echo the requested expiry, or a different one for "expiry".
                if (created.RootElement.TryGetProperty("expires_at", out var requested) && requested.ValueKind == JsonValueKind.String)
                    expiresAt = Failure == "expiry" ? requested.GetDateTimeOffset().AddSeconds(1).ToString("O") : requested.GetString();
            }
            if (rotate && Failure == "pending") return Error(HttpStatusCode.Conflict, "rotation_pending");
            if (Failure == "bad-cidr") return new(HttpStatusCode.BadRequest) { Content = new StringContent(JsonSerializer.Serialize(new { error = new { code = "invalid_request", message = "allowed_cidrs[0] is not a valid CIDR" } }), Encoding.UTF8, "application/json") };
            // Echo the requested allowlist the way ts-auth canonicalizes it: a bare IP becomes a /32 or /128.
            using var sent = Body is { Length: > 0 } ? JsonDocument.Parse(Body) : null;
            var cidrs = sent is not null && sent.RootElement.TryGetProperty("allowed_cidrs", out var listed)
                ? listed.EnumerateArray().Select(item => item.GetString()!).Select(cidr => cidr.Contains('/', StringComparison.Ordinal) ? cidr : cidr + (cidr.Contains(':', StringComparison.Ordinal) ? "/128" : "/32")).ToArray() : [];
            if (!Path.Contains("/api-keys", StringComparison.Ordinal))
                return new(HttpStatusCode.OK) { Content = new StringContent(JsonSerializer.Serialize(new { id = ProjectId, organization_id = OrganizationId, allowed_cidrs = cidrs }), Encoding.UTF8, "application/json") };
            var key = new
            {
                id = KeyId,
                name = "Synthetic",
                organization_id = OrganizationId,
                project_id = Failure == "foreign" ? "01JA0000000000000000000005" : ProjectId,
                database = "app",
                scopes = new[] { "documents:read", "documents:write" },
                created_by = CreatedBy,
                created_at = "2026-01-01T00:00:00+00:00",
                expires_at = expiresAt,
                previous_secret_expires_at = (string?)null,
                last_used_at = LastUsedAt,
                allowed_cidrs = cidrs
            };
            var response = request.Method == HttpMethod.Post && !Path.EndsWith("/allowed-cidrs", StringComparison.Ordinal)
                ? JsonSerializer.Serialize(new { api_key = key, key = KeyId + "_synthetic-secret" })
                : request.RequestUri.AbsolutePath == root ? JsonSerializer.Serialize(new { items = new[] { key }, next = Failure == "repeated" ? "opaque+/cursor=" : null })
                : JsonSerializer.Serialize(key);
            return new(request.Method == HttpMethod.Post && !rotate ? HttpStatusCode.Created : HttpStatusCode.OK)
            { Content = new StringContent(response, Encoding.UTF8, "application/json") };
        }
    }
}
