using Cronos;
using System.Net;
using System.Diagnostics.Metrics;
using Microsoft.AspNetCore.Server.Kestrel.Core;
using TerraScale.Dashboard.Platform.Rpc;
using TerraScale.TerraBase.Rpc;
using System.Collections.Concurrent;
using System.Reflection;
using System.Runtime.ExceptionServices;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.WebUtilities;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;
using TerraScale.Dashboard.Features.Auth;
using TerraScale.Dashboard.Features.Backups;
using TerraScale.Dashboard.Features.Data;
using TerraScale.Dashboard.Features.Organizations;
using TerraScale.Dashboard.Platform.Persistence;
using TerraScale.Dashboard.Platform.TsAuth;
using TerraScale.TerraBase.Contracts;

namespace TerraScale.Dashboard.E2E
{
    // A REST producer behind the real MagicOnion services for application E2E tests. The dashboard keeps its production
    // database, SQL and ts-auth adapters. Customer SDK callers reach the same producer over public REST. Data routes follow I2 contracts/terrabase (canonical /v1/projects lifecycle by
    // database name, exchanged action-scoped project bearers); project keys follow I1 contracts/auth at the
    // global ts-auth origin only. No customer key reaches TerraBase and no key is stored by the dashboard. Management routes
    // remain only for workspace/backup callers not yet moved. This fixture does not qualify a live deployment.
    public sealed class DatabaseSdkScenario : IAsyncDisposable
    {
        private WebApplication? producer;
        private readonly string peersFile = Path.Combine(Path.GetTempPath(), "dashboard-rpc-peers-" + Guid.NewGuid().ToString("N"));
        private WebApplication? first;
        private WebApplication? second;
        private ProducerTransport? transport;
        private readonly DatabaseProducer protocol = new();
        private RecordingState? state;
        public Uri Origin { get; private set; } = null!;
        public Uri ReplicaOrigin { get; private set; } = null!;
        public Uri ProviderOrigin { get; private set; } = null!;

        // ts-auth's registry, for callers that act outside the dashboard (the CLI's project and database creates).
        public TestWorkspace Workspace => protocol.Workspace;

        // A live session of a signed-in principal of the test auth registry.
        public SessionRecord Session(string userId, string email)
        {
            return new(userId, email, email, "test-access-" + email, null, DateTimeOffset.UtcNow.AddHours(1));
        }

        public static async Task<DatabaseSdkScenario> StartAsync(string root, Action<WebApplicationBuilder>? configureScenario = null,
            Func<HttpContext, Task<IResult?>>? producerExtension = null, Action<WebApplication>? configureApp = null)
        {
            var scenario = new DatabaseSdkScenario();
            scenario.protocol.Extension = producerExtension;
            try { await scenario.InitializeAsync(root, configureScenario, configureApp); return scenario; }
            catch { await scenario.DisposeAsync(); throw; }
        }

        // Existing SQL journeys count submitted statements separately from the editor's exact metadata read.
        public static async Task<IResult?> EmptySqlCatalogAsync(HttpContext http)
        {
            http.Request.EnableBuffering();
            using var body = await JsonDocument.ParseAsync(http.Request.Body, cancellationToken: http.RequestAborted);
            http.Request.Body.Position = 0;
            var statements = body.RootElement.GetProperty("statements");
            return statements.GetArrayLength() == 1 && statements[0].GetProperty("sql").GetString() ==
                "SELECT name FROM sqlite_master WHERE type='table' ORDER BY name LIMIT 10000"
                ? Results.Json(new { results = new[] { new { columns = new[] { new { name = "name", decltype = (string?)null } }, rows = Array.Empty<object>(), rows_affected = 0 } } })
                : null;
        }

        public HttpClient CreateClient(bool replica = false)
        {
            return Client(replica ? ReplicaOrigin : Origin);
        }

        public HttpClient CreateProviderClient()
        {
            var client = new HttpClient(new H2cTestTransport()) { BaseAddress = ProviderOrigin, Timeout = TimeSpan.FromSeconds(30) };
            client.DefaultRequestVersion = HttpVersion.Version20;
            client.DefaultVersionPolicy = HttpVersionPolicy.RequestVersionExact;
            return client;
        }

        public void AddSqlViewer(string organizationId, string userId, string email)
        {
            lock (protocol.Workspace.Gate)
            {
                protocol.Workspace.Organizations[organizationId].Members[userId] = new()
                { UserId = userId, Email = email, Name = "SQL viewer", Role = "viewer", Status = "active" };
            }
        }

        private static HttpClient Client(Uri origin)
        {
            return new(new HttpClientHandler { ServerCertificateCustomValidationCallback = TestHttps.Validate, AllowAutoRedirect = false, CookieContainer = new() })
            { BaseAddress = origin, Timeout = TimeSpan.FromSeconds(30) };
        }

        private async Task InitializeAsync(string root, Action<WebApplicationBuilder>? configureScenario, Action<WebApplication>? configureApp)
        {
            var builder = WebApplication.CreateBuilder();
            builder.WebHost.ConfigureKestrel(options => options.Listen(IPAddress.Any, 0, listener => listener.Protocols = HttpProtocols.Http2));
            builder.Logging.ClearProviders();
            builder.Services.AddTerraBaseRestRpc(static (_, _) => { });
            producer = builder.Build();
            producer.UseTerraBaseRestPipeline();
            producer.UseRouting();
            producer.MapTerraBaseRestRpc();
            producer.Use(async (http, next) =>
            {
                if (http.Request.Host.Host.StartsWith("127.", StringComparison.Ordinal) && !http.Request.Path.StartsWithSegments("/__test"))
                {
                    var region = http.Request.Host.Host switch
                    {
                        "127.0.0.1" => "gru",
                        "127.0.0.2" => "sfo",
                        "127.0.0.3" => "sjc",
                        "127.0.0.4" => "fra",
                        _ => ""
                    };
                    http.Request.Host = new HostString(region.Length == 0 ? "api.terrascale.tech" : region + ".api.terrascale.tech");
                }
                await next(http);
            });
            producer.MapMethods("/{**path}", ["GET", "POST", "PUT", "PATCH", "DELETE"], (Func<HttpContext, Task<IResult>>)protocol.DispatchAsync);
            await producer.StartAsync();
            ProviderOrigin = new UriBuilder(Address(producer)) { Host = "127.0.0.10" }.Uri;
            await File.WriteAllTextAsync(peersFile, "gru gru 127.0.0.1\nsfo sfo 127.0.0.2\nsjc sjc 127.0.0.3\nfra fra 127.0.0.4\n");
            transport = new(ProviderOrigin);
            var environment = new DemoEnvironment();
            await environment.InitializeAsync();
            void Configure(WebApplicationBuilder application)
            {
                state ??= new((IDashboardState)application.Services.Last(item => item.ServiceType == typeof(IDashboardState)).ImplementationInstance!, environment.Workspace);
                protocol.State = state;
                protocol.Workspace = environment.Workspace;
                protocol.Platform ??= ProjectCounter.Wrap(environment.Workspace, protocol.CountProject);
                application.Services.Replace(ServiceDescriptor.Singleton(protocol.Platform));
                var auth = (TestAuth)application.Services.Last(item => item.ServiceType == typeof(IAuthGateway)).ImplementationInstance!;
                protocol.ResolveAccessToken = auth.ResolveAccessToken;
                protocol.ResolveProjectToken = token => auth.ResolveProjectToken(token) ?? protocol.ApiKeys?.ResolveMachineToken(token);
                protocol.ApiKeys ??= new TestAuthApiKeys(auth.ResolveAccessToken, protocol.KeyRole);
                transport!.ApiKeys = protocol.ApiKeys;
                application.Services.Replace(ServiceDescriptor.Singleton<IDashboardState>(state));
                application.Services.Replace(ServiceDescriptor.Singleton<IHttpClientFactory>(new ProducerFactory(transport)));
                application.Services.Replace(ServiceDescriptor.Singleton<TerraBaseRpcClient>(services => new(
                    services.GetRequiredService<IDashboardIdentity>(), ProviderOrigin, null, peersFile, services.GetRequiredService<IMeterFactory>())));
                application.Services.Replace(ServiceDescriptor.Singleton<IDatabaseBackend, DatabaseBackend>());
                application.Services.Replace(ServiceDescriptor.Singleton<IOrganizationAccess, TsOrganizationAccess>());
                configureScenario?.Invoke(application);
            }
            first = environment.CreateTestHost(root, 0, "http://127.0.0.1", Configure);
            second = environment.CreateTestHost(root, 0, "http://127.0.0.1", Configure);
            configureApp?.Invoke(first);
            configureApp?.Invoke(second);
            protocol.ApiKeys!.StepUp = first.Services.GetRequiredService<TestStepUpGateway>();
            await first.StartAsync();
            await second.StartAsync();
            Origin = Address(first);
            ReplicaOrigin = Address(second);
        }

