using System.Text.Json;
using System.Text.RegularExpressions;
using TerraScale.DotNet.TerraBase;

namespace TerraScale.DotNet.Tests.TerraBase;

/// <summary>
/// Every client operation sends exactly the method and route that <c>contracts/terrabase/routes.json</c> records, and every
/// recorded route is used. The monorepo checks that the TerraBase built from the same commit serves each recorded route,
/// so a drift on either side fails a test. No TerraBase runs here: a recording handler answers nothing.
/// </summary>
public sealed class TerraBaseContractTests
{
    private const string Project = "01JA0000000000000000000001";
    private const string Organization = "01JA0000000000000000000002";
    private const string User = "01JA0000000000000000000003";
    private static readonly Uri Private = new("http://100.64.0.1:8081/");

    [Test]
    public async Task EveryOperationSendsItsRecordedRouteAndEveryRouteIsUsed()
    {
        var routes = Load();
        using var document = JsonDocument.Parse("{\"a\":1}");
        var json = document.RootElement;
        var used = new HashSet<string>(StringComparer.Ordinal);
        foreach (var (client, operation, call) in Calls(json))
        {
            var sent = await RecordAsync(client, call);
            var route = routes.SingleOrDefault(route => route.Client == client && route.Method == sent.Method.Method && route.Matches(sent.Path));
            await Assert.That(route?.Operation).IsEqualTo(operation).Because($"{sent.Method} {sent.Path}");
            used.Add(operation);
        }

        await Assert.That(routes.Select(route => route.Operation).Except(used)).IsEmpty();
    }

    [Test]
    public async Task ThePlatformClientNeedsAnAccessTokenProvider()
    {
        using var http = new HttpClient();
        await Assert.That(() => new TerraBasePlatformClient(http, new TerraBasePlatformClientOptions { BaseAddress = new Uri("https://terrabase.example.test/") }))
            .Throws<ArgumentException>();
    }

    [Test]
    public async Task DocumentIdsTravelInTheQuery()
    {
        var sent = await RecordAsync("documents", client => ((TerraBaseDocumentsClient)client).Database(Project, "app").GetDocumentAsync("users", User));
        await Assert.That(sent.Path).IsEqualTo($"/v1/projects/{Project}/databases/app/collections/users/documents/by-key");
        await Assert.That(sent.Query).IsEqualTo($"?id={User}");
    }

    [Test]
    [Arguments("a/b c?d")]
    [Arguments("01ja0000000000000000000003")]
    [Arguments("0190f3b2-7c4e-7a1b-9c2d-3e4f5a6b7c8d")]
    public async Task NonUlidDocumentIdsAreRejectedBeforeAnyRequest(string id)
    {
        var requests = 0;
        using var handler = new TerraBaseTestHandler((_, _) => throw new HttpRequestException($"request {++requests} sent"));
        using var http = new HttpClient(handler);
        var origin = new Uri("https://terrabase.example.test/");
        var db = new TerraBaseDocumentsClient(http, new TerraBaseDocumentsClientOptions { BaseAddress = origin, ApiKey = "tb_test_key" }).Database(Project, "app");
        var platform = new TerraBasePlatformClient(http, new TerraBasePlatformClientOptions { BaseAddress = origin, AccessTokenProvider = _ => ValueTask.FromResult("user-token") });
        using var document = JsonDocument.Parse("{}");
        var json = document.RootElement;
        await Assert.ThrowsAsync<ArgumentException>(() => db.GetDocumentAsync("users", id));
        await Assert.ThrowsAsync<ArgumentException>(() => db.PutDocumentAsync("users", id, json));
        await Assert.ThrowsAsync<ArgumentException>(() => db.PatchDocumentAsync("users", id, json));
        await Assert.ThrowsAsync<ArgumentException>(() => db.PatchDocumentAsync("users", id, [TerraBasePatchOperation.Unset("/a")]));
        await Assert.ThrowsAsync<ArgumentException>(() => db.DeleteDocumentAsync("users", id));
        await Assert.ThrowsAsync<ArgumentException>(() => platform.GetDocumentAsync("ts-auth", "users", id));
        await Assert.That(() => TerraBaseTransactionOperation.Create("users", json, id)).Throws<ArgumentException>();
        await Assert.That(() => TerraBaseTransactionOperation.Put("users", id, json)).Throws<ArgumentException>();
        await Assert.That(() => TerraBaseTransactionOperation.Patch("users", id, json)).Throws<ArgumentException>();
        await Assert.That(() => TerraBaseTransactionOperation.Delete("users", id)).Throws<ArgumentException>();
        await Assert.That(() => TerraBaseTransactionOperation.Assert("users", id, json)).Throws<ArgumentException>();
        await Assert.That(() => TerraBaseTransactionOperation.AssertAbsent("users", id)).Throws<ArgumentException>();
        await Assert.That(TerraBaseTransactionOperation.Create("users", json).Id).IsNull();
        await Assert.That(requests).IsEqualTo(0);
    }

