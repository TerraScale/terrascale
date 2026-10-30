using System.Net;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.AspNetCore.Http;
using TerraScale.Dashboard.E2E.Features.Databases;

namespace TerraScale.Dashboard.E2E.Features.Backups
{
    [ClassDataSource<DashboardRootFixture>]
    public sealed class BackupConfirmationJourneyTests(DashboardRootFixture fixture)
    {
        [Test]
        public async Task GenuineRawRestoreAndFullConfirmationPreserveActualPlacementWhileSchedulingIsPaused()
        {
            var organizationId = "";
            var projectId = "";
            var sourceId = "";
            var placeOnConfirmation = false;
            var rawNullObserved = false;
            var confirmedRequests = 0;
            var pending = 0;
            var writeBearer = "";
            HttpClient? producer = null;
            await using var scenario = await DatabaseSdkScenario.StartAsync(fixture.Root, producerExtension: async http =>
            {
                if (RestoreRequest(http, projectId)) { writeBearer = http.Request.Headers.Authorization.ToString(); Volatile.Write(ref pending, 1); return null; }
                var name = ConfirmationName(http, projectId);
                if (name is null || Interlocked.Exchange(ref pending, 0) == 0)
                {
                    return null;
                }

                DemandScope(http);
                Interlocked.Increment(ref confirmedRequests);
                if (Volatile.Read(ref placeOnConfirmation))
                {
                    Volatile.Write(ref placeOnConfirmation, false);
                    var stored = await StoredAsync(producer!, name);
                    rawNullObserved = stored.GetProperty("replicaRegions").ValueKind == JsonValueKind.Null;
                    using var request = ScopedRequest(http, HttpMethod.Patch, CanonicalDatabasePath(projectId, name), writeBearer);
                    request.Content = JsonContent.Create(new { replicaRegions = new[] { "sfo" } });
                    using var changed = await producer!.SendAsync(request, http.RequestAborted);
                    changed.EnsureSuccessStatusCode();
                }
                return null;
            });
            using var client = scenario.CreateClient();
            using var replica = scenario.CreateClient(replica: true);
            using (producer = scenario.CreateProviderClient())
            {
                var account = await DatabaseSdkJourneyTests.NativeWorkspaceAsync(client);
                using var login = await HttpJourney.LoginAsync(replica, account.Email, account.Password);
                await Assert.That(login.StatusCode).IsEqualTo(HttpStatusCode.SeeOther);
                var identity = await ReadAsync(client, "/api/v1/auth/me");
                organizationId = identity.GetProperty("organizationId").GetString()!;
                projectId = identity.GetProperty("projectId").GetString()!;
                await ControlAsync(producer, new { command = "plan", organizationId, value = OrganizationRecord.StarterPlanId });
                var csrf = await HttpJourney.CsrfAsync(client, "/databases");
                sourceId = await CreateAsync(client, csrf, "paused-source");
                await SeedAsync(client, csrf, sourceId);
                var backupId = await BackupAsync(client, csrf, sourceId);
                var backupHtml = System.Net.WebUtility.HtmlDecode(await client.GetStringAsync("/databases/" + sourceId + "/backups"));
                var nameInput = System.Text.RegularExpressions.Regex.Match(backupHtml, "<input[^>]+name=\"name\"[^>]*>").Value;
                await Assert.That(nameInput).Contains("pattern=\"" + DatabaseService.NameInputPattern + "\"");
                await Assert.That(nameInput.Contains("required", StringComparison.Ordinal)).IsFalse();
                await Assert.That(backupHtml).Contains(DatabaseService.NameHint).And.Contains("Leave blank to automatically choose");
                await WriteAsync(client, csrf, sourceId, "replace", 2);
                using (var paused = await HttpJourney.PostFormAsync(client, "/api/v1/dashboard/settings/backup-policy", new()
                {
                    ["_csrf"] = csrf,
                    ["databaseId"] = sourceId,
                    ["fullBackupCron"] = "0 2 * * *",
                    ["incrementalBackupCron"] = "",
                    ["retentionDays"] = "7",
                    ["disabled"] = "true"
                }))
                {
                    await Assert.That(paused.StatusCode).IsEqualTo(HttpStatusCode.SeeOther);
                    await Assert.That(paused.Headers.Location?.OriginalString).IsEqualTo("/");
                }
                var policy = (await ReadAsync(replica, "/api/v1/dashboard/backups?databaseId=" + sourceId)).GetProperty("policy");
                await Assert.That(policy.GetProperty("disabled").GetBoolean()).IsTrue();
                Volatile.Write(ref placeOnConfirmation, true);
                using (var restored = await HttpJourney.PostFormAsync(client, "/api/v1/dashboard/settings/restore", new()
                { ["_csrf"] = csrf, ["databaseId"] = sourceId, ["backupId"] = backupId }))
                {
                    await Assert.That(restored.StatusCode).IsEqualTo(HttpStatusCode.SeeOther).Because(await EvidenceAsync(restored, producer));
                    await Assert.That(restored.Headers.Location?.OriginalString).IsEqualTo("/");
                }
                var rows = await ManagementListJourney.ReadListAllAsync(replica, "/api/v1/dashboard/databases");
                var target = rows.EnumerateArray().Single(row => row.GetProperty("name").GetString() == "paused-source-restore");
                var restoredId = target.GetProperty("id").GetString()!;
                await Assert.That(rawNullObserved).IsTrue().Because("The real restore201 catalog body permits null replicas before the managed GET normalizes them.");
                await Assert.That(Volatile.Read(ref confirmedRequests)).IsEqualTo(1).Because("The full managed placement confirmation crosses the actual HTTP boundary.");
                await Assert.That(target.GetProperty("region").GetString()).IsEqualTo("GRU");
                await Assert.That(Regions(target)).IsEqualTo("GRU,SFO");
                await Assert.That(target.GetProperty("branchedFrom").GetString()).IsEqualTo(sourceId);
                await Assert.That(await ScoreAsync(replica, restoredId)).IsEqualTo(1);
                await Assert.That(await ScoreAsync(client, sourceId)).IsEqualTo(2);
                await DeleteAsync(client, csrf, restoredId, "paused-source-restore");
                await DeleteAsync(client, csrf, sourceId, "paused-source");
                var observed = await ReadAsync(producer, "/__test/observations");
                await Assert.That(observed.GetProperty("backupRestores").GetInt32()).IsEqualTo(1);
                await Assert.That(observed.GetProperty("databases").GetArrayLength()).IsEqualTo(0);
                await Assert.That(observed.GetProperty("failures").GetArrayLength()).IsEqualTo(0);
            }
        }