        private static Uri Address(WebApplication app)
        {
            return new(app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!
            .Addresses.Single().Replace("0.0.0.0", "127.0.0.1", StringComparison.Ordinal));
        }

        public async ValueTask DisposeAsync()
        {
            protocol.Release();
            if (second is not null) { await second.StopAsync(); await second.DisposeAsync(); second = null; }
            if (first is not null) { await first.StopAsync(); await first.DisposeAsync(); first = null; }
            if (producer is not null) { await producer.StopAsync(); await producer.DisposeAsync(); producer = null; }
            transport?.Dispose();
            protocol.DisposeSqlEngines();
            File.Delete(peersFile);
        }

        private sealed class ProducerFactory(ProducerTransport transport) : IHttpClientFactory
        {
            public HttpClient CreateClient(string name)
            {
                return new(transport, disposeHandler: false);
            }
        }

        private sealed class ProducerTransport(Uri origin) : DelegatingHandler(new HttpClientHandler { ServerCertificateCustomValidationCallback = TestHttps.Validate, AllowAutoRedirect = false })
        {
            public TestAuthApiKeys? ApiKeys { get; set; }
            protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            {
                request.Version = HttpVersion.Version20;
                request.VersionPolicy = HttpVersionPolicy.RequestVersionExact;
                var uri = request.RequestUri!;
                if (uri.Host == "auth.terrascale.tech")
                {
                    return ApiKeys!.SendAsync(request, cancellationToken);
                }

                if (uri.Scheme != "https" || !uri.IsDefaultPort || uri.UserInfo.Length != 0 || uri.Host is not
                    ("api.terrascale.tech" or "gru.api.terrascale.tech" or "sfo.api.terrascale.tech" or "sjc.api.terrascale.tech" or "fra.api.terrascale.tech"))
                {
                    throw new HttpRequestException("Unexpected outbound origin in the test-only database producer.");
                }

                request.Headers.Host = uri.Authority;
                request.RequestUri = new(origin, uri.PathAndQuery);
                return base.SendAsync(request, cancellationToken);
            }
        }

        // Observes the canonical ts-auth project lifecycle of this fixture's TestWorkspace. The fake completes synchronously,
        // so each outcome is known when the call returns or throws.
        public class ProjectCounter : DispatchProxy
        {
            private ITsAuthPlatform inner = null!;
            private Action<string, int> count = null!;
            internal static ITsAuthPlatform Wrap(ITsAuthPlatform inner, Action<string, int> count)
            {
                var proxy = Create<ITsAuthPlatform, ProjectCounter>();
                ((ProjectCounter)(object)proxy).inner = inner;
                ((ProjectCounter)(object)proxy).count = count;
                return proxy;
            }
            protected override object? Invoke(MethodInfo? method, object?[]? args)
            {
                try
                {
                    var result = method!.Invoke(inner, args);
                    count(method.Name, 0);
                    return result;
                }
                catch (TargetInvocationException error) when (error.InnerException is { } thrown)
                {
                    count(method!.Name, (thrown as AccountAccessException)?.StatusCode ?? 500);
                    ExceptionDispatchInfo.Throw(thrown);
                    throw;
                }
            }
        }

        private sealed class RecordingState(IDashboardState inner, TestWorkspace workspace) : IDashboardState
        {
            // The encrypted data-key registry is gone; recording its former table shows nothing writes customer keys again.
            private const string Credentials = "dashboard_data_credentials";
            private readonly ConcurrentQueue<string> encrypted = new();
            private int credentialReads;
            private TaskCompletionSource? quotaGate;
            private int pausedQuotaCommits;
            private int quotaCasFailures;
            private string targetProjectOrg = "";
            private string targetProjectName = "";
            private int projectMetadataArmed;
            private int failedProjectMetadataWrites;
            private string targetDeletionOrg = "";
            private string targetDeletionProject = "";
            private int projectDeletionMetadataArmed;
            private int failedProjectDeletionMetadataWrites;
            private string targetDatabaseOrg = "";
            private string targetDatabaseProject = "";
            private string targetDatabaseName = "";
            private int databaseMetadataArmed;
            private int failedDatabaseMetadataWrites;
            public bool ConflictCredentials { get; set; }
            public string[] Encrypted => encrypted.ToArray();
            public int CredentialReads => Volatile.Read(ref credentialReads);
            public int PausedQuotaCommits => Volatile.Read(ref pausedQuotaCommits);
            public int QuotaCasFailures => Volatile.Read(ref quotaCasFailures);
            public int FailedProjectMetadataWrites => Volatile.Read(ref failedProjectMetadataWrites);
            public int FailedProjectDeletionMetadataWrites => Volatile.Read(ref failedProjectDeletionMetadataWrites);
            public int FailedDatabaseMetadataWrites => Volatile.Read(ref failedDatabaseMetadataWrites);
            public void ArmDatabaseMetadataFailure(string organizationId, string projectId, string name)
            {
                targetDatabaseOrg = organizationId; targetDatabaseProject = projectId; targetDatabaseName = name;
                Volatile.Write(ref databaseMetadataArmed, 1);
            }
            public void ArmProjectMetadataFailure(string organizationId, string name)
            {
                targetProjectOrg = organizationId; targetProjectName = name;
                Volatile.Write(ref projectMetadataArmed, 1);
            }
            public void ArmProjectDeletionMetadataFailure(string organizationId, string projectId)
            {
                targetDeletionOrg = organizationId; targetDeletionProject = projectId;
                Volatile.Write(ref projectDeletionMetadataArmed, 1);
            }
            public void PauseQuotaCommits()
            {
                Interlocked.Exchange(ref pausedQuotaCommits, 0);
                quotaGate = new(TaskCreationOptions.RunContinuationsAsynchronously);
            }
            public void ReleaseQuotaCommits() { quotaGate?.TrySetResult(); quotaGate = null; }
            public Task InitializeAsync(CancellationToken ct)
            {
                return inner.InitializeAsync(ct);
            }

            public Task CheckReadyAsync(CancellationToken ct)
            {
                return inner.CheckReadyAsync(ct);
            }

