using System.Net;
using TerraScale.Dashboard.Platform.Rpc;
using System.Text.Json;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using TerraScale.Dashboard.E2E.Features.Databases;
using TerraScale.TerraBase.Contracts;

namespace TerraScale.Dashboard.E2E.Features.Backups
{
    [Category("Slow")]
    [ClassDataSource<DashboardRootFixture>]
    public sealed class BackupJourneyTests(DashboardRootFixture fixture)
    {
        private static readonly string[] BackupFields = ["createdAt", "id", "sizeBytes", "status"];
        private static readonly string[] PolicyInputs = ["databaseId", "fullBackupCron", "retentionDays"];
        private sealed class RestoreTransportFailure(IHttpClientFactory original, Action refused) : HttpMessageHandler, IHttpClientFactory
        {
            public HttpClient CreateClient(string name)
            {
                return new(this, disposeHandler: false);
            }

            protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            {
                if (request.RequestUri!.AbsolutePath.EndsWith("/restore", StringComparison.Ordinal))
                {
                    refused();
                    throw new HttpRequestException("Synthetic transport refusal.");
                }
                using var client = original.CreateClient("platform");
                using var forwarded = new HttpRequestMessage(request.Method, request.RequestUri)
                { Version = request.Version, VersionPolicy = request.VersionPolicy, Content = request.Content };
                foreach (var header in request.Headers)
                {
                    forwarded.Headers.TryAddWithoutValidation(header.Key, header.Value);
                }

                return await client.SendAsync(forwarded, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
            }
        }

