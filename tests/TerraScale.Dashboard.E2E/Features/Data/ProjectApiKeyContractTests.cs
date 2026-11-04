using System.Net;
using TerraScale.Dashboard.Platform.TsAuth;
using System.Text;
using System.Text.Json;
using TUnit.Assertions.Enums;
using TerraScale.Dashboard.Features.ApiKeys;

namespace TerraScale.Dashboard.E2E.Features.Data
{
    public sealed class ProjectApiKeyContractTests
    {
        private const string OrganizationId = "01JA0000000000000000000002";
        private const string ProjectId = "01JA0000000000000000000001";
        private const string KeyId = "01JA0000000000000000000003";
        private const string DatabaseId = "01JA0000000000000000000004";
        private const string CreatorId = "01JA0000000000000000000009";

        [Test]
        public async Task IssuanceUsesOriginalUserBearerAndCanonicalRestrictionsWithoutPersistingSecret()
        {
            using var wire = new Wire();
            var context = Context();
            var service = new ApiKeyService(wire, TimeProvider.System, new TsAuthClient(wire.CreateClient("platform")));
            var created = await service.CreateAsync(context, "api-key", "Synthetic", DatabaseId,
                ["documents:read", "documents:write"], 0, [], CancellationToken.None, "synthetic-proof");
            using var request = JsonDocument.Parse(wire.Body!);
            await Assert.That(request.RootElement.TryGetProperty("expires_at", out var noExpiry) && noExpiry.ValueKind != JsonValueKind.Null).IsFalse();
            await Assert.That(request.RootElement.GetProperty("allowed_cidrs").GetArrayLength()).IsEqualTo(0);
            await Assert.That(created.Key.AllowedCidrs).IsEquivalentTo(Array.Empty<string>());
            await Assert.That(request.RootElement.GetProperty("database").GetString()).IsEqualTo(DatabaseId);
            await Assert.That(request.RootElement.GetProperty("scopes").EnumerateArray().Select(item => item.GetString()!))
                .IsEquivalentTo(new[] { "documents:read", "documents:write" });
            await Assert.That(created.Secret).IsEqualTo(KeyId + "_synthetic-secret");
            await Assert.That(created.Key.LastFour).IsEqualTo("");
            await Assert.That(ApiKeyService.Metadata(created.Key).DatabaseName).IsEqualTo("app");
            await Assert.That(ApiKeyService.Metadata(created.Key).DatabaseId).IsEqualTo(DatabaseId);
            var listed = await service.ListAsync(context, CancellationToken.None);
            await Assert.That(wire.Query).IsEqualTo("?limit=100");
            await Assert.That(listed.Single().Id).IsEqualTo(KeyId);
            await Assert.That(JsonSerializer.Serialize(listed).Contains(created.Secret, StringComparison.Ordinal)).IsFalse();
            await Assert.That(wire.Requests).IsEqualTo(2);
        }

        [Test]
        public async Task RestrictionSurvivesMissingLocalDatabaseAndRevokeVerifiesKeyBeforeDelete()
        {
            using var wire = new Wire();
            var context = Context();
            context.Project!.Databases.Clear();
            var service = new ApiKeyService(wire, TimeProvider.System, new TsAuthClient(wire.CreateClient("platform")));
            var key = (await service.ListAsync(context, CancellationToken.None)).Single();
            await Assert.That(key.DatabaseId).IsEqualTo(DatabaseId);
            await Assert.That(ApiKeyService.CredentialMetadata(key).DatabaseName).IsEqualTo("Deleted database");
            await service.RevokeAsync(context, KeyId, CancellationToken.None);
            await Assert.That(wire.Methods).IsEquivalentTo(new[] { HttpMethod.Get, HttpMethod.Get, HttpMethod.Delete }, CollectionOrdering.Matching);
        }

        [Test]
        public async Task RotateVerifiesTheKeyAndSendsTheGracePeriodForTheOldSecret()
        {
            using var wire = new Wire();
            var rotated = await new ApiKeyService(wire, TimeProvider.System, new TsAuthClient(wire.CreateClient("platform"))).RotateAsync(Context(), KeyId, 3600, CancellationToken.None, "synthetic-proof");
            await Assert.That(wire.Methods).IsEquivalentTo(new[] { HttpMethod.Get, HttpMethod.Post }, CollectionOrdering.Matching);
            await Assert.That(wire.Path).IsEqualTo($"/v1/terrascale/organizations/{OrganizationId}/projects/{ProjectId}/api-keys/{KeyId}/rotate");
            using var request = JsonDocument.Parse(wire.Body!);
            await Assert.That(request.RootElement.GetProperty("previous_secret_ttl").GetInt32()).IsEqualTo(3600);
            await Assert.That(rotated.Key.Id).IsEqualTo(KeyId);
            await Assert.That(rotated.Secret).IsEqualTo(KeyId + "_synthetic-secret");
        }