            public async Task<StateRecord?> GetAsync(string table, string id, CancellationToken ct)
            {
                var value = await inner.GetAsync(table, id, ct);
                if (table != Credentials)
                {
                    return value;
                }

                Interlocked.Increment(ref credentialReads);
                return ConflictCredentials && value is not null ? throw new IOException("Synthetic credential read conflict.") : value;
            }
            public async Task PutAsync(string table, string id, JsonElement row, StatePrecondition condition, CancellationToken ct)
            {
                var databaseCommit = table == AccountService.OrganizationsTable &&
                    row.GetProperty("Projects").EnumerateObject().Any(project => project.Value.GetProperty("Databases").EnumerateObject().Any());
                if (databaseCommit && quotaGate is { } paused)
                {
                    Interlocked.Increment(ref pausedQuotaCommits);
                    await paused.Task.WaitAsync(ct);
                }
                if (table == AccountService.OrganizationsTable && id == targetDatabaseOrg &&
                    Volatile.Read(ref databaseMetadataArmed) == 1 && row.GetProperty("Projects").TryGetProperty(targetDatabaseProject, out var databaseProject) &&
                    databaseProject.GetProperty("Databases").EnumerateObject().Any(database => database.Value.GetProperty("Name").GetString() == targetDatabaseName))
                {
                    var prior = await inner.GetAsync(table, id, ct);
                    if (prior is not null && prior.Body.GetProperty("Projects").TryGetProperty(targetDatabaseProject, out var priorProject) &&
                        !priorProject.GetProperty("Databases").EnumerateObject().Any(database => database.Value.GetProperty("Name").GetString() == targetDatabaseName) &&
                        Interlocked.CompareExchange(ref databaseMetadataArmed, 0, 1) == 1)
                    {
                        Interlocked.Increment(ref failedDatabaseMetadataWrites);
                        throw new IOException("Test-only database metadata write interrupted.");
                    }
                }
                // Stored project settings carry no name; ts-auth's canonical project with the armed name identifies the target.
                if (table == AccountService.OrganizationsTable && id == targetProjectOrg &&
                    Volatile.Read(ref projectMetadataArmed) == 1 && CanonicalProjectId(targetProjectOrg, targetProjectName) is { } targetProject &&
                    row.GetProperty("Projects").TryGetProperty(targetProject, out _))
                {
                    var prior = await inner.GetAsync(table, id, ct);
                    if (prior is not null && !prior.Body.GetProperty("Projects").TryGetProperty(targetProject, out _) &&
                        Interlocked.CompareExchange(ref projectMetadataArmed, 0, 1) == 1)
                    {
                        Interlocked.Increment(ref failedProjectMetadataWrites);
                        throw new IOException("Test-only project metadata write interrupted.");
                    }
                }
                if (table == AccountService.OrganizationsTable && id == targetDeletionOrg &&
                    Volatile.Read(ref projectDeletionMetadataArmed) == 1 && !row.GetProperty("Projects").TryGetProperty(targetDeletionProject, out _))
                {
                    var prior = await inner.GetAsync(table, id, ct);
                    if (prior is not null && prior.Body.GetProperty("Projects").TryGetProperty(targetDeletionProject, out var project) &&
                        project.GetProperty("Id").GetString() == targetDeletionProject &&
                        Interlocked.CompareExchange(ref projectDeletionMetadataArmed, 0, 1) == 1)
                    {
                        Interlocked.Increment(ref failedProjectDeletionMetadataWrites);
                        throw new IOException("Test-only project deletion metadata write interrupted.");
                    }
                }
                try { await inner.PutAsync(table, id, row, condition, ct); }
                catch (StateConflictException) when (databaseCommit)
                { Interlocked.Increment(ref quotaCasFailures); throw; }
                if (table == Credentials)
                {
                    encrypted.Enqueue(row.GetRawText());
                }
            }
            public Task<IReadOnlyList<StateRecord>> ListAsync(string table, CancellationToken ct)
            {
                return inner.ListAsync(table, ct);
            }

            public Task<StatePage> PageAsync(string table, IReadOnlyDictionary<string, string> scope, IReadOnlyList<string>? fields, int limit, string? cursor, CancellationToken ct)
            {
                return inner.PageAsync(table, scope, fields, limit, cursor, ct);
            }

            public Task DeleteAsync(string table, string id, string version, CancellationToken ct)
            {
                return inner.DeleteAsync(table, id, version, ct);
            }

            private string? CanonicalProjectId(string organizationId, string name)
            {
                lock (workspace.Gate)
                {
                    return workspace.Organizations.GetValueOrDefault(organizationId)?.Projects.Values.SingleOrDefault(project => project.Name == name)?.Id;
                }
            }
        }

        private sealed class DatabaseProducer
        {
            // The current producer creates canonical ULIDs. Keep those actual IDs
            // on both SDK boundaries instead of converting fixture UUIDs.
            private readonly TerraScaleIdGenerator ids = new();
            private readonly object gate = new();
            private readonly Dictionary<string, ManagedDatabase> databases = [];
            private readonly Dictionary<string, ProducerSnapshot> snapshots = [];
            private readonly Dictionary<string, ProducerBackupPolicy> policies = [];
            private readonly Dictionary<string, BackupDestinationView> destinations = [];
            private string backupFailure = "";
            // A TerraBase conflict code every database create answers with, or empty.
            private string createConflict = "";
            private int backupLists;
            private int backupCreates;
            private int backupRestores;
            private int policyWrites;
            private int projectCreates;
            private int projectConflicts;
            private int projectDeletes;
            private int projectDeleteNotFound;
            private readonly ConcurrentDictionary<string, TestSqlEngine> sqlEngines = new(StringComparer.Ordinal);
            private readonly ConcurrentQueue<RequestObservation> requests = new();
            private readonly ConcurrentQueue<string> failures = new();
            private TaskCompletionSource? createGate;
            private int pausedCreates;
            private int creates;
            // As production TerraBase's 503 catalog_anchor_unavailable: this many next placement and delete writes commit and still answer 503.
            private int anchorDeleteFailures;
            private int branchCreates;
            private int branchConflicts;
            public RecordingState State { get; set; } = null!;
            // ts-auth's canonical organizations, projects and memberships for this fixture; the producer stores no identity.
            public TestWorkspace Workspace { get; set; } = null!;
            public ITsAuthPlatform? Platform { get; set; }
            public Func<string, (string UserId, string Email)?> ResolveAccessToken { get; set; } = null!;
            public Func<string, (string UserId, string OrganizationId, string ProjectId, IReadOnlyList<string> Scopes)?> ResolveProjectToken { get; set; } = null!;
            public TestAuthApiKeys? ApiKeys { get; set; }
            // ts-auth's current project role; the role control is ts-auth's own override for current members ("" removes them).
            public string KeyRole(string userId, string organizationId, string projectId)
            {
                return Workspace.ProjectRole(userId, organizationId, projectId);
            }

            private bool CanonicalProject(string organizationId, string projectId)
            {
                lock (Workspace.Gate)
                {
                    return Workspace.Organizations.GetValueOrDefault(organizationId)?.Projects.ContainsKey(projectId) == true;
                }
            }
            // The stored dashboard record omits ts-auth identity, so ownership comes from the fixture's ts-auth registry.
            private string Owner(string organizationId)
            {
                lock (Workspace.Gate)
                {
                    return Workspace.Organizations.GetValueOrDefault(organizationId) is { } org && org.RoleFor(org.OwnerUserId) == "owner" ? org.OwnerUserId : "";
                }
            }
            public void CountProject(string operation, int status)
            {
                switch (operation, status)
                {
                    case ("CreateProjectAsync", 0): Interlocked.Increment(ref projectCreates); break;
                    case ("CreateProjectAsync", 409): Interlocked.Increment(ref projectConflicts); break;
                    case ("DeleteProjectAsync", 0): Interlocked.Increment(ref projectDeletes); break;
                    case ("DeleteProjectAsync", 404): Interlocked.Increment(ref projectDeleteNotFound); break;
                }
            }
            public Func<HttpContext, Task<IResult?>>? Extension { get; set; }
            // The region reported by the public TerraBase origin.
            public string ApiRegion { get; set; } = "gru";
            // TerraBase's /v1/regions catalog; unset, it names only the answering region, as /healthz does.
            public string[]? Catalog { get; set; }

            public void Release() { ApiKeys?.ReleaseLists(); createGate?.TrySetResult(); State?.ReleaseQuotaCommits(); }

            // Each database's relational tables live in its own SQL engine for the scenario's lifetime.
            private TestSqlEngine SqlEngine(string databaseId)
            {
                return sqlEngines.GetOrAdd(databaseId, _ => new TestSqlEngine());
            }

            public void DisposeSqlEngines()
            {
                foreach (var engine in sqlEngines.Values)
                {
                    engine.Dispose();
                }

                sqlEngines.Clear();
            }