        [Test]
        [Arguments("id")]
        [Arguments("project")]
        [Arguments("name")]
        [Arguments("source")]
        [Arguments("home")]
        [Arguments("missing-home")]
        [Arguments("missing-replicas")]
        [Arguments("invalid-replica")]
        [Arguments("duplicate-replica")]
        public Task InvalidFullConfirmationNeverPublishesAClaimedRestore(string failure)
        {
            return RefusedAndRetryAsync(failure, interruptedMetadata: false);
        }

        [Test]
        public Task InterruptedMetadataCannotRecoverAnUnprovenSnapshotByName()
        {
            return RefusedAndRetryAsync("", interruptedMetadata: true);
        }

        private async Task RefusedAndRetryAsync(string failure, bool interruptedMetadata)
        {
            var organizationId = "";
            var projectId = "";
            var sourceId = "";
            var corrupt = false;
            var writeBearer = "";
            HttpClient? producer = null;
            await using var scenario = await DatabaseSdkScenario.StartAsync(fixture.Root, producerExtension: async http =>
            {
                if (RestoreRequest(http, projectId)) { writeBearer = http.Request.Headers.Authorization.ToString(); return null; }
                var name = ConfirmationName(http, projectId);
                if (name is null || !Volatile.Read(ref corrupt))
                {
                    return null;
                }

                DemandScope(http);
                var stored = await StoredAsync(producer!, name);
                var row = JsonNode.Parse(stored.GetRawText())!.AsObject();
                row["replicaRegions"] = new JsonArray();
                switch (failure)
                {
                    case "id": row["id"] = "01K78000000000000000000000"; break;
                    case "project": row["projectId"] = "01K78000000000000000000001"; break;
                    case "name": row["name"] = "another-restored-name"; break;
                    case "source": row["branchedFrom"] = "01K78000000000000000000002"; break;
                    case "home": row["homeRegion"] = "sfo"; break;
                    case "missing-home": row.Remove("homeRegion"); break;
                    case "missing-replicas": row.Remove("replicaRegions"); break;
                    case "invalid-replica": row["replicaRegions"] = new JsonArray("Unpublished_Region"); break;
                    case "duplicate-replica": row["replicaRegions"] = new JsonArray("sfo", "sfo"); break;
                }
                return Results.Json(row);
            });
            using var client = scenario.CreateClient();
            using var replica = scenario.CreateClient(replica: true);
            using (producer = scenario.CreateProviderClient())
            {
                var account = await DatabaseSdkJourneyTests.NativeWorkspaceAsync(client);
                using var login = await HttpJourney.LoginAsync(replica, account.Email, account.Password);
                await Assert.That(login.StatusCode).IsEqualTo(HttpStatusCode.SeeOther);
                var identity = await ReadAsync(client, "/api/v1/auth/me");
                organizationId = identity.GetProperty("organizationId").GetString()!;
                projectId = identity.GetProperty("projectId").GetString()!;
                await ControlAsync(producer, new { command = "plan", organizationId, value = OrganizationRecord.StarterPlanId });
                var csrf = await HttpJourney.CsrfAsync(client, "/databases");
                var replicaCsrf = await HttpJourney.CsrfAsync(replica, "/databases");
                sourceId = await CreateAsync(client, csrf, "confirmation-source");
                await SeedAsync(client, csrf, sourceId);
                var backupId = await BackupAsync(client, csrf, sourceId);
                await WriteAsync(client, csrf, sourceId, "replace", 2);
                const string restoredName = "confirmation-restored";
                if (interruptedMetadata)
                {
                    await ControlAsync(producer, new { command = "fail-database-metadata", organizationId, projectId, name = restoredName });
                }
                else
                {
                    Volatile.Write(ref corrupt, true);
                }

                using (var refused = await RestoreAsync(client, csrf, sourceId, backupId, restoredName))
                {
                    await Assert.That(refused.StatusCode).IsEqualTo(HttpStatusCode.ServiceUnavailable).Because(await EvidenceAsync(refused, producer));
                }

                Volatile.Write(ref corrupt, false);
                var observed = await ReadAsync(producer, "/__test/observations");
                var actual = observed.GetProperty("databases").EnumerateArray().Single(row => row.GetProperty("name").GetString() == restoredName);
                var orphanId = actual.GetProperty("id").GetString()!;
                await Assert.That(observed.GetProperty("backupRestores").GetInt32()).IsEqualTo(1);
                // The refused answer is never recorded, but TerraBase owns databases: the listing records the orphan it holds.
                await Assert.That((await ManagementListJourney.ReadListAllAsync(replica, "/api/v1/dashboard/databases")).EnumerateArray().Select(row => row.GetProperty("id").GetString()!).Order(StringComparer.Ordinal).ToArray())
                    .IsEquivalentTo(new[] { sourceId, orphanId });
                if (interruptedMetadata)
                {
                    await Assert.That(observed.GetProperty("failedDatabaseMetadataWrites").GetInt32()).IsEqualTo(1);
                }

                using (var retry = await RestoreAsync(replica, replicaCsrf, sourceId, backupId, restoredName))
                {
                    await Assert.That(retry.StatusCode).IsEqualTo(HttpStatusCode.Conflict).Because(await EvidenceAsync(retry, producer));
                }

                await Assert.That((await ReadAsync(producer, "/__test/observations")).GetProperty("backupRestores").GetInt32()).IsEqualTo(1)
                    .Because("An existing name does not prove which backup was restored: the producer exposes only source-database provenance.");
                await Assert.That((await ManagementListJourney.ReadListAllAsync(client, "/api/v1/dashboard/databases")).GetArrayLength()).IsEqualTo(2);
                // Cleanup is an explicit authorized HTTP request to the exact
                // unprojected producer target, never an invented successful repair.
                using (var request = new HttpRequestMessage(HttpMethod.Delete, CanonicalDatabasePath(projectId, restoredName)))
                {
                    request.Headers.Host = "gru.api.terrascale.tech";
                    request.Headers.Add("Authorization", writeBearer);
                    using var removed = await producer.SendAsync(request);
                    await Assert.That(removed.StatusCode).IsEqualTo(HttpStatusCode.NoContent).Because(await EvidenceAsync(removed, producer));
                }
                // The next listing drops the record of the database TerraBase no longer has.
                await Assert.That((await ManagementListJourney.ReadListAllAsync(replica, "/api/v1/dashboard/databases")).GetArrayLength()).IsEqualTo(1);
                string recoveredId;
                using (var retry = await RestoreAsync(replica, replicaCsrf, sourceId, backupId, restoredName))
                {
                    await Assert.That(retry.StatusCode).IsEqualTo(HttpStatusCode.Created).Because(await EvidenceAsync(retry, producer));
                    recoveredId = (await BodyAsync(retry)).GetProperty("id").GetString()!;
                }
                await Assert.That(recoveredId == orphanId).IsFalse();
                await Assert.That(await ScoreAsync(client, recoveredId)).IsEqualTo(1);
                await Assert.That(await ScoreAsync(replica, sourceId)).IsEqualTo(2);
                await DeleteAsync(replica, replicaCsrf, recoveredId, restoredName);
                await DeleteAsync(client, csrf, sourceId, "confirmation-source");
                observed = await ReadAsync(producer, "/__test/observations");
                await Assert.That(observed.GetProperty("backupRestores").GetInt32()).IsEqualTo(2);
                await Assert.That(observed.GetProperty("databases").GetArrayLength()).IsEqualTo(0);
                await Assert.That(observed.GetProperty("failures").GetArrayLength()).IsEqualTo(0);
            }
        }