    private static IEnumerable<(string Client, string Operation, Func<object, Task> Call)> Calls(JsonElement json)
    {
        static TerraBaseDatabase Db(object client) => ((TerraBaseDocumentsClient)client).Database(Project, "app");
        static TerraBaseProjectClient ProjectClient(object client) => ((TerraBaseDocumentsClient)client).Project(Project);
        static TerraBaseManagementClient M(object client) => (TerraBaseManagementClient)client;
        var schema = new TerraBaseUniqueSchemaRequest(Indexes: [new("email", ["email"], Unique: true)]);
        yield return ("documents", "createChangeWebhook", client => Db(client).CreateChangeWebhookAsync("users", "https://receiver.example/events"));
        yield return ("documents", "listChangeWebhooks", client => Db(client).ListChangeWebhooksAsync("users"));
        yield return ("documents", "deleteChangeWebhook", client => Db(client).DeleteChangeWebhookAsync("users", User));
        yield return ("documents", "listChangeWebhookDeliveries", client => Db(client).ListChangeWebhookDeliveriesAsync("users", User));
        yield return ("documents", "invokeFunction", client => Db(client).InvokeFunctionAsync("hello"));
        yield return ("documents", "listDatabaseLogs", client => Db(client).ListRequestLogsAsync());
        yield return ("documents", "listFunctionLogs", client => Db(client).ListFunctionLogsAsync("hello"));
        yield return ("documents", "listFunctionDeliveries", client => Db(client).ListFunctionDeliveriesAsync("hello"));
        yield return ("documents", "getExportSchedule", client => Db(client).GetExportScheduleAsync());
        yield return ("documents", "setExportSchedule", client => Db(client).SetExportScheduleAsync(null));
        yield return ("documents", "listFunctions", client => Db(client).ListFunctionsAsync());
        yield return ("documents", "deployFunction", client => Db(client).DeployFunctionAsync("hello", new("function handler(event) { return event; }")));
        yield return ("documents", "getFunction", client => Db(client).GetFunctionAsync("hello"));
        yield return ("documents", "deleteFunction", client => Db(client).DeleteFunctionAsync("hello"));
        yield return ("documents", "createDatabase", client => Db(client).CreateAsync());
        yield return ("documents", "deleteDatabase", client => Db(client).DeleteAsync());
        yield return ("documents", "cancelClosure", client => Db(client).CancelClosureAsync(User, User));
        yield return ("documents", "listCollections", client => Db(client).ListCollectionsAsync());
        yield return ("documents", "createCollection", client => Db(client).CreateCollectionAsync("users"));
        yield return ("documents", "getCollection", client => Db(client).GetCollectionAsync("users"));
        yield return ("documents", "getCollectionRules", client => Db(client).GetCollectionRulesAsync("users"));
        yield return ("documents", "putCollectionRules", client => Db(client).SetCollectionRulesAsync("users", new(null, new(false, false, []), new(false, false, []))));
        yield return ("documents", "dropCollection", client => Db(client).DropCollectionAsync("users"));
        yield return ("documents", "declareCollectionSchema", client => Db(client).DeclareSchemaAsync("users", schema));
        yield return ("documents", "getCollectionSchema", client => Db(client).GetSchemaAsync("users"));
        yield return ("documents", "getCollectionSchema", client => Db(client).GetInferredSchemaAsync("users"));
        yield return ("documents", "getDatabaseSchema", client => Db(client).ListCollectionStatsAsync());
        yield return ("documents", "getDocument", client => Db(client).GetDocumentAsync("users", User));
        yield return ("documents", "listDocuments", client => Db(client).ListDocumentsAsync("users"));
        yield return ("documents", "createDocument", client => Db(client).CreateDocumentAsync("users", json));
        yield return ("documents", "putDocument", client => Db(client).PutDocumentAsync("users", User, json));
        yield return ("documents", "patchDocument", client => Db(client).PatchDocumentAsync("users", User, json));
        yield return ("documents", "patchDocument", client => Db(client).PatchDocumentAsync("users", User, [TerraBasePatchOperation.Unset("/a")]));
        yield return ("documents", "deleteDocument", client => Db(client).DeleteDocumentAsync("users", User));
        yield return ("documents", "queryDocuments", client => Db(client).QueryAsync("users", new TerraBaseQuery()));
        yield return ("documents", "runTransaction", client => Db(client).TransactAsync([TerraBaseTransactionOperation.Delete("users", User)]));
        yield return ("documents", "createImport", client => Db(client).CreateImportAsync(TerraBaseImportFormat.Sqlite, 1, new string('0', 64)));
        yield return ("documents", "getImport", client => Db(client).GetImportAsync());
        yield return ("documents", "uploadImportSource", client => Db(client).UploadImportSourceAsync(0, new byte[] { 1 }));
        yield return ("documents", "updateImport", client => Db(client).UpdateImportAsync(new TerraBaseImportOptions()));
        yield return ("documents", "startImport", client => Db(client).StartImportAsync("preview"));
        yield return ("documents", "cancelImport", client => Db(client).CancelImportAsync());
        yield return ("documents", "resumeImport", client => Db(client).ResumeImportAsync());

        yield return ("project", "createDatabase", client => ProjectClient(client).CreateDatabaseAsync("app"));
        yield return ("project", "listDatabases", client => ProjectClient(client).ListDatabasesAsync());
        yield return ("project", "getDatabase", client => ProjectClient(client).GetDatabaseAsync("app"));
        yield return ("project", "deleteDatabase", client => ProjectClient(client).DeleteDatabaseAsync("app"));
        yield return ("project", "updateDatabase", client => ProjectClient(client).SetDatabaseRegionsAsync("app", []));
        yield return ("project", "getProjectUsage", client => ProjectClient(client).GetUsageAsync());
        yield return ("project", "getProjectMetrics", client => ProjectClient(client).GetPrometheusMetricsAsync());
        yield return ("documents", "getDatabaseUsage", client => Db(client).GetUsageAsync());
        yield return ("documents", "streamDatabaseChanges", client => ReadFirstAsync(Db(client).StreamChangesAsync()));
        yield return ("documents", "exportDatabase", client => ReadFirstAsync(Db(client).ExportAsync()));
        yield return ("documents", "getDeclaredCollectionSchema", client => Db(client).GetDeclaredSchemaAsync("users"));
        yield return ("documents", "putDeclaredCollectionSchema", client => Db(client).PutDeclaredSchemaAsync("users", json));
        yield return ("documents", "deleteDeclaredCollectionSchema", client => Db(client).DeleteDeclaredSchemaAsync("users"));
        yield return ("documents", "getCollectionTtl", client => Db(client).GetCollectionTtlAsync("users"));
        yield return ("documents", "putCollectionTtl", client => Db(client).SetCollectionTtlAsync("users", "/expiresAt"));
        yield return ("documents", "deleteCollectionTtl", client => Db(client).ClearCollectionTtlAsync("users"));
        yield return ("documents", "branchDatabase", client => Db(client).BranchAsync("copy"));
        yield return ("documents", "runSql", client => Db(client).RunSqlAsync("SELECT * FROM users LIMIT 1"));
        yield return ("documents", "executeSql", client => Db(client).ExecuteSqlAsync("SELECT 1"));
        yield return ("documents", "listIndexes", client => Db(client).ListIndexesAsync());
        yield return ("documents", "createIndex", client => Db(client).CreateIndexAsync("users", ["email"]));
        yield return ("documents", "dropIndex", client => Db(client).DropIndexAsync("users_email"));

        yield return ("project", "createDatabase", client => ProjectClient(client).CreateDatabaseAsync("app"));
        yield return ("project", "listDatabases", client => ProjectClient(client).ListDatabasesAsync());
        yield return ("project", "getDatabase", client => ProjectClient(client).GetDatabaseAsync("app"));
        yield return ("project", "deleteDatabase", client => ProjectClient(client).DeleteDatabaseAsync("app"));
        yield return ("project", "updateDatabase", client => ProjectClient(client).SetDatabaseRegionsAsync("app", []));
        yield return ("project", "getProjectUsage", client => ProjectClient(client).GetUsageAsync());
        yield return ("project", "getProjectMetrics", client => ProjectClient(client).GetPrometheusMetricsAsync());
        yield return ("documents", "getDatabaseUsage", client => Db(client).GetUsageAsync());
        yield return ("documents", "streamDatabaseChanges", client => ReadFirstAsync(Db(client).StreamChangesAsync()));
        yield return ("documents", "getDeclaredCollectionSchema", client => Db(client).GetDeclaredSchemaAsync("users"));
        yield return ("documents", "putDeclaredCollectionSchema", client => Db(client).PutDeclaredSchemaAsync("users", json));
        yield return ("documents", "deleteDeclaredCollectionSchema", client => Db(client).DeleteDeclaredSchemaAsync("users"));
        yield return ("documents", "branchDatabase", client => Db(client).BranchAsync("copy"));
        yield return ("documents", "runSql", client => Db(client).RunSqlAsync("SELECT * FROM users LIMIT 1"));
        yield return ("documents", "listIndexes", client => Db(client).ListIndexesAsync());
        yield return ("documents", "createIndex", client => Db(client).CreateIndexAsync("users", ["email"]));
        yield return ("documents", "dropIndex", client => Db(client).DropIndexAsync("users_email"));

        static TerraBasePlatformClient P(object client) => (TerraBasePlatformClient)client;
        yield return ("platform", "listPlatformDatabases", client => P(client).ListDatabasesAsync());
        yield return ("platform", "listPlatformCollections", client => P(client).ListCollectionsAsync("ts-auth"));
        yield return ("platform", "listPlatformDocuments", client => P(client).ListDocumentsAsync("ts-auth", "users", limit: 10));
        yield return ("platform", "getPlatformDocument", client => P(client).GetDocumentAsync("ts-auth", "users", User));

        yield return ("management", "createOrganization", client => M(client).CreateOrganizationAsync(User, "Acme"));
        yield return ("management", "listOrganizations", client => M(client).ListOrganizationsAsync(User));
        yield return ("management", "getOrganization", client => M(client).GetOrganizationAsync(User, Organization));
        yield return ("management", "renameOrganization", client => M(client).RenameOrganizationAsync(User, Organization, "Acme"));
        yield return ("management", "setOrganizationPlan", client => M(client).SetPlanAsync(User, Organization, TerraBasePlan.Pro));
        yield return ("management", "deleteOrganization", client => M(client).DeleteOrganizationAsync(User, Organization));
        yield return ("management", "getCurrentOrganization", client => M(client).GetCurrentOrganizationAsync(User));
        yield return ("management", "setCurrentOrganization", client => M(client).SetCurrentOrganizationAsync(User, Organization));
        yield return ("management", "createProject", client => M(client).CreateProjectAsync(User, Organization, "App"));
        yield return ("management", "listProjects", client => M(client).ListProjectsAsync(User, Organization));
        yield return ("management", "getProject", client => M(client).GetProjectAsync(User, Organization, Project));
        yield return ("management", "renameProject", client => M(client).RenameProjectAsync(User, Organization, Project, "App"));
        yield return ("management", "deleteProject", client => M(client).DeleteProjectAsync(User, Organization, Project));
        yield return ("management", "closeProject", client => M(client).CloseProjectAsync(Project, Organization, User));
        yield return ("management", "closeProject", client => M(client).EraseProjectAsync(Project, Organization, User));
        yield return ("management", "requestHistoryErasure", client => M(client).RequestHistoryErasureAsync(User, DateTimeOffset.UnixEpoch));
        yield return ("management", "createManagedDatabase", client => M(client).CreateDatabaseAsync(User, Organization, Project, "app"));
        yield return ("management", "listDatabases", client => M(client).ListDatabasesAsync(User, Organization, Project));
        yield return ("management", "getDatabase", client => M(client).GetDatabaseAsync(User, Organization, Project, User));
        yield return ("management", "deleteManagedDatabase", client => M(client).DeleteDatabaseAsync(User, Organization, Project, User));
        yield return ("management", "branchDatabase", client => M(client).BranchDatabaseAsync(User, Organization, Project, User, "copy"));
        yield return ("management", "listMembers", client => M(client).ListMembersAsync(User, Organization));
        yield return ("management", "changeMemberRole", client => M(client).ChangeMemberRoleAsync(User, Organization, User, TerraBaseRole.Admin));
        yield return ("management", "removeMember", client => M(client).RemoveMemberAsync(User, Organization, User));
        yield return ("management", "addMember", client => M(client).AddMemberAsync(User, Organization, User, "ada@example.com"));
        yield return ("management", "leaveOrganization", client => M(client).LeaveOrganizationAsync(User, Organization));
        yield return ("management", "createInvitation", client => M(client).CreateInvitationAsync(User, Organization, "ada@example.com"));
        yield return ("management", "listInvitations", client => M(client).ListInvitationsAsync(User, Organization));
        yield return ("management", "revokeInvitation", client => M(client).RevokeInvitationAsync(User, Organization, User));
        yield return ("management", "acceptInvitation", client => M(client).AcceptInvitationAsync(User, "token"));
        yield return ("management", "getOwnershipTransfer", client => M(client).GetOwnershipTransferAsync(User, Organization));
        yield return ("management", "startOwnershipTransfer", client => M(client).StartOwnershipTransferAsync(User, Organization, User));
        yield return ("management", "confirmOwnershipTransfer", client => M(client).ConfirmOwnershipTransferAsync(User, Organization));
        yield return ("management", "cancelOwnershipTransfer", client => M(client).CancelOwnershipTransferAsync(User, Organization));
        yield return ("management", "getAuditRetention", client => M(client).GetAuditRetentionAsync(User, Organization));
        yield return ("management", "setAuditRetention", client => M(client).SetAuditRetentionAsync(User, Organization, 30));
        yield return ("management", "listAuditEvents", client => M(client).ListAuditEventsAsync(User, Organization));
    }