            public async Task<IResult> DispatchAsync(HttpContext http)
            {
                try
                {
                    var path = http.Request.Path.Value!;
                    if (path.StartsWith("/v1/projects/", StringComparison.Ordinal) &&
                        (http.Request.Method == "GET" && (path.EndsWith("/replication", StringComparison.Ordinal) || path.EndsWith("/analytics", StringComparison.Ordinal) || path.EndsWith("/point-in-time", StringComparison.Ordinal)) ||
                         http.Request.Method == "POST" && path.Contains("/point-in-time/", StringComparison.Ordinal)))
                    {
                        var parts = path.Split('/', StringSplitOptions.RemoveEmptyEntries);
                        var projectId = parts.Length >= 4 ? parts[2] : "";
                        var text = http.Request.Headers.Authorization.ToString();
                        var grant = text.StartsWith("Bearer ", StringComparison.Ordinal) ? ResolveProjectToken(text[7..]) : null;
                        Require(projectId.Length > 0 && grant is not null && grant.Value.ProjectId == projectId,
                            "Project reporting uses an exchanged project-scoped bearer for the selected project");
                        Require(CanonicalProject(grant!.Value.OrganizationId, projectId), "Project replication bearer organization matches the canonical project");
                        if (path.EndsWith("/point-in-time", StringComparison.Ordinal))
                        {
                            Require(grant.Value.Scopes.SequenceEqual(new[] { "databases:read" }), "PITR window uses only database read authority");
                        }

                        if (http.Request.Method == "POST")
                        {
                            var expected = path.EndsWith("/plan", StringComparison.Ordinal) ? new[] { "databases:read" } : new[] { "databases:write" };
                            Require(grant.Value.Scopes.Order(StringComparer.Ordinal).SequenceEqual(expected), "Point-in-time recovery uses the exact action-scoped project grant");
                        }
                        lock (gate)
                        {
                            var databaseId = http.Request.Query["database"].ToString();
                            Require(databaseId.Length == 0 || databases.TryGetValue(databaseId, out var database) && database.ProjectId == projectId,
                                "Project replication database filter is inside the bearer project");
                        }
                    }
                    if (Extension is not null && await Extension(http) is { } extensionResult)
                    {
                        return extensionResult;
                    }

                    if (path == "/__test/control")
                    {
                        return await ControlAsync(http);
                    }

                    if (path == "/__test/observations")
                    {
                        return Observations();
                    }

                    if (path.Split('/') is ["", "admin", "organizations" or "projects", var planOwner, "plan"])
                    {
                        Require(http.Request.Method == "PUT" && http.Request.Headers.Authorization.Count == 0 &&
                            http.Request.Headers[TerraScale.TerraBase.Contracts.TerraBaseRpc.PeerCredentialHeader] == TestIdentity.PeerSecret,
                            "Only the credentialed operator publishes platform plans.");
                        using var body = await JsonDocument.ParseAsync(http.Request.Body, cancellationToken: http.RequestAborted);
                        var plan = body.RootElement.GetProperty("plan").GetString();
                        Require(plan is "hobby" or "starter" or "pro" or "team" or "enterprise", "A platform publish carries a known storage plan.");
                        lock (gate)
                        {
                            var enrolled = databases.Values.Any(database => database.ProjectId == planOwner);
                            return enrolled ? Results.Json(new { plan }) : Error(404, "not_found");
                        }
                    }

                    Require(http.Request.Host.Host is "api.terrascale.tech" or "gru.api.terrascale.tech" or "sfo.api.terrascale.tech" or "sjc.api.terrascale.tech" or "fra.api.terrascale.tech", "Trusted producer host");
                    // terrascale e30ffdc2 (region-identity.md): every answer names its region; api.terrascale.tech answers in ApiRegion.
                    var answering = http.Request.Host.Host switch
                    {
                        "gru.api.terrascale.tech" => "gru",
                        "sfo.api.terrascale.tech" => "sfo",
                        "sjc.api.terrascale.tech" => "sjc",
                        "fra.api.terrascale.tech" => "fra",
                        _ => ApiRegion
                    };
                    http.Response.Headers["X-TerraBase-Region"] = answering;
                    if (http.Request.Headers.TryGetValue("X-TerraScale-Region-Pin", out var pin) && (pin.Count != 1 || pin[0] != answering))
                    {
                        return Error(421, "misdirected_request");
                    }

                    if (path == "/healthz")
                    {
                        return Results.Json(new { status = "ok", version = "test", region = answering, storageFormat = "test", revision = (string?)null, machine = "local" });
                    }

                    if (path == "/v1/regions")
                    {
                        Require(http.Request.Method == "GET" && http.Request.Headers.Authorization.Count == 0, "The region catalog is read anonymously");
                        return Results.Json(new { regions = Catalog ?? [answering] });
                    }
                    var token = http.Request.Headers.Authorization.ToString();
                    if (path.StartsWith("/v1/management/", StringComparison.Ordinal))
                    {
                        var admitted = token.StartsWith("Bearer ", StringComparison.Ordinal) ? ResolveAccessToken(token[7..]) : null;
                        Require(admitted is not null, "Management uses admitted user access token");
                        Require(TerraScaleIds.IsValid(admitted!.Value.UserId), "The current producer accepts canonical ULID bearer subjects");
                        // Organization and project identity left management for ts-auth; database routes are canonical project routes.
                        throw new InvalidOperationException("Unsupported management protocol route");
                    }
                    if (path.StartsWith("/v1/projects/", StringComparison.Ordinal))
                    {
                        // Analytics shares the replication reporting envelope; both answer with no measurements by default.
                        if (http.Request.Method == "GET" && (path.EndsWith("/replication", StringComparison.Ordinal) || path.EndsWith("/analytics", StringComparison.Ordinal)))
                        {
                            var parts = path.Split('/', StringSplitOptions.RemoveEmptyEntries);
                            Require(parts.Length == 4 && parts[3] is "replication" or "analytics", "Canonical project reporting route");
                            var range = http.Request.Query["range"].ToString();
                            var (bucketSeconds, durationSeconds) = range switch
                            {
                                "1h" => (60, 3600),
                                "24h" => (3600, 86400),
                                "7d" => (21600, 604800),
                                "30d" => (86400, 2592000),
                                _ => throw new InvalidOperationException("Supported replication reporting range")
                            };
                            var now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
                            var to = DateTimeOffset.FromUnixTimeSeconds((now / bucketSeconds + 1) * bucketSeconds);
                            return Results.Json(new { projectId = parts[2], range, from = to.AddSeconds(-durationSeconds), to, bucketSeconds, items = Array.Empty<object>() });
                        }
                        var route = path.Split('/', StringSplitOptions.RemoveEmptyEntries);
                        if (route is [_, _, _, "backup-destination"])
                        {
                            return await DestinationAsync(http, route[2]);
                        }

                        return await CanonicalAsync(http);
                    }
                    throw new InvalidOperationException("Unexpected producer route: " + http.Request.Method + " " + path);
                }
                catch (Exception error) when (!http.Response.HasStarted && error is InvalidOperationException or JsonException)
                {
                    failures.Enqueue(error.Message);
                    return Error(500, "test_contract_failure");
                }
            }