        [Test]
        public async Task NativePolicyAliasesUseGenuineStandardCronValidationAndProviderDefaults()
        {
            await using var scenario = await DatabaseSdkScenario.StartAsync(fixture.Root);
            using var client = scenario.CreateClient();
            using var producer = scenario.CreateProviderClient();
            await DatabaseSdkJourneyTests.NativeWorkspaceAsync(client);
            var csrf = await HttpJourney.CsrfAsync(client, "/databases");
            var sourceId = await CreateAsync(client, csrf, "cron-source");
            var policy = (await ReadAsync(client, "/api/v1/dashboard/backups?databaseId=" + sourceId)).GetProperty("policy");
            await Assert.That(policy.GetProperty("fullBackupCron").GetString()).IsEqualTo("0 2 * * *");
            await Assert.That(policy.GetProperty("retentionDays").GetInt32()).IsEqualTo(7);
            await Assert.That(policy.GetProperty("disabled").GetBoolean()).IsFalse();
            await Assert.That(policy.TryGetProperty("updatedAt", out _)).IsFalse().Because("The genuine default is unsaved, so it has no invented update time.");
            foreach (var cron in new[] { "0 3 * * *", "*/15 9-17 * JAN MON-FRI", "0 2 L * *", "@daily", "" })
            {
                using var response = await HttpJourney.PostFormAsync(client, "/api/v1/dashboard/settings/backup-policy", new()
                {
                    ["_csrf"] = csrf,
                    ["databaseId"] = sourceId,
                    ["fullBackupCron"] = cron,
                    ["incrementalBackupCron"] = "",
                    ["retentionDays"] = "35",
                    ["disabled"] = "false"
                });
                await Assert.That(response.StatusCode).IsEqualTo(HttpStatusCode.SeeOther).Because(await EvidenceAsync(response, producer));
                await Assert.That(response.Headers.Location?.OriginalString).IsEqualTo("/");
                policy = (await ReadAsync(client, "/api/v1/dashboard/backups?databaseId=" + sourceId)).GetProperty("policy");
                await Assert.That(policy.GetProperty("fullBackupCron").GetString()).IsEqualTo(cron);
                await Assert.That(policy.GetProperty("retentionDays").GetInt32()).IsEqualTo(35);
                await Assert.That(policy.TryGetProperty("updatedAt", out _)).IsTrue();
            }
            var writes = (await ReadAsync(producer, "/__test/observations")).GetProperty("policyWrites").GetInt32();
            foreach (var cron in new[] { "99 2 * * *", "0 24 * * *", "0 2 32 * *", "0 2 * 13 *", "0 0 2 * * *", "@not_a_schedule", "no full backup schedule here" })
            {
                using var refused = await HttpJourney.PostFormAsync(client, "/api/v1/dashboard/settings/backup-policy", new()
                {
                    ["_csrf"] = csrf,
                    ["databaseId"] = sourceId,
                    ["fullBackupCron"] = cron,
                    ["incrementalBackupCron"] = "",
                    ["retentionDays"] = "7"
                });
                await Assert.That(refused.StatusCode).IsEqualTo(HttpStatusCode.UnprocessableEntity).Because(await EvidenceAsync(refused, producer));
            }
            await Assert.That((await ReadAsync(producer, "/__test/observations")).GetProperty("policyWrites").GetInt32()).IsEqualTo(writes);
            await DeleteAsync(client, csrf, sourceId, "cron-source");
        }

