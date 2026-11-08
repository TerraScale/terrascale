using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using TerraScale.DotNet.TerraBase;

namespace TerraScale.DotNet.Tests.TerraBase;

/// <summary>
/// Every client operation sends exactly the method and route that <c>contracts/terrabase/routes.json</c> records, and every
/// recorded route is used. The monorepo checks that the TerraBase built from the same commit serves each recorded route,
/// so a drift on either side fails a test. No TerraBase runs here: a recording handler returns a permanent refusal.
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
        var used = new HashSet<string>(StringComparer.Ordinal);
        foreach (var (client, operation, call) in Calls())
        {
            var sent = await RecordAsync(client, call);
            var route = routes.SingleOrDefault(route => route.Client == client && route.Method == sent.Method.Method && route.Matches(sent.Path));
            await Assert.That(route?.Operation).IsEqualTo(operation).Because($"{sent.Method} {sent.Path}");
            used.Add(operation);
        }

        await Assert.That(routes.Select(route => route.Operation).Except(used)).IsEmpty();
    }

    private static IEnumerable<(string Client, string Operation, Func<object, Task> Call)> Calls()
    {
        static TerraBaseDatabase Db(object client) => ((TerraBaseDatabaseClient)client).Database(Project, "app");
        static TerraBaseProjectClient ProjectClient(object client) => ((TerraBaseDatabaseClient)client).Project(Project);
        static TerraBaseManagementClient M(object client) => (TerraBaseManagementClient)client;
        yield return ("database", "invokeFunction", client => Db(client).InvokeFunctionAsync("hello"));
        yield return ("database", "listDatabaseLogs", client => Db(client).ListRequestLogsAsync());
        yield return ("database", "listFunctionLogs", client => Db(client).ListFunctionLogsAsync("hello"));
        yield return ("database", "listFunctions", client => Db(client).ListFunctionsAsync());
        yield return ("database", "deployFunction", client => Db(client).DeployFunctionAsync("hello", new("function handler(event) { return event; }")));
        yield return ("database", "getFunction", client => Db(client).GetFunctionAsync("hello"));
        yield return ("database", "deleteFunction", client => Db(client).DeleteFunctionAsync("hello"));
        yield return ("database", "createDatabase", client => Db(client).CreateAsync());
        yield return ("database", "deleteDatabase", client => Db(client).DeleteAsync());
        yield return ("database", "cancelClosure", client => Db(client).CancelClosureAsync(User, User));
        yield return ("database", "getDatabaseUsage", client => Db(client).GetUsageAsync());
        yield return ("database", "branchDatabase", client => Db(client).BranchAsync("copy"));
        yield return ("database", "branchDatabaseIfId", client => Db(client).BranchIfIdAsync("copy", User));
        yield return ("database", "deleteDatabaseIfId", client => Db(client).DeleteIfIdAsync(User));

        yield return ("project", "createDatabase", client => ProjectClient(client).CreateDatabaseAsync("app"));
        yield return ("project", "listDatabases", client => ProjectClient(client).ListDatabasesAsync());
        yield return ("project", "getDatabase", client => ProjectClient(client).GetDatabaseAsync("app"));
        yield return ("project", "deleteDatabase", client => ProjectClient(client).DeleteDatabaseAsync("app"));
        yield return ("project", "updateDatabase", client => ProjectClient(client).RenameDatabaseAsync(User, "production"));
        yield return ("project", "getProjectUsage", client => ProjectClient(client).GetUsageAsync());
        yield return ("project", "getProjectMetrics", client => ProjectClient(client).GetPrometheusMetricsAsync());
        yield return ("sql", "executeSql", client => ((TerraBaseSqlClient)client).Database(Project, "app").ExecuteAsync("SELECT 1"));
        yield return ("sql", "exportDatabase", client => ((TerraBaseSqlClient)client).Database(Project, "app").ExportDatabaseAsync());
        yield return ("platform", "listPlatformDatabases", client => ((TerraBasePlatformClient)client).ListDatabasesAsync());

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

    // A permanent HTTP refusal records the route without exercising read retries.
    private static async Task<(HttpMethod Method, string Path, string Query)> RecordAsync(string client, Func<object, Task> call)
    {
        HttpRequestMessage? sent = null;
        var requests = 0;
        using var handler = new TerraBaseTestHandler((request, _) =>
        {
            sent = request;
            requests++;
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.BadRequest)
            {
                Content = new StringContent("""{"error":{"code":"recorded","message":"recorded"}}""", Encoding.UTF8, "application/json"),
            });
        });
        using var http = new HttpClient(handler);
        var origin = new Uri("https://terrabase.example.test/");
        // Management here goes to the private listener (the platform operator, no credential), so it also proves HTTP/2.
        object sdk = client switch
        {
            "database" or "project" => new TerraBaseDatabaseClient(http, new TerraBaseDatabaseClientOptions { BaseAddress = origin, ApiKey = "tb_test_key" }),
            "sql" => new TerraBaseSqlClient(http, new TerraBaseSqlClientOptions { BaseAddress = origin, AccessTokenProvider = _ => ValueTask.FromResult("tb_test_key") }),
            "platform" => new TerraBasePlatformClient(http, new TerraBasePlatformClientOptions { BaseAddress = origin, AccessTokenProvider = _ => ValueTask.FromResult("user-token") }),
            _ => new TerraBaseManagementClient(http, new TerraBaseManagementClientOptions { BaseAddress = Private }),
        };
        try
        {
            await call(sdk);
        }
        catch (Exception error) when (error is HttpRequestException or TerraBaseException or TimeoutException)
        {
            // Expected: the handler refuses every request after recording it.
        }

        await Assert.That(requests).IsEqualTo(1);
        var uri = sent?.RequestUri ?? throw new InvalidOperationException("the call sent no request");
        if (client == "management" && (sent.Version != System.Net.HttpVersion.Version20 || sent.VersionPolicy != HttpVersionPolicy.RequestVersionExact))
        {
            throw new InvalidOperationException("private-listener requests must use HTTP/2 with prior knowledge");
        }

        return (sent.Method, uri.AbsolutePath, uri.Query);
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
            Regex.IsMatch(path, "^" + Regex.Replace(Regex.Escape(this.Path), @"\\\{[a-zA-Z]+}", "[^/]+") + "$", RegexOptions.None, TimeSpan.FromSeconds(1));
    }
}