            private async Task<IResult> ControlAsync(HttpContext http)
            {
                var body = await JsonAsync(http);
                switch (body.GetProperty("command").GetString())
                {
                    case "pause-key-list": ApiKeys!.PauseLists(); break;
                    case "release-key-list": ApiKeys!.ReleaseLists(); break;
                    case "pause-database-creates": Interlocked.Exchange(ref pausedCreates, 0); createGate = new(TaskCreationOptions.RunContinuationsAsynchronously); break;
                    case "release-database-creates": createGate?.TrySetResult(); createGate = null; break;
                    case "pause-quota-commits": State.PauseQuotaCommits(); break;
                    case "release-quota-commits": State.ReleaseQuotaCommits(); break;
                    case "credential-conflict": State.ConflictCredentials = body.GetProperty("value").GetBoolean(); break;
                    case "role": Workspace.Role = body.GetProperty("value").GetString()!; break;
                    case "backup-failure": backupFailure = body.GetProperty("value").GetString()!; break;
                    case "create-conflict": createConflict = body.GetProperty("value").GetString()!; break;
                    // Another TerraBase client placed a replica: the producer's placement is wider than the dashboard asked for.
                    case "api-region": ApiRegion = body.GetProperty("value").GetString()!; break;
                    case "catalog": Catalog = [.. body.GetProperty("value").EnumerateArray().Select(value => value.GetString()!)]; break;
                    case "anchor-unavailable":
                        Volatile.Write(ref anchorDeleteFailures, body.GetProperty("delete").GetInt32());
                        break;
                    // Another TerraBase client (the CLI's ts db create, an SDK) created or deleted a database in a project.
                    case "external-create":
                        lock (gate)
                        {
                            var external = new ManagedDatabase(ids.NewId(), body.GetProperty("projectId").GetString()!, body.GetProperty("value").GetString()!, DateTimeOffset.UtcNow,
                                HomeRegion: body.TryGetProperty("home", out var home) ? home.GetString()! : "gru",
                                ReplicaRegions: body.TryGetProperty("replicas", out var replicas) ? replicas.EnumerateArray().Select(value => value.GetString()!).ToArray() : ["sfo"]);
                            databases[external.Id] = external;
                        }
                        break;
                    case "external-delete":
                        lock (gate)
                        {
                            foreach (var removed in databases.Values.Where(value => value.ProjectId == body.GetProperty("projectId").GetString() && value.Name == body.GetProperty("value").GetString()).ToArray())
                            {
                                databases.Remove(removed.Id);
                            }
                        }

                        break;
                    // Another TerraBase client placed a replica: on every database, or on the named one.
                    case "add-replica":
                        var added = body.GetProperty("value").GetString()!;
                        var only = body.TryGetProperty("name", out var named) ? named.GetString() : null;
                        lock (gate)
                        {
                            foreach (var placed in databases.Values.Where(value => only is null || value.Name == only).ToArray())
                            {
                                databases[placed.Id] = placed with { ReplicaRegions = [.. (placed.ReplicaRegions ?? []).Where(region => region != added), added] };
                            }
                        }

                        break;
                    case "fail-database-metadata":
                    case "database-status":
                        var databaseOrgId = body.GetProperty("organizationId").GetString()!;
                        var databaseProjectId = body.GetProperty("projectId").GetString()!;
                        var databaseValue = await State.GetAsync(AccountService.OrganizationsTable, databaseOrgId, http.RequestAborted)
                            ?? throw new InvalidOperationException("Database fixture control targets a canonical organization");
                        var databaseOrg = databaseValue.Body.Deserialize<OrganizationRecord>()!;
                        databaseOrg.Projects.TryGetValue(databaseProjectId, out var fixtureProject);
                        Require(Workspace.Role == "owner" && databaseValue.Id == databaseOrg.Id && databaseOrg.Id == databaseOrgId && databaseOrg.Status == "active" &&
                            Owner(databaseOrgId).Length > 0 && fixtureProject is not null,
                            "Database fixture control targets its current owner's canonical project");
                        Require(CanonicalProject(databaseOrgId, databaseProjectId), "Database fixture control agrees with ts-auth's canonical project");
                        if (body.GetProperty("command").GetString() == "fail-database-metadata")
                        {
                            var databaseName = body.GetProperty("name").GetString()!;
                            Require(databaseName.Length > 0 && !fixtureProject!.Databases.Values.Any(database => database.Name == databaseName),
                                "Database metadata interruption targets a new name only");
                            State.ArmDatabaseMetadataFailure(databaseOrgId, databaseProjectId, databaseName);
                        }
                        else
                        {
                            var fixtureDatabaseId = body.GetProperty("databaseId").GetString()!;
                            var status = body.GetProperty("value").GetString()!;
                            fixtureProject!.Databases.TryGetValue(fixtureDatabaseId, out var fixtureDatabase);
                            Require(status is "ready" or "provisioning" or "accepted" or "pending" or "failed" && fixtureDatabase is not null,
                                "A database presentation fixture changes only an existing canonical row's recorded status");
                            lock (gate)
                            {
                                Require(databases.TryGetValue(fixtureDatabaseId, out var remoteDatabase) && remoteDatabase.ProjectId == databaseProjectId,
                                "The presentation fixture owns its actual producer database");
                            }

                            fixtureDatabase!.Status = status;
                            await State.PutAsync(AccountService.OrganizationsTable, databaseOrgId, JsonSerializer.SerializeToElement(databaseOrg),
                                StatePrecondition.Matches(databaseValue.Version), http.RequestAborted);
                        }
                        break;
                    case "fail-project-metadata":
                        var faultOrganizationId = body.GetProperty("organizationId").GetString()!;
                        var faultName = body.GetProperty("name").GetString()!.Trim();
                        var faultValue = await State.GetAsync(AccountService.OrganizationsTable, faultOrganizationId, http.RequestAborted)
                            ?? throw new InvalidOperationException("Project metadata failure targets an existing organization");
                        var faultOrg = faultValue.Body.Deserialize<OrganizationRecord>()!;
                        bool faultNameTaken;
                        lock (Workspace.Gate)
                        {
                            faultNameTaken = Workspace.Organizations.GetValueOrDefault(faultOrganizationId)?.Projects.Values.Any(project => project.Name == faultName) != false;
                        }

                        Require(TerraScaleIds.IsValid(faultOrganizationId) && faultValue.Id == faultOrg.Id && faultOrg.Id == faultOrganizationId &&
                            faultOrg.Status == "active" && Owner(faultOrganizationId).Length > 0 && faultName.Length > 0 && !faultNameTaken,
                            "Project metadata fault is limited to a new project of a current canonical owner");
                        State.ArmProjectMetadataFailure(faultOrganizationId, faultName);
                        break;
                    case "fail-project-deletion-metadata":
                        var deletionOrgId = body.GetProperty("organizationId").GetString()!;
                        var deletionProjectId = body.GetProperty("projectId").GetString()!;
                        var deletionValue = await State.GetAsync(AccountService.OrganizationsTable, deletionOrgId, http.RequestAborted)
                            ?? throw new InvalidOperationException("Project deletion metadata failure targets an existing organization");
                        var deletionOrg = deletionValue.Body.Deserialize<OrganizationRecord>()!;
                        Require(TerraScaleIds.IsValid(deletionOrgId) && TerraScaleIds.IsValid(deletionProjectId) &&
                            deletionValue.Id == deletionOrgId && deletionOrg.Id == deletionOrgId && deletionOrg.Status == "active" &&
                            Owner(deletionOrgId).Length > 0 && deletionOrg.Projects.Count > 1 &&
                            deletionOrg.Projects.TryGetValue(deletionProjectId, out var deleting) && deleting.Id == deletionProjectId &&
                            !deleting.IsDefault && deleting.Databases.Count == 0,
                            "Project deletion metadata fault is limited to an existing empty nondefault project of a current canonical owner");
                        Require(Workspace.Role == "owner" && CanonicalProject(deletionOrgId, deletionProjectId), "Project deletion metadata fault matches ts-auth's canonical project");
                        State.ArmProjectDeletionMetadataFailure(deletionOrgId, deletionProjectId);
                        break;
                    case "plan":
                        var organizationId = body.GetProperty("organizationId").GetString()!;
                        var current = await State.GetAsync(AccountService.OrganizationsTable, organizationId, http.RequestAborted)
                            ?? throw new InvalidOperationException("The fixture plan targets an existing organization");
                        var organization = current.Body.Deserialize<OrganizationRecord>()!;
                        organization.PlanId = body.GetProperty("value").GetString()!;
                        await State.PutAsync(AccountService.OrganizationsTable, organizationId, JsonSerializer.SerializeToElement(organization),
                            StatePrecondition.Matches(current.Version), http.RequestAborted);
                        break;
                    default: throw new InvalidOperationException("Unknown test producer control");
                }
                return Results.NoContent();
            }

            private IResult Observations()
            {
                object[] workspaceProjects;
                lock (Workspace.Gate)
                {
                    workspaceProjects = Workspace.Organizations.Values.SelectMany(org => org.Projects.Values)
                    .Select(value => new { value.Id, value.OrganizationId, value.Name }).ToArray();
                }

                lock (gate)
                {
                    return Results.Json(new
                    {
                        failures = failures.ToArray(),
                        requests = requests.ToArray(),
                        pausedLists = ApiKeys?.PausedLists ?? 0,
                        pausedCreates,
                        creates,
                        branchCreates,
                        branchConflicts,
                        backupLists,
                        backupCreates,
                        backupRestores,
                        policyWrites,
                        projectCreates,
                        projectConflicts,
                        projectDeletes,
                        projectDeleteNotFound,
                        failedProjectMetadataWrites = State.FailedProjectMetadataWrites,
                        failedProjectDeletionMetadataWrites = State.FailedProjectDeletionMetadataWrites,
                        failedDatabaseMetadataWrites = State.FailedDatabaseMetadataWrites,
                        workspaceProjects,
                        backups = snapshots.Values.Select(value => value.Backup).ToArray(),
                        credentials = (ApiKeys?.Issued() ?? []).Select(value => new { value.Key.Id, value.Key.Name, Secret = value.Secret, Host = "auth.terrascale.tech", value.Key.Scopes, value.Key.Database }).ToArray(),
                        authKeys = ApiKeys?.Observations(),
                        encrypted = State.Encrypted,
                        credentialReads = State.CredentialReads,
                        pausedQuotaCommits = State.PausedQuotaCommits,
                        quotaCasFailures = State.QuotaCasFailures,
                        databases = databases.Values.ToArray()
                    });
                }
            }

