using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using TerraScale.Dashboard.E2E.Features.Databases;
using TerraScale.Dashboard.Features.Accounts;
using TerraScale.DotNet;

namespace TerraScale.Dashboard.E2E.Features.Backups;

[ClassDataSource<DashboardRootFixture>]
public sealed class BackupJourneyTests(DashboardRootFixture fixture)
{
    [Test]
    public async Task NativeAndMcpBackupsRestoreActualStoredSnapshotsAcrossReplicasWithoutChangingSource()
    {
        await using var host = await DatabaseSdkScenario.StartAsync(fixture.Root);
        using var client = host.CreateClient(); using var replica = host.CreateClient(true); using var producer = host.CreateProviderClient();
        var native = await DatabaseSdkJourneyTests.NativeWorkspaceAsync(client);
        using (var login = await HttpJourney.LoginAsync(replica, native.Email, native.Password))
            Check(login.StatusCode == HttpStatusCode.SeeOther, "The same newly activated account signs into the other app replica");
        var identity = await ReadAsync(client, "/api/v1/auth/me");
        var org = identity.GetProperty("organizationId").GetString()!; var project = identity.GetProperty("projectId").GetString()!;
        var secret = await HttpJourney.CreatePatAsync(client, "Backup protocol E2E", native.Password);
        var csrf = await HttpJourney.CsrfAsync(client, "/databases");
        var tools = (await HttpJourney.McpAsync(client, secret, "tools/list", new { })).GetProperty("result").GetProperty("tools");
        foreach (var name in new[] { "list_backups", "create_backup", "set_backup_policy", "restore_backup" })
            Check(tools.EnumerateArray().Any(tool => tool.GetProperty("name").GetString() == name), "Actual MCP discovery exposes the source tool: " + name);
        var policySchema = tools.EnumerateArray().Single(tool => tool.GetProperty("name").GetString() == "set_backup_policy").GetProperty("inputSchema");
        Check(new[] { "databaseId", "fullBackupCron", "retentionDays" }.All(name =>
            policySchema.GetProperty("required").EnumerateArray().Any(field => field.GetString() == name)), "Required policy inputs remain explicit");
        Check(!policySchema.GetProperty("required").EnumerateArray().Any(field => field.GetString() == "incrementalBackupCron"),
            "Clients do not have to supply an unsupported incremental schedule");
        var source = await CallAsync(client, secret, "create_database", new { name = "backup-source", region = "GRU" });
        var id = source.GetProperty("id").GetString()!;
        using (var collection = await HttpJourney.PostFormAsync(client, "/api/v1/dashboard/data/collections", new()
        { ["_csrf"] = csrf, ["databaseId"] = id, ["name"] = "users" }))
            Check(collection.StatusCode == HttpStatusCode.Created, "Native collection creation reaches the official Documents SDK");
        await CallAsync(client, secret, "create_document", new { databaseId = id, collection = "users", documentId = "01K78D0C000000000000000010", document = new { score = 1, nested = new { keep = true } } });
        var initial = await ReadAsync(replica, "/api/v1/dashboard/backups?databaseId=" + id);
        Check(initial.GetProperty("backups").GetArrayLength() == 0 && initial.GetProperty("policy").GetProperty("retentionDays").GetInt32() == 7 &&
            !initial.GetProperty("policy").TryGetProperty("updatedAt", out _), "An empty real producer inventory reports its actual default policy without an invented update timestamp");
        using var created = await HttpJourney.PostFormAsync(client, "/api/v1/dashboard/backups", new() { ["_csrf"] = csrf, ["databaseId"] = id });
        Check(created.StatusCode == HttpStatusCode.Created, "Native creation waits for a completed producer backup");
        var first = await JsonAsync(created); var backupId = first.GetProperty("id").GetString()!;
        Check(TerraScaleIds.IsValid(backupId) && first.EnumerateObject().Select(field => field.Name).Order(StringComparer.Ordinal)
            .SequenceEqual(new[] { "createdAt", "id", "sizeBytes", "status" }) && first.GetProperty("status").GetString() == "ready", "Native backup has the exact original public shape, without raw provider scope metadata");
        await CallAsync(replica, secret, "patch_document", new { databaseId = id, collection = "users", documentId = "01K78D0C000000000000000010", patch = new { score = 2 } });
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
            Check(html.Contains("Available snapshots", StringComparison.Ordinal), "Overview reports a measured snapshot count instead of an invented zero");
        }
        var before = await ReadAsync(producer, "/__test/observations");
        using (var quota = await HttpJourney.PostFormAsync(client, "/api/v1/dashboard/backups/restore", new()
        { ["_csrf"] = csrf, ["databaseId"] = id, ["backupId"] = backupId, ["name"] = "backup-restored" }))
            Check(quota.StatusCode == HttpStatusCode.Conflict, "A Free organization cannot restore a second database outside its quota");
        Check((await ReadAsync(producer, "/__test/observations")).GetProperty("backupRestores").GetInt32() == before.GetProperty("backupRestores").GetInt32(), "Quota refusal occurs before the producer can create a new database");
        // Fixture admission is test-only and distinct from the separately
        // exercised native paid-checkout journey; no production plan bypass.
        await ControlAsync(producer, new { command = "plan", organizationId = org, value = OrganizationRecord.StarterPlanId });
        using var restored = await HttpJourney.PostFormAsync(client, "/api/v1/dashboard/backups/restore", new()
        { ["_csrf"] = csrf, ["databaseId"] = id, ["backupId"] = backupId, ["name"] = "backup-restored" });
        Check(restored.StatusCode == HttpStatusCode.Created, "Native restore finishes a real new producer database and durable projection");
        var restoredBody = await JsonAsync(restored); var restoredId = restoredBody.GetProperty("id").GetString()!;
        Check(TerraScaleIds.IsValid(restoredId) && restoredId != id && restoredBody.GetProperty("name").GetString() == "backup-restored", "Native restore returns a new canonical identity");
        Check(await ScoreAsync(restoredId) == 1 && await ScoreAsync(id) == 2, "The restored database reads the captured snapshot through the production Documents SDK; the source retains its later write");
        var defaultRestore = await CallAsync(replica, secret, "restore_backup", new { databaseId = id });
        var defaultId = defaultRestore.GetProperty("databaseId").GetString()!;
        Check(defaultRestore.GetProperty("name").GetString() == "backup-source-restore" && defaultRestore.GetProperty("status").GetString() == "ready" && await ScoreAsync(defaultId) == 2,
            "MCP selects the newest actual restore point and retains the original {databaseId,name,status} shape");
        var projected = (await CallAsync(replica, secret, "list_databases", new { organizationId = org, projectId = project })).GetProperty("databases");
        Check(projected.GetArrayLength() == 3 && projected.EnumerateArray().Any(database => database.GetProperty("id").GetString() == restoredId), "The other app replica reads committed producer identities without a process-local restore");
        using (var saved = await HttpJourney.PostFormAsync(client, "/api/v1/dashboard/backups/policy", new()
        { ["_csrf"] = csrf, ["databaseId"] = id, ["fullBackupCron"] = "0 3 * * *", ["incrementalBackupCron"] = "", ["retentionDays"] = "14", ["disabled"] = "1" }))
        {
            Check(saved.StatusCode == HttpStatusCode.OK, "Native full policy uses the genuine PUT producer contract");
            var policy = await JsonAsync(saved);
            Check(policy.GetProperty("fullBackupCron").GetString() == "0 3 * * *" && policy.GetProperty("disabled").GetBoolean() && policy.TryGetProperty("updatedAt", out _), "The returned policy reflects the actual persisted schedule and pause state");
        }
        Check(!(await CallAsync(replica, secret, "get_database", new { databaseId = id })).GetProperty("backupsEnabled").GetBoolean(),
            "A provider-confirmed paused policy projects disabled rather than its old state");
        var resumed = await CallAsync(replica, secret, "set_backup_policy", new { databaseId = id, fullBackupCron = "", retentionDays = 35, disabled = false });
        Check(resumed.GetProperty("fullBackupCron").GetString() == "" && resumed.GetProperty("incrementalBackupCron").GetString() == "" &&
            resumed.GetProperty("retentionDays").GetInt32() == 35, "Null producer schedules are projected as empty public strings; no incremental schedule is invented");
        using (var page = await replica.GetAsync("/databases/" + id + "/backups"))
        {
            Check(page.StatusCode == HttpStatusCode.OK, "The actual native backup page renders across replicas");
            var html = await page.Content.ReadAsStringAsync();
            Check(html.Contains("Restore as new database", StringComparison.Ordinal) &&
                !html.Contains("name=\"replace\"", StringComparison.Ordinal) && html.Contains("name=\"incrementalBackupCron\" value=\"\"", StringComparison.Ordinal), "The native controls support genuine new-target restore and explicitly leave unsupported overwrite/incremental actions unavailable");
        }
        before = await ReadAsync(producer, "/__test/observations");
        using (var overwrite = await HttpJourney.PostFormAsync(client, "/api/v1/dashboard/backups/restore", new()
        { ["_csrf"] = csrf, ["databaseId"] = id, ["backupId"] = backupId, ["replace"] = "1", ["confirmName"] = "backup-source" }))
            Check(overwrite.StatusCode == HttpStatusCode.ServiceUnavailable, "Unsupported overwrite returns unavailable and never reports completion");
        await ErrorAsync(client, secret, "set_backup_policy", new { databaseId = id, fullBackupCron = "0 3 * * *", incrementalBackupCron = "0 * * * *", retentionDays = 7 });
        await ErrorAsync(client, secret, "get_document_history", new { databaseId = id, collection = "users", documentId = "01K78D0C000000000000000010" });
        var after = await ReadAsync(producer, "/__test/observations");
        Check(after.GetProperty("backupRestores").GetInt32() == before.GetProperty("backupRestores").GetInt32() && after.GetProperty("policyWrites").GetInt32() == before.GetProperty("policyWrites").GetInt32(), "Unsupported actions issue no producer mutation");
        using (var collision = await HttpJourney.PostFormAsync(client, "/api/v1/dashboard/backups/restore", new()
        { ["_csrf"] = csrf, ["databaseId"] = id, ["backupId"] = backupId, ["name"] = "backup-source" }))
            Check(collision.StatusCode == HttpStatusCode.Conflict, "Restoring over an existing name is rejected before any copy");
        await ControlAsync(producer, new { command = "role", value = "member" });
        using (var denied = await HttpJourney.PostFormAsync(replica, "/api/v1/dashboard/backups", new() { ["_csrf"] = await HttpJourney.CsrfAsync(replica, "/profile"), ["databaseId"] = id }))
            Check(denied.StatusCode == HttpStatusCode.Forbidden, "Fresh provider membership denies native backup writes to a member");
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
        async Task<int> ScoreAsync(string databaseId) => (await CallAsync(replica, secret, "get_document", new { databaseId, collection = "users", documentId = "01K78D0C000000000000000010" })).GetProperty("value").GetProperty("score").GetInt32();
    }

    [Test]
    [Arguments("wrong-project")]
    [Arguments("same-id")]
    [Arguments("wrong-source")]
    [Arguments("wrong-name")]
    [Arguments("invalid-name")]
    [Arguments("phantom")]
    [Arguments("foreign-confirm")]
    [Arguments("outage")]
    public async Task ProviderRestoreCannotPublishAnUnconfirmedOrUnrelatedDatabase(string failure)
    {
        await using var host = await DatabaseSdkScenario.StartAsync(fixture.Root);
        using var client = host.CreateClient(); using var producer = host.CreateProviderClient();
        var native = await DatabaseSdkJourneyTests.NativeWorkspaceAsync(client);
        var identity = await ReadAsync(client, "/api/v1/auth/me");
        var org = identity.GetProperty("organizationId").GetString()!;
        await ControlAsync(producer, new { command = "plan", organizationId = org, value = OrganizationRecord.StarterPlanId });
        var secret = await HttpJourney.CreatePatAsync(client, "Backup identity guard", native.Password);
        var source = await CallAsync(client, secret, "create_database", new { name = "guard-source", region = "GRU" });
        var id = source.GetProperty("id").GetString()!;
        var backup = await CallAsync(client, secret, "create_backup", new { databaseId = id });
        await ControlAsync(producer, new { command = "backup-failure", value = failure });
        var csrf = await HttpJourney.CsrfAsync(client, "/profile");
        using (var restore = await HttpJourney.PostFormAsync(client, "/api/v1/dashboard/backups/restore", new()
        { ["_csrf"] = csrf, ["databaseId"] = id, ["backupId"] = backup.GetProperty("id").GetString()!, ["name"] = "guard-restored" }))
            Check(!restore.IsSuccessStatusCode, "A malformed/unconfirmed provider restore must never report success: " + failure);
        // The list follows TerraBase, so a database the provider really restored is listed under its own identity; the
        // malformed answer itself is never recorded and never replaces the source.
        var databases = (await CallAsync(client, secret, "list_databases", new { })).GetProperty("databases").EnumerateArray().ToArray();
        Check(databases.Count(database => database.GetProperty("id").GetString() == id || database.GetProperty("name").GetString() == "guard-source") == 1 &&
            databases.Single(database => database.GetProperty("id").GetString() == id).GetProperty("name").GetString() == "guard-source",
            "Invalid restore metadata neither records the malformed answer nor replaces the original committed database: " + failure);
    }

    [Test]
    public async Task NativeProviderValidationAndNonAdvancingPaginationStayUnavailable()
    {
        await using var host = await DatabaseSdkScenario.StartAsync(fixture.Root);
        using var client = host.CreateClient(); using var producer = host.CreateProviderClient();
        var native = await DatabaseSdkJourneyTests.NativeWorkspaceAsync(client);
        var secret = await HttpJourney.CreatePatAsync(client, "Backup validation", native.Password);
        var source = await CallAsync(client, secret, "create_database", new { name = "validation-source", region = "GRU" });
        var id = source.GetProperty("id").GetString()!;
        var csrf = await HttpJourney.CsrfAsync(client, "/profile");
        var writes = (await ReadAsync(producer, "/__test/observations")).GetProperty("policyWrites").GetInt32();
        using (var invalid = await HttpJourney.PostFormAsync(client, "/api/v1/dashboard/backups/policy", new()
        { ["_csrf"] = csrf, ["databaseId"] = id, ["fullBackupCron"] = "not a valid cron expression", ["incrementalBackupCron"] = "", ["retentionDays"] = "7" }))
            Check(invalid.StatusCode == HttpStatusCode.UnprocessableEntity, "Native cron validation uses the genuine producer's Cronos Standard contract before a mutation");
        Check((await ReadAsync(producer, "/__test/observations")).GetProperty("policyWrites").GetInt32() == writes,
            "Invalid cron input does not send a producer policy mutation");
        using (var invalid = await HttpJourney.PostFormAsync(client, "/api/v1/dashboard/backups/policy", new()
        { ["_csrf"] = csrf, ["databaseId"] = id, ["fullBackupCron"] = "0 3 * * *", ["incrementalBackupCron"] = "", ["retentionDays"] = "36" }))
            Check(invalid.StatusCode == HttpStatusCode.UnprocessableEntity, "Native retention rejects outside the documented range before a mutation");
        await CallAsync(client, secret, "create_backup", new { databaseId = id });
        await CallAsync(client, secret, "create_backup", new { databaseId = id });
        await ControlAsync(producer, new { command = "backup-failure", value = "repeat-cursor" });
        using (var unavailable = await client.GetAsync("/api/v1/dashboard/backups?databaseId=" + id))
            Check(unavailable.StatusCode == HttpStatusCode.ServiceUnavailable, "A repeated opaque producer cursor cannot produce a truncated successful inventory");
        await ErrorAsync(client, secret, "list_backups", new { databaseId = id });
        var metadata = await CallAsync(client, secret, "get_database", new { databaseId = id });
        Check(!metadata.TryGetProperty("backupsEnabled", out _) && metadata.GetProperty("unavailable").GetString()!.Contains("Backup", StringComparison.Ordinal),
            "Unavailable backup measurements omit the unknown boolean rather than claim scheduling is disabled");
        using (var overview = await client.GetAsync("/databases/" + id))
        {
            var html = await overview.Content.ReadAsStringAsync();
            Check(overview.StatusCode == HttpStatusCode.OK && html.Contains("Not reported by provider", StringComparison.Ordinal) &&
                !html.Contains("Available snapshots", StringComparison.Ordinal), "An unavailable snapshot count renders unknown rather than zero");
        }
        await ControlAsync(producer, new { command = "backup-failure", value = "foreign-backup" });
        using (var invalid = await HttpJourney.PostFormAsync(client, "/api/v1/dashboard/backups", new() { ["_csrf"] = csrf, ["databaseId"] = id }))
            Check(invalid.StatusCode == HttpStatusCode.ServiceUnavailable, "A producer backup from another database is never exposed as this database's snapshot");
    }

    private static async Task<JsonElement> CallAsync(HttpClient client, string secret, string name, object arguments)
    {
        var rpc = await HttpJourney.McpAsync(client, secret, "tools/call", new { name, arguments });
        Check(!IsError(rpc), "Actual MCP tool must succeed: " + name + " " + rpc.GetRawText());
        return rpc.GetProperty("result").GetProperty("structuredContent").Clone();
    }
    private static async Task ErrorAsync(HttpClient client, string secret, string name, object arguments)
        => Check(IsError(await HttpJourney.McpAsync(client, secret, "tools/call", new { name, arguments })), "Actual MCP tool fails closed: " + name);
    private static bool IsError(JsonElement rpc) => rpc.TryGetProperty("error", out _) || rpc.GetProperty("result").TryGetProperty("isError", out var value) && value.GetBoolean();
    private static async Task<JsonElement> ReadAsync(HttpClient client, string path)
    { using var response = await client.GetAsync(path); response.EnsureSuccessStatusCode(); return await JsonAsync(response); }
    private static async Task<JsonElement> JsonAsync(HttpResponseMessage response)
    { using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync()); return json.RootElement.Clone(); }
    private static async Task ControlAsync(HttpClient client, object command)
    { using var response = await client.PostAsJsonAsync("/__test/control", command); response.EnsureSuccessStatusCode(); }
    private static void Check(bool condition, string message) { if (!condition) throw new InvalidOperationException(message); }
}