        // The dashboard's restore POST carries an action-scoped databases:write project token; the test reuses it for cleanup.
        private static bool RestoreRequest(HttpContext http, string projectId)
        {
            return http.Request.Method == "POST" && projectId.Length > 0 && (http.Request.Path.Value ?? "").StartsWith(CanonicalDatabasePath(projectId, ""), StringComparison.Ordinal) &&
            (http.Request.Path.Value ?? "").EndsWith("/restore", StringComparison.Ordinal);
        }

        private static string? ConfirmationName(HttpContext http, string projectId)
        {
            var prefix = CanonicalDatabasePath(projectId, "");
            var path = http.Request.Path.Value ?? "";
            if (http.Request.Method != "GET" || projectId.Length == 0 || !path.StartsWith(prefix, StringComparison.Ordinal))
            {
                return null;
            }

            var name = path[prefix.Length..];
            return name.Length == 0 || name.Contains('/') ? null : Uri.UnescapeDataString(name);
        }
        private static void DemandScope(HttpContext http)
        {
            if (http.Request.Host.Host != "gru.api.terrascale.tech" || http.Request.Headers[TerraScale.TerraBase.Contracts.TerraBaseRpc.PeerCredentialHeader] != TestIdentity.PeerSecret ||
                !http.Request.Headers.Authorization.ToString().StartsWith("Bearer ", StringComparison.Ordinal))
            {
                throw new InvalidOperationException("Restore confirmation must cross the home-region RPC listener with an admitted project bearer.");
            }
        }
        private static HttpRequestMessage ScopedRequest(HttpContext http, HttpMethod method, string path, string bearer)
        {
            var request = new HttpRequestMessage(method, path);
            request.Headers.Host = http.Request.Host.Host;
            request.Headers.Add("Authorization", bearer);
            return request;
        }
        private static async Task<JsonElement> StoredAsync(HttpClient producer, string name)
        {
            using var response = await producer.GetAsync("/__test/observations");
            response.EnsureSuccessStatusCode();
            using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
            return body.RootElement.GetProperty("databases").EnumerateArray().Single(row => row.GetProperty("name").GetString() == name).Clone();
        }
        private static string CanonicalDatabasePath(string projectId, string name)
        {
            return "/v1/projects/" + projectId + "/databases/" + Uri.EscapeDataString(name);
        }