            // Producer project backup contract: /v1/projects/{project}/databases/{name}/backups and backup-policy
            // with an action-scoped project token, full-only policy, complete snapshots, new-database restore.
            // The test producer really copies its stored snapshot; it is not live
            // engine/storage admission or scheduler qualification.
            private async Task<IResult> BackupsAsync(HttpContext http, string[] parts, string project, ManagedDatabase source, string callerRole)
            {
                if (callerRole.Length == 0)
                {
                    return Error(404, "not_found");
                }

                if (http.Request.Method != "GET" && callerRole is not ("owner" or "admin"))
                {
                    return Error(403, "role_too_low");
                }

                if (backupFailure == "outage")
                {
                    return Error(503, "unavailable");
                }

                if (parts.Length == 6 && parts[5] == "backup-policy")
                {
                    if (http.Request.Method == "GET")
                    {
                        lock (gate)
                        {
                            return Results.Json(policies.GetValueOrDefault(source.Id) ?? new ProducerBackupPolicy("0 2 * * *", 7, false, null));
                        }
                    }
                    Require(http.Request.Method == "PUT", "Producer full policy uses PUT");
                    var body = await JsonAsync(http);
                    Require(body.EnumerateObject().Select(field => field.Name).Order(StringComparer.Ordinal)
                        .SequenceEqual(new[] { "disabled", "fullBackupCron", "retentionDays" }), "Producer accepts no invented incremental policy field");
                    var cron = body.GetProperty("fullBackupCron").GetString();
                    var days = body.GetProperty("retentionDays").GetInt32();
                    if (days is < 1 or > 35 || !string.IsNullOrWhiteSpace(cron) && !CronExpression.TryParse(cron.Trim(), CronFormat.Standard, out _))
                    {
                        return Error(400, "validation");
                    }

                    var policy = new ProducerBackupPolicy(string.IsNullOrWhiteSpace(cron) ? null : cron.Trim(), days,
                        body.GetProperty("disabled").GetBoolean(), DateTimeOffset.UtcNow);
                    lock (gate)
                    {
                        policies[source.Id] = policy;
                    }

                    Interlocked.Increment(ref policyWrites);
                    return Results.Json(policy);
                }
                if (parts.Length == 6 && parts[5] == "backups")
                {
                    if (http.Request.Method == "GET")
                    {
                        Require(http.Request.Query["limit"] == "100", "Backup inventory uses bounded producer pagination");
                        Interlocked.Increment(ref backupLists);
                        var after = http.Request.Query["after"].ToString();
                        lock (gate)
                        {
                            var items = snapshots.Values.Select(snapshot => snapshot.Backup).Where(backup => backup.DatabaseId == source.Id)
                                .OrderBy(backup => backup.CreatedAt).ThenBy(backup => backup.Id, StringComparer.Ordinal).ToArray();
                            var start = after.Length == 0 ? 0 : int.Parse(after["backup-cursor:".Length..]);
                            var next = start + 1 < items.Length ? "backup-cursor:" + (start + 1) : null;
                            return Results.Json(new { items = items.Skip(start).Take(1).ToArray(), next = backupFailure == "repeat-cursor" && after.Length > 0 ? after : next });
                        }
                    }
                    Require(http.Request.Method == "POST" && http.Request.ContentLength is null or 0, "Manual backup has no invented body");
                    var snapshot = SqlEngine(source.Id).Snapshot();
                    lock (gate)
                    {
                        var size = snapshot.Sum(statement => (long)System.Text.Encoding.UTF8.GetByteCount(statement.Sql) + statement.Args.Sum(arg => (long)System.Text.Encoding.UTF8.GetByteCount(arg ?? "")));
                        var backup = new ProducerBackup(ids.NewId(), backupFailure == "foreign-backup" ? ids.NewId() : source.Id,
                            "ready", "manual", size, DateTimeOffset.UtcNow);
                        snapshots.Add(backup.Id, new(backup, snapshot));
                        Interlocked.Increment(ref backupCreates);
                        return Results.Json(backup, statusCode: 201);
                    }
                }
                Require(parts.Length == 8 && parts[5] == "backups" && parts[7] == "restore" && http.Request.Method == "POST", "Producer restore route");
                var request = await JsonAsync(http);
                Require(request.EnumerateObject().Count() == 1 && request.TryGetProperty("name", out _), "Producer restore accepts only optional name");
                ManagedDatabase restored;
                IReadOnlyList<TestSqlStatement> replay = [];
                lock (gate)
                {
                    if (!snapshots.TryGetValue(parts[6], out var snapshot) || snapshot.Backup.DatabaseId != source.Id)
                    {
                        return Error(404, "not_found");
                    }

                    var name = request.GetProperty("name").GetString();
                    if (name is null)
                    {
                        for (var attempt = 1; ; attempt++)
                        {
                            var suffix = attempt == 1 ? "-restore" : "-restore-" + attempt;
                            name = source.Name[..Math.Min(source.Name.Length, 63 - suffix.Length)] + suffix;
                            if (!databases.Values.Any(database => database.ProjectId == project && database.Name == name))
                            {
                                break;
                            }
                        }
                    }
                    if (databases.Values.Any(database => database.ProjectId == project && database.Name == name))
                    {
                        return Error(409, "name_taken");
                    }

                    Interlocked.Increment(ref backupRestores);
                    restored = new ManagedDatabase(ids.NewId(), project, name, DateTimeOffset.UtcNow, source.Id,
                        http.Request.Host.Host == "gru.api.terrascale.tech" ? "gru" : "sfo", null);
                    if (backupFailure == "wrong-project")
                    {
                        restored = restored with { ProjectId = ids.NewId() };
                    }

                    if (backupFailure == "same-id")
                    {
                        restored = restored with { Id = source.Id };
                    }

                    if (backupFailure == "wrong-source")
                    {
                        restored = restored with { BranchedFrom = ids.NewId() };
                    }

                    if (backupFailure == "wrong-name")
                    {
                        restored = restored with { Name = source.Name };
                    }

                    if (backupFailure == "invalid-name")
                    {
                        restored = restored with { Name = "INVALID" };
                    }

                    if (backupFailure is not ("phantom" or "same-id" or "wrong-project"))
                    {
                        databases.Add(restored.Id, restored);
                        if (backupFailure != "wrong-name")
                        {
                            replay = snapshot.Statements;
                        }
                    }
                }
                if (replay.Count > 0)
                {
                    await SqlEngine(restored.Id).ExecuteAsync(replay, http.RequestAborted);
                }

                return Results.Json(restored, statusCode: 201);
            }