        [Test]
        public async Task NativeAndMcpBackupsRestoreActualStoredSnapshotsAcrossReplicasWithoutChangingSource()
        {
            await using var host = await DatabaseSdkScenario.StartAsync(fixture.Root);
            using var client = host.CreateClient(); using var replica = host.CreateClient(true); using var producer = host.CreateProviderClient();
            var native = await DatabaseJourney.NativeWorkspaceAsync(client);
            using (var login = await HttpJourney.LoginAsync(replica, native.Email, native.Password))
            {
                Check(login.StatusCode == HttpStatusCode.SeeOther, "The same newly activated account signs into the other app replica");
            }

            var identity = await ReadAsync(client, "/api/v1/auth/me");
            var org = identity.GetProperty("organizationId").GetString()!; var project = identity.GetProperty("projectId").GetString()!;
            var secret = await HttpJourney.CreatePatAsync(client, "Backup protocol E2E", native.Password);
            var csrf = await HttpJourney.CsrfAsync(client, "/databases");
            var tools = (await HttpJourney.McpAsync(client, secret, "tools/list", new { })).GetProperty("result").GetProperty("tools");
            foreach (var name in new[] { "list_backups", "create_backup", "set_backup_policy", "restore_backup", "get_restore_status" })
            {
                Check(tools.EnumerateArray().Any(tool => tool.GetProperty("name").GetString() == name), "Actual MCP discovery exposes the source tool: " + name);
            }

            var policySchema = tools.EnumerateArray().Single(tool => tool.GetProperty("name").GetString() == "set_backup_policy").GetProperty("inputSchema");
            Check(PolicyInputs.All(name =>
                policySchema.GetProperty("required").EnumerateArray().Any(field => field.GetString() == name)), "Required policy inputs remain explicit");
            Check(!policySchema.GetProperty("required").EnumerateArray().Any(field => field.GetString() == "incrementalBackupCron"),
                "Clients do not have to supply an unsupported incremental schedule");
            var source = await CallAsync(client, secret, "create_database", new { name = "backup-source", region = "GRU" });
            var id = source.GetProperty("id").GetString()!;
            await SqlAsync(client, csrf, id, "CREATE TABLE users (id TEXT PRIMARY KEY, score INTEGER)");
            await SqlAsync(client, csrf, id, "INSERT INTO users VALUES ('01K78D0C000000000000000010', 1)");
            var initial = await ReadAsync(replica, "/api/v1/dashboard/backups?databaseId=" + id);
            Check(initial.GetProperty("backups").GetArrayLength() == 0 && initial.GetProperty("policy").GetProperty("retentionDays").GetInt32() == 7 &&
                !initial.GetProperty("policy").TryGetProperty("updatedAt", out _), "An empty real producer inventory reports its actual default policy without an invented update timestamp");
            using var created = await HttpJourney.PostFormAsync(client, "/api/v1/dashboard/backups", new() { ["_csrf"] = csrf, ["databaseId"] = id });
            Check(created.StatusCode == HttpStatusCode.Created, "Native creation waits for a completed producer backup");
            var first = await JsonAsync(created); var backupId = first.GetProperty("id").GetString()!;
            Check(TerraScaleIds.IsValid(backupId) && first.EnumerateObject().Select(field => field.Name).Order(StringComparer.Ordinal)
                .SequenceEqual(BackupFields) && first.GetProperty("status").GetString() == "ready", "Native backup has the exact original public shape, without raw provider scope metadata");
            await SqlAsync(replica, await HttpJourney.CsrfAsync(replica, "/databases"), id, "UPDATE users SET score = 2");
            var second = await CallAsync(replica, secret, "create_backup", new { databaseId = id });
            var listing = await CallAsync(client, secret, "list_backups", new { databaseId = id });
            var backupIds = listing.GetProperty("backups").EnumerateArray().Select(row => row.GetProperty("id").GetString()).ToArray();
            Check(backupIds.SequenceEqual(new[] { backupId, second.GetProperty("id").GetString() }.Order(StringComparer.Ordinal)),
                "MCP follows upstream continuation and orders every snapshot by immutable ID");
            var measured = await CallAsync(client, secret, "get_database", new { databaseId = id });
            Check(measured.GetProperty("backupsEnabled").GetBoolean(), "Database metadata projects the actual enabled full-backup policy");
            using (var overview = await replica.GetAsync("/databases/" + id))
            {
                Check(overview.StatusCode == HttpStatusCode.OK, "The actual Overview reads backup measurements across replicas");
                var html = await overview.Content.ReadAsStringAsync();
                Check(System.Text.RegularExpressions.Regex.IsMatch(html, "Restore points</dt><dd class=\"ts-stat-value\">[0-9]+</dd>"), "Overview reports a measured snapshot count instead of an invented zero");
            }
            var before = await ReadAsync(producer, "/__test/observations");
            using (var quota = await HttpJourney.PostFormAsync(client, "/api/v1/dashboard/backups/restore", new()
            { ["_csrf"] = csrf, ["databaseId"] = id, ["backupId"] = backupId, ["name"] = "backup-restored" }))
            {
                Check(quota.StatusCode == HttpStatusCode.Conflict, "A Free organization cannot restore a second database outside its quota");
            }

            Check((await ReadAsync(producer, "/__test/observations")).GetProperty("backupRestores").GetInt32() == before.GetProperty("backupRestores").GetInt32(), "Quota refusal occurs before the producer can create a new database");
            // Fixture admission is test-only and distinct from the separately
            // exercised native paid-checkout journey; no production plan bypass.
            await ControlAsync(producer, new { command = "plan", organizationId = org, value = OrganizationRecord.StarterPlanId });
            using var restored = await HttpJourney.PostFormAsync(client, "/api/v1/dashboard/backups/restore", new()
            { ["_csrf"] = csrf, ["databaseId"] = id, ["backupId"] = backupId, ["name"] = "backup-restored" });
            Check(restored.StatusCode == HttpStatusCode.Accepted, "Native restore returns the accepted operation at once instead of waiting for the copy");
            var restoredBody = await JsonAsync(restored);
            var operationId = restoredBody.GetProperty("operationId").GetString()!;
            Check(TerraScaleIds.IsValid(operationId) && restoredBody.GetProperty("status").GetString() == "running", "Native restore answers with a pollable operation identity");
            var completed = await RestoreCompletedAsync(client, id, operationId);
            var restoredId = completed.GetProperty("targetDatabaseId").GetString()!;
            Check(TerraScaleIds.IsValid(restoredId) && restoredId != id && completed.GetProperty("targetName").GetString() == "backup-restored", "Native restore status reports the new canonical identity once the copy lands");
            using (var htmlRestore = new HttpRequestMessage(HttpMethod.Post, "/api/v1/dashboard/backups/restore")
            { Content = new FormUrlEncodedContent(new Dictionary<string, string> { ["_csrf"] = csrf, ["databaseId"] = id, ["backupId"] = backupId, ["name"] = "backup-html-restored" }) })
            {
                htmlRestore.Headers.Accept.ParseAdd("text/html");
                using var redirected = await client.SendAsync(htmlRestore);
                var location = redirected.Headers.Location?.OriginalString ?? "";
                var htmlOperation = location.StartsWith("/databases/" + id + "/backups?restore=", StringComparison.Ordinal)
                    ? location[("/databases/" + id + "/backups?restore=").Length..] : "";
                Check(redirected.StatusCode == HttpStatusCode.SeeOther && TerraScaleIds.IsValid(htmlOperation),
                    "The dashboard form redirect carries the restore operation back to the backups page");
                var htmlCompleted = await RestoreCompletedAsync(client, id, htmlOperation);
                var htmlId = htmlCompleted.GetProperty("targetDatabaseId").GetString()!;
                using var page = await client.GetAsync("/databases/" + id + "/backups?restore=" + htmlOperation);
                var html = await page.Content.ReadAsStringAsync();
                Check(page.StatusCode == HttpStatusCode.OK && html.Contains("id=\"restore-status\"", StringComparison.Ordinal) &&
                    html.Contains("href=\"/databases/" + htmlId + "\"", StringComparison.Ordinal) && html.Contains("backup-html-restored", StringComparison.Ordinal),
                    "The backups page shows the completed restore with a link to the restored database");
                await CallAsync(client, secret, "delete_database", new { databaseId = htmlId, confirmName = "backup-html-restored" });
            }
            using (var unpolled = await HttpJourney.PostFormAsync(client, "/api/v1/dashboard/backups/restore", new()
            { ["_csrf"] = csrf, ["databaseId"] = id, ["backupId"] = backupId, ["name"] = "backup-unpolled" }))
            {
                Check(unpolled.StatusCode == HttpStatusCode.Accepted && TerraScaleIds.IsValid((await JsonAsync(unpolled)).GetProperty("operationId").GetString()!),
                    "A restore started without a watching browser still answers with a pollable operation");
                var listed = await ReadAsync(client, "/api/v1/dashboard/databases?limit=100");
                var unpolledRow = listed.GetProperty("items").EnumerateArray()
                    .SingleOrDefault(database => database.GetProperty("name").GetString() == "backup-unpolled");
                Check(unpolledRow.ValueKind != JsonValueKind.Undefined && TerraScaleIds.IsValid(unpolledRow.GetProperty("id").GetString()!),
                    "Loading the databases list settles an unwatched completed restore into the project");
                await CallAsync(client, secret, "delete_database", new { databaseId = unpolledRow.GetProperty("id").GetString()!, confirmName = "backup-unpolled" });
            }

            Check(await ScoreAsync(restoredId) == 1 && await ScoreAsync(id) == 2, "The restored database reads the captured snapshot through SQL; the source retains its later write");
            var defaultStart = await CallAsync(replica, secret, "restore_backup", new { databaseId = id });
            var defaultOperation = defaultStart.GetProperty("operationId").GetString()!;
            Check(TerraScaleIds.IsValid(defaultOperation) && defaultStart.GetProperty("status").GetString() is "running" or "completed",
                "MCP restore starts the newest actual restore point and answers with a pollable operation instead of blocking");
            var defaultDone = await RestoreCompletedAsync(replica, id, defaultOperation);
            var defaultId = defaultDone.GetProperty("targetDatabaseId").GetString()!;
            Check(TerraScaleIds.IsValid(defaultId) && defaultDone.GetProperty("targetName").GetString() == "backup-source-restore" &&
                defaultDone.GetProperty("status").GetString() == "completed" && await ScoreAsync(defaultId) == 2,
                "MCP restore status reports the completed target database and its captured snapshot");
            var projected = (await CallAsync(replica, secret, "list_databases", new { organizationId = org, projectId = project })).GetProperty("databases");
            Check(projected.GetArrayLength() == 3 && projected.EnumerateArray().Any(database => database.GetProperty("id").GetString() == restoredId), "The other app replica reads committed producer identities without a process-local restore");
            using (var saved = await HttpJourney.PostFormAsync(client, "/api/v1/dashboard/backups/policy", new()
            { ["_csrf"] = csrf, ["databaseId"] = id, ["fullBackupCron"] = "0 3 * * *", ["incrementalBackupCron"] = "", ["retentionDays"] = "14" }))
            {
                Check(saved.StatusCode == HttpStatusCode.OK, "Native full policy uses the genuine PUT producer contract");
                var policy = await JsonAsync(saved);
                Check(policy.GetProperty("fullBackupCron").GetString() == "0 3 * * *" && policy.TryGetProperty("updatedAt", out _), "The returned policy reflects the actual persisted schedule");
            }
            Check((await CallAsync(replica, secret, "get_database", new { databaseId = id })).GetProperty("backupsEnabled").GetBoolean(),
                "A provider-confirmed schedule projects enabled rather than its old state");
            var resumed = await CallAsync(replica, secret, "set_backup_policy", new { databaseId = id, fullBackupCron = "", retentionDays = 35 });
            Check(resumed.GetProperty("fullBackupCron").GetString() == "" && resumed.GetProperty("incrementalBackupCron").GetString() == "" &&
                resumed.GetProperty("retentionDays").GetInt32() == 35, "Null producer schedules are projected as empty public strings; no incremental schedule is invented");
            await CallAsync(replica, secret, "set_backup_policy", new { databaseId = id, fullBackupCron = "0 4 * * *", retentionDays = 7 });
            using (var healthy = await replica.GetAsync("/databases/" + id + "/backups"))
            {
                var html = await healthy.Content.ReadAsStringAsync();
                Check(healthy.StatusCode == HttpStatusCode.OK && html.Contains("id=\"backup-health\"", StringComparison.Ordinal) &&
                    html.Contains("Last backup", StringComparison.Ordinal) && html.Contains("Next", StringComparison.Ordinal),
                    "The backups page reports the last successful scheduled backup and the next due time");
            }
            await ControlAsync(producer, new { command = "policy-health", value = "fail" });
            using (var failing = await replica.GetAsync("/databases/" + id + "/backups"))
            {
                var html = await failing.Content.ReadAsStringAsync();
                Check(failing.StatusCode == HttpStatusCode.OK && html.Contains("id=\"backup-health\"", StringComparison.Ordinal) &&
                    html.Contains("ts-alert-danger", StringComparison.Ordinal) && html.Contains("Backup storage is temporarily unavailable.", StringComparison.Ordinal),
                    "The backups page shows the failure state with the producer's last error message");
            }
            using (var page = await replica.GetAsync("/databases/" + id + "/backups"))
            {
                Check(page.StatusCode == HttpStatusCode.OK, "The actual native backup page renders across replicas");
                var html = await page.Content.ReadAsStringAsync();
                Check(html.Contains("Restore as new database", StringComparison.Ordinal) &&
                    !html.Contains("name=\"replace\"", StringComparison.Ordinal) && !html.Contains("incrementalBackupCron", StringComparison.Ordinal), "The native controls support genuine new-target restore and explicitly leave unsupported overwrite/incremental actions unavailable");
            }
            before = await ReadAsync(producer, "/__test/observations");
            using (var overwrite = await HttpJourney.PostFormAsync(client, "/api/v1/dashboard/backups/restore", new()
            { ["_csrf"] = csrf, ["databaseId"] = id, ["backupId"] = backupId, ["replace"] = "1", ["confirmName"] = "backup-source" }))
            {
                Check(overwrite.StatusCode == HttpStatusCode.ServiceUnavailable, "Unsupported overwrite returns unavailable and never reports completion");
            }

            await ErrorAsync(client, secret, "set_backup_policy", new { databaseId = id, fullBackupCron = "0 3 * * *", incrementalBackupCron = "0 * * * *", retentionDays = 7 });
            var after = await ReadAsync(producer, "/__test/observations");
            Check(after.GetProperty("backupRestores").GetInt32() == before.GetProperty("backupRestores").GetInt32() && after.GetProperty("policyWrites").GetInt32() == before.GetProperty("policyWrites").GetInt32(), "Unsupported actions issue no producer mutation");
            using (var collision = await HttpJourney.PostFormAsync(client, "/api/v1/dashboard/backups/restore", new()
            { ["_csrf"] = csrf, ["databaseId"] = id, ["backupId"] = backupId, ["name"] = "backup-source" }))
            {
                Check(collision.StatusCode == HttpStatusCode.Conflict, "Restoring over an existing name is rejected before any copy");
            }

            await ControlAsync(producer, new { command = "role", value = "member" });
            using (var denied = await HttpJourney.PostFormAsync(replica, "/api/v1/dashboard/backups", new() { ["_csrf"] = await HttpJourney.CsrfAsync(replica, "/profile"), ["databaseId"] = id }))
            {
                Check(denied.StatusCode == HttpStatusCode.Forbidden, "Fresh provider membership denies native backup writes to a member");
            }

            await ErrorAsync(client, secret, "create_backup", new { databaseId = id });
            await ErrorAsync(replica, secret, "restore_backup", new { databaseId = id, backupId });
            after = await ReadAsync(producer, "/__test/observations");
            Check(after.GetProperty("backupCreates").GetInt32() == before.GetProperty("backupCreates").GetInt32(), "The pre-existing PAT cannot bypass a live role change");
            await ControlAsync(producer, new { command = "role", value = "owner" });
            await CallAsync(client, secret, "delete_database", new { databaseId = restoredId, confirmName = "backup-restored" });
            await CallAsync(replica, secret, "delete_database", new { databaseId = defaultId, confirmName = "backup-source-restore" });
            await CallAsync(client, secret, "delete_database", new { databaseId = id, confirmName = "backup-source" });
            var final = await ReadAsync(producer, "/__test/observations");
            Check(final.GetProperty("failures").GetArrayLength() == 0 && final.GetProperty("databases").GetArrayLength() == 0,
                "All native/MCP backup requests matched the frozen producer contract and native cleanup deleted each real database");
            Console.WriteLine("BACKUP PROTOCOL E2E: isolated producer snapshot restore verified with actual app HTTP/MCP + official SDK; live backup storage/scheduler/secret-delivery qualification remains open.");
            async Task<int> ScoreAsync(string databaseId) => (await CallAsync(replica, secret, "execute_sql", new { databaseId, sql = "SELECT score FROM users" })).GetProperty("rows")[0][0].GetInt32();
        }