        [Test]
        [Arguments("foreign")]
        public async Task ListingRefusesForeignIdentity(string failure)
        {
            using var wire = new Wire { Failure = failure };
            HttpRequestException? rejected = null;
            try { await new ApiKeyService(wire, TimeProvider.System, new TsAuthClient(wire.CreateClient("platform"))).ListAsync(Context(), CancellationToken.None); }
            catch (HttpRequestException error) { rejected = error; }
            await Assert.That(rejected).IsNotNull();
            await Assert.That(wire.Requests).IsEqualTo(1);
            await Assert.That(wire.Methods.All(method => method == HttpMethod.Get)).IsTrue();
        }

        [Test]
        public async Task IssuanceSendsAllowedCidrsAndKeepsTsAuthsCanonicalList()
        {
            using var wire = new Wire();
            var created = await new ApiKeyService(wire, TimeProvider.System, new TsAuthClient(wire.CreateClient("platform"))).CreateAsync(Context(), "api-key", "Synthetic", DatabaseId,
                ["documents:read", "documents:write"], 0, ApiKeyService.ParseCidrs("203.0.113.0/24\n198.51.100.7, 2001:db8::/48"), CancellationToken.None, "synthetic-proof");
            using var request = JsonDocument.Parse(wire.Body!);
            await Assert.That(request.RootElement.GetProperty("allowed_cidrs").EnumerateArray().Select(item => item.GetString()!))
                .IsEquivalentTo(new[] { "203.0.113.0/24", "198.51.100.7", "2001:db8::/48" }, CollectionOrdering.Matching);
            await Assert.That(created.Key.AllowedCidrs!).IsEquivalentTo(new[] { "203.0.113.0/24", "198.51.100.7/32", "2001:db8::/48" }, CollectionOrdering.Matching);
            await Assert.That(ApiKeyService.CredentialMetadata(created.Key).AllowedCidrs!).IsEquivalentTo(created.Key.AllowedCidrs!);
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
            public override DateTimeOffset GetUtcNow()
            {
                return now;
            }
        }

        private sealed class Wire : HttpMessageHandler, IHttpClientFactory
        {
            public string Failure { get; init; } = "";
            public string CreatedBy { get; init; } = "user";
            public string? LastUsedAt { get; init; }
            // Replaces the allowlist echoed by writes, as a provider that answers a different restriction would.
            public string[]? EchoCidrs { get; init; }
            public string Database { get; init; } = DatabaseId;
            // Multi-page listings: Pages issuer pages of 2 keys each, the last one repeating a key and its cursor once.
            public int Pages { get; init; }
            public int Requests { get; private set; }
            public string? Body { get; private set; }
            public string? Query { get; private set; }
            public string? Path { get; private set; }
            public List<HttpMethod> Methods { get; } = [];
            private bool Deleted { get; set; }
            public string[] AllIds =>
            [
                KeyId,
                "01JA00000000000000000000A0",
                "01JA0000000000000000000A00",
                "01JA000000000000000000A000",
                "01JA00000000000000000A0000",
            ];
            private static HttpResponseMessage Error(HttpStatusCode status, string code)
            {
                return new(status) { Content = new StringContent(JsonSerializer.Serialize(new { error = new { code, message = "Synthetic failure" } }), Encoding.UTF8, "application/json") };
            }

            public HttpClient CreateClient(string name)
            {
                return new(this, false);
            }

            private string ListResponse()
            {
                if (Pages == 0)
                {
                    // "repeated": the issuer answers the same cursor it was given; the traversal must end, not loop.
                    if (Failure == "repeated")
                    {
                        return JsonSerializer.Serialize(new { items = new[] { Key(KeyId) }, next = "opaque+/cursor=" });
                    }

                    return JsonSerializer.Serialize(new { items = new[] { Key(KeyId) }, next = (string?)null });
                }

                // Three bounded pages over five keys: 2 + 2 (repeating the first key) + 1.
                var after = Microsoft.AspNetCore.WebUtilities.QueryHelpers.ParseQuery(Query).GetValueOrDefault("after").ToString();
                var page = after switch
                {
                    "" => (Ids: new[] { KeyId, "01JA00000000000000000000A0" }, Next: "page-2" as string),
                    "page-2" => (Ids: new[] { "01JA0000000000000000000A00", "01JA0000000000000000000A00" }, Next: "page-3"),
                    _ => (Ids: new[] { "01JA000000000000000000A000", "01JA00000000000000000A0000" }, Next: null),
                };
                return JsonSerializer.Serialize(new { items = page.Ids.Select(Key), next = page.Next });
            }