            // I2 canonical project lifecycle and database operations. {database} is the name; each operation demands the exact
            // action scope its SDK method requires (databases:* lifecycle and backups, sql:read for SQL).
            private async Task<IResult> CanonicalAsync(HttpContext http)
            {
                var parts = http.Request.Path.Value!.Split('/', StringSplitOptions.RemoveEmptyEntries).Select(Uri.UnescapeDataString).ToArray();
                Require(parts.Length >= 4 && parts[3] == "databases", "Canonical project database route");
                var project = parts[2];
                var method = http.Request.Method;
                var answering = http.Response.Headers["X-TerraBase-Region"].ToString();
                var scope = parts switch
                {
                    [_, _, _, _] or [_, _, _, _, _] when method == "GET" => "databases:read",
                    [_, _, _, _, _, "sql", "execute"] => "sql:read",
                    [_, _, _, _, _, "backups" or "backup-policy"] when method == "GET" => "databases:read",
                    _ => "databases:write"
                };
                if (Authorize(http, project, scope) is { } denied)
                {
                    return denied;
                }

                var callerRole = (string)http.Items["producer-role"]!;
                if (parts.Length == 4 && method == "GET")
                {
                    var query = http.Request.Query;
                    Require(query.Keys.All(key => key is "after" or "limit"), "Database listing accepts only after and limit");
                    var limit = query.ContainsKey("limit") ? int.Parse(query["limit"].ToString(), System.Globalization.CultureInfo.InvariantCulture) : 20;
                    if (limit is < 1 or > 100)
                    {
                        return Error(400, "validation");
                    }

                    var after = query["after"].ToString();
                    lock (gate)
                    {
                        var owned = databases.Values.Where(value => value.ProjectId == project && (value.AllRegions ||
                            value.HomeRegion == answering || value.ReplicaRegions?.Contains(answering, StringComparer.Ordinal) == true))
                            .OrderBy(value => value.Id, StringComparer.Ordinal).ToArray();
                        if (after.Length > 0 && !owned.Any(value => value.Id == after))
                        {
                            return Error(400, "validation");
                        }

                        var page = owned.Where(value => string.CompareOrdinal(value.Id, after) > 0).Take(limit + 1).ToArray();
                        return Results.Json(new { items = page.Take(limit).Select(value => Wire(value, answering)).ToArray(), next = page.Length > limit ? page[limit - 1].Id : null });
                    }
                }
                if (parts.Length == 4)
                {
                    Require(method == "POST", "Canonical database create");
                    var body = await JsonAsync(http);
                    Require(body.EnumerateObject().All(field => field.Name is "name" or "homeRegion" or "replicaRegions" or "allRegions" or "ttlSeconds"), "Create sends only name and placement");
                    // As production TerraBase until replicas are available: a database serves only its home region.
                    if (body.TryGetProperty("replicaRegions", out _) || body.TryGetProperty("allRegions", out var every) && every.GetBoolean())
                    {
                        return Error(400, "replicas_unavailable");
                    }

                    var name = body.GetProperty("name").GetString()!;
                    var home = body.TryGetProperty("homeRegion", out var named) ? named.GetString()! : answering;
                    string[] replicas = [];
                    if (home != answering)
                    {
                        return WrongRegion(http, new(ids.NewId(), project, name, DateTimeOffset.UtcNow, HomeRegion: home, ReplicaRegions: replicas));
                    }

                    if (createGate is { } paused) { Interlocked.Increment(ref pausedCreates); await paused.Task.WaitAsync(http.RequestAborted); }
                    lock (gate)
                    {
                        if (databases.Values.Any(value => value.ProjectId == project && value.Name == name))
                        {
                            return Error(409, "name_taken");
                        }

                        if (createConflict.Length > 0)
                        {
                            return Error(409, createConflict);
                        }

                        var database = new ManagedDatabase(ids.NewId(), project, name, DateTimeOffset.UtcNow, HomeRegion: home, ReplicaRegions: replicas) { ExpiresAt = Expiry(body) };
                        databases[database.Id] = database;
                        Interlocked.Increment(ref creates);
                        return Results.Json(Wire(database, answering), statusCode: 201);
                    }
                }
                ManagedDatabase current;
                lock (gate)
                {
                    var found = databases.Values.SingleOrDefault(value => value.ProjectId == project && (value.Name == parts[4] || value.Id == parts[4]));
                    if (found is null)
                    {
                        return Error(404, "not_found");
                    }

                    current = found;
                }
                if (parts.Length >= 6 && parts[5] is "backups" or "backup-policy")
                {
                    return await BackupsAsync(http, parts, project, current, callerRole);
                }

                var atHome = current.HomeRegion == answering;
                if (parts.Length == 7 && parts[5] == "point-in-time" && parts[6] == "restore" && method == "POST")
                {
                    if (callerRole is not ("owner" or "admin")) { return Error(403, "role_too_low"); }
                    if (!atHome) { return WrongRegion(http, current); }
                    var request = await JsonAsync(http);
                    Require(request.EnumerateObject().All(field => field.Name is "name" or "pointInTime" or "ttlSeconds"), "Fork at a time uses platform history");
                    var point = request.GetProperty("pointInTime").GetDateTimeOffset();
                    var name = request.GetProperty("name").GetString()!;
                    ManagedDatabase fork;
                    IReadOnlyList<TestSqlStatement> replay;
                    lock (gate)
                    {
                        var snapshot = snapshots.Values.Where(item => item.Backup.DatabaseId == current.Id && item.Backup.CreatedAt <= point)
                            .OrderByDescending(item => item.Backup.CreatedAt).FirstOrDefault();
                        if (snapshot is null) { return Error(409, "restore_point_unproven"); }
                        if (databases.Values.Any(item => item.ProjectId == project && item.Name == name)) { return Error(409, "name_taken"); }
                        fork = new ManagedDatabase(ids.NewId(), project, name, DateTimeOffset.UtcNow, current.Id, current.HomeRegion, []) { ExpiresAt = Expiry(request) };
                        databases.Add(fork.Id, fork);
                        replay = snapshot.Statements;
                    }
                    if (replay.Count > 0) { await SqlEngine(fork.Id).ExecuteAsync(replay, http.RequestAborted); }
                    return Results.Json(Wire(fork, answering), statusCode: 201);
                }
                switch (parts.Length, method, parts.ElementAtOrDefault(5))
                {
                    // The managed GET normalizes the replica list even though raw restore 201 permits null.
                    case (5, "GET", _): return Results.Json(Wire(backupFailure == "foreign-confirm" && current.BranchedFrom is not null ? current with { ProjectId = ids.NewId() } : current, answering));
                    case (6, "PUT" or "DELETE", "ttl"):
                        if (callerRole is not ("owner" or "admin")) { return Error(403, "role_too_low"); }
                        if (!atHome) { return WrongRegion(http, current); }
                        DateTimeOffset? expiry = null;
                        if (method == "PUT")
                        {
                            var ttl = await JsonAsync(http);
                            Require(ttl.EnumerateObject().Select(field => field.Name).SequenceEqual(new[] { "ttlSeconds" }), "TTL update body");
                            expiry = Expiry(ttl);
                        }
                        lock (gate)
                        {
                            var updated = current with { ExpiresAt = expiry };
                            databases[current.Id] = updated;
                            return Results.Json(Wire(updated, answering));
                        }
                    case (5, "PATCH", _):
                        if (callerRole is not ("owner" or "admin"))
                        {
                            return Error(403, "role_too_low");
                        }

                        if (!atHome)
                        {
                            return WrongRegion(http, current);
                        }

                        var body = await JsonAsync(http);
                        if (body.EnumerateObject().Select(field => field.Name).SequenceEqual(new[] { "name" }))
                        {
                            var name = body.GetProperty("name").GetString()!;
                            if (!System.Text.RegularExpressions.Regex.IsMatch(name, @"\A(?!(?:con|prn|aux|nul|com[0-9]|lpt[0-9])\z)[a-z][a-z0-9_-]{0,62}\z"))
                            {
                                return Error(400, "invalid_request");
                            }

                            lock (gate)
                            {
                                if (databases.Values.Any(value => value.ProjectId == project && value.Id != current.Id && value.Name == name))
                                {
                                    return Error(409, "name_taken");
                                }

                                var renamed = current with { Name = name, PreviousName = null, ConflictOf = null };
                                databases[current.Id] = renamed;
                                return Results.Json(Wire(renamed, answering));
                            }
                        }
                        // As production TerraBase until replicas are available: placement updates are refused.
                        return Error(400, "replicas_unavailable");
                    case (5, "DELETE", _):
                        if (!atHome)
                        {
                            return WrongRegion(http, current);
                        }

                        lock (gate)
                        {
                            databases.Remove(current.Id);
                        }
                        return Interlocked.Decrement(ref anchorDeleteFailures) >= 0 ? AnchorUnavailable() : Results.NoContent();
                    case (6, "POST", "branches"):
                        if (callerRole is not ("owner" or "admin"))
                        {
                            return Error(403, "role_too_low");
                        }

                        if (!atHome)
                        {
                            return WrongRegion(http, current);
                        }

                        var request = await JsonAsync(http);
                        Require(request.EnumerateObject().All(field => field.Name is "name" or "ttlSeconds"), "The fork body accepts name and TTL");
                        var branchName = request.GetProperty("name").GetString()!;
                        var branchSnapshot = SqlEngine(current.Id).Snapshot();
                        ManagedDatabase branch;
                        lock (gate)
                        {
                            if (databases.Values.Any(database => database.ProjectId == project && database.Name == branchName))
                            { Interlocked.Increment(ref branchConflicts); return Error(409, "name_taken"); }
                            branch = new ManagedDatabase(ids.NewId(), project, branchName, DateTimeOffset.UtcNow, current.Id, current.HomeRegion, []) { ExpiresAt = Expiry(request) };
                            databases.Add(branch.Id, branch);
                            Interlocked.Increment(ref branchCreates);
                        }
                        if (branchSnapshot.Count > 0) { await SqlEngine(branch.Id).ExecuteAsync(branchSnapshot, http.RequestAborted); }
                        return Results.Json(Wire(branch, answering), statusCode: 201);
                    case (7, "POST", "sql") when parts[6] == "execute":
                        // TerraBase's relational SQL API: statements run in one transaction on the database's own SQL engine.
                        var statements = (await JsonAsync(http)).GetProperty("statements").EnumerateArray().Select(item => item.GetProperty("sql").GetString()!).ToArray();
                        var engine = SqlEngine(current.Id);
                        {
                            if (statements.Any(sql => System.Text.RegularExpressions.Regex.IsMatch(sql, @"^\s*(INSERT|UPDATE|DELETE|REPLACE|CREATE|DROP|ALTER)\b", System.Text.RegularExpressions.RegexOptions.IgnoreCase)) &&
                                Authorize(http, project, "sql:write") is not null)
                            {
                                return SqlError(403, "forbidden_scope", "not authorized", "42501");
                            }

                            try
                            {
                                var ran = await engine.ExecuteAsync([.. statements.Select(sql => new TestSqlStatement(sql, []))], http.RequestAborted);
                                return Results.Json(new
                                {
                                    results = ran.Select(result => new
                                    {
                                        columns = result.Columns.Select(name => new { name, decltype = (string?)null }).ToArray(),
                                        rows = result.Rows,
                                        rows_affected = result.RowsAffected,
                                        last_insert_rowid = (long?)null
                                    }).ToArray()
                                });
                            }
                            catch (TestSqlFailure failure)
                            {
                                return Results.Json(new { error = new { code = failure.Code, message = failure.Message, statement = failure.Statement, sqlstate = failure.Sqlstate } }, statusCode: 400);
                            }
                        }
                }
                throw new InvalidOperationException("Unsupported canonical database route: " + method + " " + http.Request.Path);
            }