        private static Task<HttpResponseMessage> RestoreAsync(HttpClient client, string csrf, string id, string backupId, string name)
        {
            return HttpJourney.PostFormAsync(client, "/api/v1/dashboard/backups/restore", new()
            { ["_csrf"] = csrf, ["databaseId"] = id, ["backupId"] = backupId, ["name"] = name });
        }

        private static async Task<string> CreateAsync(HttpClient client, string csrf, string name)
        {
            using var response = await HttpJourney.PostFormAsync(client, "/api/v1/dashboard/databases", new()
            { ["_csrf"] = csrf, ["name"] = name, ["region"] = "GRU" });
            await Assert.That(response.StatusCode).IsEqualTo(HttpStatusCode.Created).Because(await response.Content.ReadAsStringAsync());
            return (await BodyAsync(response)).GetProperty("id").GetString()!;
        }
        private static async Task SeedAsync(HttpClient client, string csrf, string id)
        {
            using var collection = await HttpJourney.PostFormAsync(client, "/api/v1/dashboard/data/collections", new()
            { ["_csrf"] = csrf, ["databaseId"] = id, ["name"] = "users" });
            await Assert.That(collection.StatusCode).IsEqualTo(HttpStatusCode.Created);
            await WriteAsync(client, csrf, id, "create", 1);
        }
        private static async Task WriteAsync(HttpClient client, string csrf, string id, string operation, int score)
        {
            using var response = await HttpJourney.PostFormAsync(client, "/api/v1/dashboard/data/documents", new()
            { ["_csrf"] = csrf, ["databaseId"] = id, ["collection"] = "users", ["id"] = "01K78D0C000000000000000011", ["operation"] = operation, ["document"] = JsonSerializer.Serialize(new { score }) });
            await Assert.That(response.StatusCode).IsEqualTo(HttpStatusCode.OK).Because(await response.Content.ReadAsStringAsync());
        }
        private static async Task<string> BackupAsync(HttpClient client, string csrf, string id)
        {
            using var response = await HttpJourney.PostFormAsync(client, "/api/v1/dashboard/backups", new() { ["_csrf"] = csrf, ["databaseId"] = id });
            await Assert.That(response.StatusCode).IsEqualTo(HttpStatusCode.Created).Because(await response.Content.ReadAsStringAsync());
            return (await BodyAsync(response)).GetProperty("id").GetString()!;
        }
        private static async Task<int> ScoreAsync(HttpClient client, string id)
        {
            return (await ReadAsync(client, "/api/v1/dashboard/data/documents?databaseId=" + id + "&collection=users")).GetProperty("documents")[0].GetProperty("value").GetProperty("score").GetInt32();
        }