    // Sends one call through a handler that records the request and then fails it, so no response is ever parsed.
    private static async Task<(HttpMethod Method, string Path, string Query)> RecordAsync(string client, Func<object, Task> call)
    {
        HttpRequestMessage? sent = null;
        using var handler = new TerraBaseTestHandler((request, _) =>
        {
            sent = request;
            throw new HttpRequestException("recorded");
        });
        using var http = new HttpClient(handler);
        var origin = new Uri("https://terrabase.example.test/");
        // Management here goes to the private listener (the platform operator, no credential), so it also proves HTTP/2.
        object sdk = client switch
        {
            "documents" or "project" => new TerraBaseDocumentsClient(http, new TerraBaseDocumentsClientOptions { BaseAddress = origin, ApiKey = "tb_test_key" }),
            "platform" => new TerraBasePlatformClient(http, new TerraBasePlatformClientOptions { BaseAddress = origin, AccessTokenProvider = _ => ValueTask.FromResult("user-token") }),
            _ => new TerraBaseManagementClient(http, new TerraBaseManagementClientOptions { BaseAddress = Private }),
        };
        try
        {
            await call(sdk);
        }
        catch (Exception error) when (error is HttpRequestException or TerraBaseException or TimeoutException)
        {
            // Expected: the handler fails every request after recording it.
        }

        var uri = sent?.RequestUri ?? throw new InvalidOperationException("the call sent no request");
        if (client == "management" && (sent.Version != System.Net.HttpVersion.Version20 || sent.VersionPolicy != HttpVersionPolicy.RequestVersionExact))
        {
            throw new InvalidOperationException("private-listener requests must use HTTP/2 with prior knowledge");
        }

        return (sent.Method, uri.AbsolutePath, uri.Query);
    }

    private static async Task ReadFirstAsync<T>(IAsyncEnumerable<T> events)
    {
        await using var enumerator = events.GetAsyncEnumerator();
        await enumerator.MoveNextAsync();
    }

    private static List<Route> Load()
    {
        var path = Path.Combine(AppContext.BaseDirectory, "contracts", "terrabase", "routes.json");
        using var manifest = JsonDocument.Parse(File.ReadAllText(path));
        return [.. manifest.RootElement.GetProperty("operations").EnumerateArray().Select(entry => new Route(
            entry.GetProperty("client").GetString()!, entry.GetProperty("operation").GetString()!,
            entry.GetProperty("method").GetString()!, entry.GetProperty("path").GetString()!))];
    }

    private sealed record Route(string Client, string Operation, string Method, string Path)
    {
        // A {placeholder} is one escaped path segment.
        public bool Matches(string path) =>
            Regex.IsMatch(path, "^" + Regex.Replace(Regex.Escape(this.Path), @"\\\{[a-z]+}", "[^/]+") + "$", RegexOptions.None, TimeSpan.FromSeconds(1));
    }
}