        internal static async Task<JsonElement> RestoreCompletedAsync(HttpClient client, string databaseId, string operationId)
        {
            JsonElement status = default;
            for (var attempt = 0; attempt < 120; attempt++)
            {
                using var poll = await client.GetAsync("/api/v1/dashboard/backups/restore?databaseId=" + databaseId + "&operation=" + operationId);
                poll.EnsureSuccessStatusCode();
                using var polled = JsonDocument.Parse(await poll.Content.ReadAsStringAsync());
                status = polled.RootElement.Clone();
                if (status.GetProperty("status").GetString() != "running")
                {
                    return status;
                }

                await Task.Delay(TimeSpan.FromMilliseconds(250));
            }

            throw new InvalidOperationException("The restore operation did not finish: " + status.GetRawText());
        }

        private static async Task<JsonElement> CallAsync(HttpClient client, string secret, string name, object arguments)
        {
            var rpc = await HttpJourney.McpAsync(client, secret, "tools/call", new { name, arguments });
            Check(!IsError(rpc), "Actual MCP tool must succeed: " + name + " " + rpc.GetRawText());
            return rpc.GetProperty("result").GetProperty("structuredContent").Clone();
        }
        private static async Task SqlAsync(HttpClient client, string csrf, string databaseId, string sql)
        {
            using var response = await HttpJourney.PostFormAsync(client, "/api/v1/dashboard/data/sql", new() { ["_csrf"] = csrf, ["databaseId"] = databaseId, ["sql"] = sql });
            Check(response.StatusCode == HttpStatusCode.OK, "The SQL editor runs the statement on the producer: " + sql);
        }

        private static async Task ErrorAsync(HttpClient client, string secret, string name, object arguments)
        {
            Check(IsError(await HttpJourney.McpAsync(client, secret, "tools/call", new { name, arguments })), "Actual MCP tool fails closed: " + name);
        }

        private static bool IsError(JsonElement rpc)
        {
            return rpc.TryGetProperty("error", out _) || rpc.GetProperty("result").TryGetProperty("isError", out var value) && value.GetBoolean();
        }

        private static async Task<JsonElement> ReadAsync(HttpClient client, string path)
        { using var response = await client.GetAsync(path); response.EnsureSuccessStatusCode(); return await JsonAsync(response); }
        private static async Task<JsonElement> JsonAsync(HttpResponseMessage response)
        { using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync()); return json.RootElement.Clone(); }
        private static async Task ControlAsync(HttpClient client, object command)
        { using var response = await client.PostAsJsonAsync("/__test/control", command); response.EnsureSuccessStatusCode(); }
        private static void Check(bool condition, string message)
        {
            if (!condition)
            {
                throw new InvalidOperationException(message);
            }
        }
    }
}