        private static async Task DeleteAsync(HttpClient client, string csrf, string id, string name)
        {
            using var response = await HttpJourney.PostFormAsync(client, "/api/v1/dashboard/databases/delete", new()
            { ["_csrf"] = csrf, ["databaseId"] = id, ["confirmName"] = name });
            await Assert.That(response.StatusCode).IsEqualTo(HttpStatusCode.NoContent).Because(await response.Content.ReadAsStringAsync());
        }
        private static async Task<JsonElement> ReadAsync(HttpClient client, string path)
        { using var response = await client.GetAsync(path); await Assert.That(response.StatusCode).IsEqualTo(HttpStatusCode.OK).Because(await response.Content.ReadAsStringAsync()); return await BodyAsync(response); }
        private static async Task<JsonElement> BodyAsync(HttpResponseMessage response)
        { using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync()); return body.RootElement.Clone(); }
        private static async Task ControlAsync(HttpClient client, object value)
        { using var response = await client.PostAsJsonAsync("/__test/control", value); await Assert.That(response.StatusCode).IsEqualTo(HttpStatusCode.NoContent).Because(await response.Content.ReadAsStringAsync()); }
        private static string Regions(JsonElement row)
        {
            return string.Join(',', row.GetProperty("regions").EnumerateArray().Select(region => region.GetString()));
        }

        private static async Task<string> EvidenceAsync(HttpResponseMessage response, HttpClient producer)
        { var body = await response.Content.ReadAsStringAsync(); return body[..Math.Min(1000, body.Length)] + "; producer failures: " + (await ReadAsync(producer, "/__test/observations")).GetProperty("failures").GetRawText(); }
    }
}