            private object Key(string id)
            {
                return new
                {
                    id,
                    name = "Synthetic " + id[^1],
                    organization_id = OrganizationId,
                    project_id = Failure == "foreign" ? "01JA0000000000000000000005" : ProjectId,
                    database = Database,
                    scopes = new[] { "documents:read", "documents:write" },
                    created_by = CreatedBy,
                    created_at = "2026-01-01T00:00:00+00:00",
                    expires_at = (string?)null,
                    previous_secret_expires_at = (string?)null,
                    last_used_at = LastUsedAt,
                    allowed_cidrs = Array.Empty<string>()
                };
            }

            protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            {
                var root = $"/v1/terrascale/organizations/{OrganizationId}/projects/{ProjectId}/api-keys";
                if (request.RequestUri!.Host != "auth.terrascale.tech" || request.Headers.Authorization?.Parameter != "original-user-token" ||
                    request.RequestUri.AbsolutePath is not { } path || path != root && path != root + "/" + KeyId && path != root + "/" + KeyId + "/rotate" &&
                    path != root + "/" + KeyId + "/allowed-cidrs" && path != root[..^"/api-keys".Length] && path != root[..^"/api-keys".Length] + "/allowed-cidrs")
                {
                    throw new InvalidOperationException("Keys must use the original user authority at ts-auth's project boundary.");
                }

                if (request.Method == HttpMethod.Post && request.Headers.GetValues("X-TerraScale-Step-Up-Proof").Single() != "synthetic-proof")
                {
                    throw new InvalidOperationException("Protected key mutations must carry their backend step-up proof.");
                }

                Requests++;
                Methods.Add(request.Method);
                Query = request.RequestUri.Query;
                Path = request.RequestUri.AbsolutePath;
                Body = request.Content is null ? null : await request.Content.ReadAsStringAsync(cancellationToken);
                if (Deleted)
                {
                    return Error(HttpStatusCode.NotFound, "api_key_not_found");
                }

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
                    {
                        expiresAt = Failure == "expiry" ? requested.GetDateTimeOffset().AddSeconds(1).ToString("O") : requested.GetString();
                    }
                }
                if (rotate && Failure == "pending")
                {
                    return Error(HttpStatusCode.Conflict, "rotation_pending");
                }

                if (Failure == "bad-cidr")
                {
                    return new(HttpStatusCode.BadRequest) { Content = new StringContent(JsonSerializer.Serialize(new { error = new { code = "invalid_request", message = "allowed_cidrs[0] is not a valid CIDR" } }), Encoding.UTF8, "application/json") };
                }
                // Echo the requested allowlist the way ts-auth canonicalizes it: a bare IP becomes a /32 or /128.
                using var sent = Body is { Length: > 0 } ? JsonDocument.Parse(Body) : null;
                var cidrs = sent is not null && sent.RootElement.TryGetProperty("allowed_cidrs", out var listed)
                    ? listed.EnumerateArray().Select(item => item.GetString()!).Select(cidr => cidr.Contains('/', StringComparison.Ordinal) ? cidr : cidr + (cidr.Contains(':', StringComparison.Ordinal) ? "/128" : "/32")).ToArray() : [];
                if (request.Method != HttpMethod.Get && EchoCidrs is not null)
                {
                    cidrs = EchoCidrs;
                }

                if (!Path.Contains("/api-keys", StringComparison.Ordinal))
                {
                    return new(HttpStatusCode.OK) { Content = new StringContent(JsonSerializer.Serialize(new { id = ProjectId, organization_id = OrganizationId, allowed_cidrs = cidrs }), Encoding.UTF8, "application/json") };
                }

                var key = new
                {
                    id = KeyId,
                    name = "Synthetic",
                    organization_id = OrganizationId,
                    project_id = Failure == "foreign" ? "01JA0000000000000000000005" : ProjectId,
                    database = Database,
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
                    : request.RequestUri.AbsolutePath == root ? ListResponse()
                    : JsonSerializer.Serialize(key);
                return new(request.Method == HttpMethod.Post && !rotate ? HttpStatusCode.Created : HttpStatusCode.OK)
                { Content = new StringContent(response, Encoding.UTF8, "application/json") };
            }
        }
    }
}