            // An exchanged project bearer (TestAuth RFC 8693 grant) for exactly this project and its tenant, holding the action scope.
            // A bare session token, a ts-auth API key secret, or another project's bearer is refused.
            // TerraBase's project backup destination: the credentials are write-only and the bucket probe always passes here.
            private async Task<IResult> DestinationAsync(HttpContext http, string project)
            {
                if (Authorize(http, project, http.Request.Method == "GET" ? "databases:read" : "databases:write") is { } denied)
                {
                    return denied;
                }

                if (http.Request.Method != "GET" && (string)http.Items["producer-role"]! is not ("owner" or "admin"))
                {
                    return Error(403, "role_too_low");
                }

                lock (gate)
                {
                    if (http.Request.Method == "GET")
                    {
                        return destinations.TryGetValue(project, out var saved) ? Results.Json(saved) : Error(404, "not_found");
                    }

                    if (http.Request.Method == "DELETE")
                    {
                        return destinations.Remove(project) ? Results.NoContent() : Error(404, "not_found");
                    }
                }
                Require(http.Request.Method == "PUT", "Backup destination uses PUT");
                var body = await JsonAsync(http);
                Require(body.EnumerateObject().Select(field => field.Name).Order(StringComparer.Ordinal)
                    .SequenceEqual(new[] { "accessKeyId", "bucket", "endpoint", "mode", "region", "secretKey" }) && body.GetProperty("mode").GetString() == "both",
                    "The destination sends the documented fields and keeps platform backups");
                lock (gate)
                {
                    return Results.Json(destinations[project] = new(body.GetProperty("endpoint").GetString()!, body.GetProperty("region").GetString()!,
                    body.GetProperty("bucket").GetString()!, "both", DateTimeOffset.UtcNow));
                }
            }

            private IResult? Authorize(HttpContext http, string project, string scope)
            {
                var text = http.Request.Headers.Authorization.ToString();
                var grant = text.StartsWith("Bearer ", StringComparison.Ordinal) ? ResolveProjectToken(text[7..]) : null;
                if (grant is not { } admitted)
                {
                    return Error(401, "unauthorized");
                }

                Require(TerraScaleIds.IsValid(project), "The current producer accepts canonical ULID projects");
                // The grant's tenant is re-checked against ts-auth's current membership, so a removed member's bearer stops working.
                var callerRole = admitted.ProjectId == project ? KeyRole(admitted.UserId, admitted.OrganizationId, project) : "";
                if (callerRole.Length == 0)
                {
                    return Error(404, "not_found");
                }

                var only = text.StartsWith("Bearer ", StringComparison.Ordinal) ? ApiKeys?.MachineDatabase(text[7..]) : null;
                var parts = http.Request.Path.Value!.Split('/', StringSplitOptions.RemoveEmptyEntries);
                if (only is not null && (parts.Length < 5 || parts[4] != only)) return Error(404, "not_found");
                if (!admitted.Scopes.Contains(scope, StringComparer.Ordinal))
                {
                    return Error(403, "forbidden_scope");
                }

                http.Items["producer-subject"] = admitted.UserId;
                http.Items["producer-role"] = callerRole;
                return null;
            }

            // I2 ManagedDatabaseBody: camelCase, answering region and complete placement.
            private DateTimeOffset? Expiry(JsonElement request)
            {
                if (!request.TryGetProperty("ttlSeconds", out var ttl)) { return null; }
                var seconds = ttl.GetInt64();
                Require(seconds is >= 60 and <= 31_536_000, "TTL seconds range");
                return DateTimeOffset.UtcNow.AddSeconds(seconds);
            }

            private static object Wire(ManagedDatabase database, string region)
            {
                return new
                {
                    id = database.Id,
                    projectId = database.ProjectId,
                    name = database.Name,
                    branchedFrom = database.BranchedFrom,
                    createdAt = database.CreatedAt,
                    expiresAt = database.ExpiresAt,
                    region,
                    homeRegion = database.HomeRegion,
                    replicaRegions = database.ReplicaRegions ?? Array.Empty<string>(),
                    expiresAt = database.ExpiresAt,
                    allRegions = database.AllRegions
                };
            }

            private void Require(bool condition, string message)
            {
                if (!condition)
                {
                    throw new InvalidOperationException(message);
                }
            }
            // TerraBase DatabaseRegions.Misdirected: 421 wrong_region naming the home and every serving region.
            private static IResult WrongRegion(HttpContext http, ManagedDatabase database)
            {
                http.Response.Headers.CacheControl = "no-store";
                http.Response.Headers["X-TerraBase-Database-Home"] = database.HomeRegion;
                http.Response.Headers["X-TerraBase-Database-Regions"] = string.Join(',', (database.ReplicaRegions ?? []).Prepend(database.HomeRegion));
                return Results.Json(new { error = new { code = "wrong_region", message = "the database is served in " + database.HomeRegion } }, statusCode: 421);
            }
            private static IResult SqlError(int status, string code, string message, string sqlstate)
            {
                return Results.Json(new { error = new { code, message, statement = 0, sqlstate } }, statusCode: status);
            }

            private static IResult AnchorUnavailable()
            {
                return Results.Json(new
                {
                    error = new { code = "catalog_anchor_unavailable", message = "the change is committed on this machine and replicates, but its restore anchor could not be written in time; retry" }
                }, statusCode: 503);
            }

            private static IResult Error(int status, string code)
            {
                return Results.Json(new { error = new { code, message = "Protocol producer rejected the request" } }, statusCode: status);
            }

            private static async Task<JsonElement> JsonAsync(HttpContext http) { using var json = await JsonDocument.ParseAsync(http.Request.Body, cancellationToken: http.RequestAborted); return json.RootElement.Clone(); }
            private sealed record ProducerSnapshot(ProducerBackup Backup, IReadOnlyList<TestSqlStatement> Statements);
            private sealed record RequestObservation(string Method, string Path, string Authorization, string ContentType, string IfMatch, string IfNoneMatch, string Query);
        }
    }
}
