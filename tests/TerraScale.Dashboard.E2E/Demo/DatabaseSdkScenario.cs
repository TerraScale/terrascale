using Cronos;
using System.Collections.Concurrent;
using System.Net;
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
using TerraScale.Dashboard.Features.Accounts;
using TerraScale.Dashboard.Features.Auth;
using TerraScale.Dashboard.Features.Backups;
using TerraScale.Dashboard.Features.Data;
using TerraScale.Dashboard.Features.Databases;
using TerraScale.Dashboard.Features.Organizations;
using TerraScale.Dashboard.Platform.Persistence;
using TerraScale.DotNet;
using TerraScale.DotNet.TerraBase;

namespace TerraScale.Dashboard.E2E;

// An external HTTP producer for application E2E tests. The dashboard keeps its production database, document, changes and
// API-key adapters with the official SDK. Data routes follow I2 contracts/terrabase (canonical /v1/projects lifecycle by
// database name, exchanged action-scoped project bearers, SSE changes); project keys follow I1 contracts/auth at the
// global ts-auth origin only. No customer key reaches TerraBase and no key is stored by the dashboard. Management routes
// remain only for workspace/backup callers not yet moved. This fixture does not qualify a live deployment.
public sealed class DatabaseSdkScenario : IAsyncDisposable
{
    private WebApplication? producer;
    private WebApplication? first;
    private WebApplication? second;
    private ProducerTransport? transport;
    private readonly DatabaseProducer protocol = new();
    private RecordingState? state;
    public Uri Origin { get; private set; } = null!;
    public Uri ReplicaOrigin { get; private set; } = null!;
    public Uri ProviderOrigin { get; private set; } = null!;

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
            "SELECT name FROM sqlite_master WHERE type='table' ORDER BY name LIMIT 101"
            ? Results.Json(new { results = new[] { new { columns = new[] { new { name = "name", decltype = (string?)null } }, rows = Array.Empty<object>(), rows_affected = 0 } } })
            : null;
    }

    public HttpClient CreateClient(bool replica = false) => Client(replica ? ReplicaOrigin : Origin);
    public HttpClient CreateProviderClient() => Client(ProviderOrigin);
    public void AddSqlViewer(string organizationId, string userId, string email)
    {
        lock (protocol.Workspace.Gate)
            protocol.Workspace.Organizations[organizationId].Members[userId] = new()
            { UserId = userId, Email = email, Name = "SQL viewer", Role = "viewer", Status = "active" };
    }

    private static HttpClient Client(Uri origin) => new(new HttpClientHandler { ServerCertificateCustomValidationCallback = TestHttps.Validate, AllowAutoRedirect = false, CookieContainer = new() })
    { BaseAddress = origin, Timeout = TimeSpan.FromSeconds(30) };

    private async Task InitializeAsync(string root, Action<WebApplicationBuilder>? configureScenario, Action<WebApplication>? configureApp)
    {
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        builder.Logging.ClearProviders();
        producer = builder.Build();
        producer.MapMethods("/{**path}", ["GET", "POST", "PUT", "PATCH", "DELETE"], (Func<HttpContext, Task<IResult>>)protocol.DispatchAsync);
        await producer.StartAsync();
        ProviderOrigin = Address(producer);
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
            protocol.ResolveProjectToken = auth.ResolveProjectToken;
            protocol.ApiKeys ??= new TestAuthApiKeys(auth.ResolveAccessToken, protocol.KeyRole);
            transport!.ApiKeys = protocol.ApiKeys;
            application.Services.Replace(ServiceDescriptor.Singleton<IDashboardState>(state));
            application.Services.Replace(ServiceDescriptor.Singleton<IHttpClientFactory>(new ProducerFactory(transport)));
            application.Services.Replace(ServiceDescriptor.Singleton<IDatabaseBackend, DatabaseBackend>());
            application.Services.Replace(ServiceDescriptor.Singleton<IDocumentBackend>(services => services.GetRequiredService<DocumentBackend>()));
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

    private static Uri Address(WebApplication app) => new(app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!
        .Addresses.Single().Replace("0.0.0.0", "127.0.0.1", StringComparison.Ordinal));

    public async ValueTask DisposeAsync()
    {
        protocol.Release();
        if (second is not null) { await second.StopAsync(); await second.DisposeAsync(); second = null; }
        if (first is not null) { await first.StopAsync(); await first.DisposeAsync(); first = null; }
        if (producer is not null) { await producer.StopAsync(); await producer.DisposeAsync(); producer = null; }
        transport?.Dispose();
    }

    private sealed class ProducerFactory(ProducerTransport transport) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => new(transport, disposeHandler: false);
    }

    private sealed class ProducerTransport(Uri origin) : DelegatingHandler(new HttpClientHandler { ServerCertificateCustomValidationCallback = TestHttps.Validate, AllowAutoRedirect = false })
    {
        public TestAuthApiKeys? ApiKeys { get; set; }
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var uri = request.RequestUri!;
            if (uri.Host == "auth.terrascale.tech") return ApiKeys!.SendAsync(request, cancellationToken);
            if (uri.Scheme != "https" || !uri.IsDefaultPort || uri.UserInfo.Length != 0 || uri.Host is not
                ("api.terrascale.tech" or "gru.api.terrascale.tech" or "sfo.api.terrascale.tech" or "sjc.api.terrascale.tech" or "fra.api.terrascale.tech"))
                throw new HttpRequestException("Unexpected outbound origin in the test-only database producer.");
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
        // The encrypted data-key registry is gone; recording its former collection shows nothing writes customer keys again.
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
        public Task InitializeAsync(CancellationToken ct) => inner.InitializeAsync(ct);
        public Task CheckReadyAsync(CancellationToken ct) => inner.CheckReadyAsync(ct);
        public async Task<TerraBaseDocument?> GetAsync(string collection, string id, CancellationToken ct)
        {
            var value = await inner.GetAsync(collection, id, ct);
            if (collection != Credentials) return value;
            Interlocked.Increment(ref credentialReads);
            return ConflictCredentials && value is not null ? value with { State = "conflict" } : value;
        }
        public async Task PutAsync(string collection, string id, JsonElement document, TerraBasePrecondition condition, CancellationToken ct)
        {
            var databaseCommit = collection == AccountService.OrganizationsCollection &&
                document.GetProperty("Projects").EnumerateObject().Any(project => project.Value.GetProperty("Databases").EnumerateObject().Any());
            if (databaseCommit && quotaGate is { } paused)
            {
                Interlocked.Increment(ref pausedQuotaCommits);
                await paused.Task.WaitAsync(ct);
            }
            if (collection == AccountService.OrganizationsCollection && id == targetDatabaseOrg &&
                Volatile.Read(ref databaseMetadataArmed) == 1 && document.GetProperty("Projects").TryGetProperty(targetDatabaseProject, out var databaseProject) &&
                databaseProject.GetProperty("Databases").EnumerateObject().Any(database => database.Value.GetProperty("Name").GetString() == targetDatabaseName))
            {
                var prior = await inner.GetAsync(collection, id, ct);
                if (prior is not null && prior.Document.GetProperty("Projects").TryGetProperty(targetDatabaseProject, out var priorProject) &&
                    !priorProject.GetProperty("Databases").EnumerateObject().Any(database => database.Value.GetProperty("Name").GetString() == targetDatabaseName) &&
                    Interlocked.CompareExchange(ref databaseMetadataArmed, 0, 1) == 1)
                {
                    Interlocked.Increment(ref failedDatabaseMetadataWrites);
                    throw new IOException("Test-only database metadata write interrupted.");
                }
            }
            // Stored project settings carry no name; ts-auth's canonical project with the armed name identifies the target.
            if (collection == AccountService.OrganizationsCollection && id == targetProjectOrg &&
                Volatile.Read(ref projectMetadataArmed) == 1 && CanonicalProjectId(targetProjectOrg, targetProjectName) is { } targetProject &&
                document.GetProperty("Projects").TryGetProperty(targetProject, out _))
            {
                var prior = await inner.GetAsync(collection, id, ct);
                if (prior is not null && !prior.Document.GetProperty("Projects").TryGetProperty(targetProject, out _) &&
                    Interlocked.CompareExchange(ref projectMetadataArmed, 0, 1) == 1)
                {
                    Interlocked.Increment(ref failedProjectMetadataWrites);
                    throw new IOException("Test-only project metadata write interrupted.");
                }
            }
            if (collection == AccountService.OrganizationsCollection && id == targetDeletionOrg &&
                Volatile.Read(ref projectDeletionMetadataArmed) == 1 && !document.GetProperty("Projects").TryGetProperty(targetDeletionProject, out _))
            {
                var prior = await inner.GetAsync(collection, id, ct);
                if (prior is not null && prior.Document.GetProperty("Projects").TryGetProperty(targetDeletionProject, out var project) &&
                    project.GetProperty("Id").GetString() == targetDeletionProject &&
                    Interlocked.CompareExchange(ref projectDeletionMetadataArmed, 0, 1) == 1)
                {
                    Interlocked.Increment(ref failedProjectDeletionMetadataWrites);
                    throw new IOException("Test-only project deletion metadata write interrupted.");
                }
            }
            try { await inner.PutAsync(collection, id, document, condition, ct); }
            catch (TerraBasePreconditionFailedException) when (databaseCommit)
            { Interlocked.Increment(ref quotaCasFailures); throw; }
            if (collection == Credentials) encrypted.Enqueue(document.GetRawText());
        }
        Task<TerraBaseDocumentPage> IDashboardState.ReadPageAsync(string collection, JsonElement? filter, int limit, string? cursor, CancellationToken ct) => inner.ReadPageAsync(collection, filter, limit, cursor, ct);
        Task<TerraBaseTransactionResult> IDashboardState.TransactAsync(IReadOnlyList<DashboardStateOperation> operations, CancellationToken ct) => inner.TransactAsync(operations, ct);
        public Task<IReadOnlyList<TerraBaseDocument>> ListAsync(string collection, CancellationToken ct) => inner.ListAsync(collection, ct);
        public Task<TerraBaseDocumentPage> QueryPageAsync(string collection, TerraBaseQuery query, CancellationToken ct) => inner.QueryPageAsync(collection, query, ct);
        public Task DeleteAsync(string collection, string id, string version, CancellationToken ct) => inner.DeleteAsync(collection, id, version, ct);
        private string? CanonicalProjectId(string organizationId, string name)
        {
            lock (workspace.Gate)
                return workspace.Organizations.GetValueOrDefault(organizationId)?.Projects.Values.SingleOrDefault(project => project.Name == name)?.Id;
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
        private readonly Dictionary<string, ExportScheduleView> schedules = [];
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
        private readonly Dictionary<string, Dictionary<string, TerraBaseDocument>> collections = [];
        private readonly ConcurrentQueue<RequestObservation> requests = new();
        private readonly ConcurrentQueue<string> failures = new();
        private TaskCompletionSource? createGate;
        private readonly Dictionary<string, SortedDictionary<string, ProducerIndex>> indexes = [];
        private readonly List<ChangeRecord> changes = [];
        private long sequence;
        private long changeFloor;
        private bool changesInitialCursor = true;
        private bool changesReplayBoundary;
        private int pausedCreates;
        private int creates;
        // As production TerraBase's 503 catalog_anchor_unavailable: this many next placement and delete writes commit and still answer 503.
        private int anchorPatchFailures;
        private int anchorDeleteFailures;
        private int branchCreates;
        private int branchConflicts;
        private int documentsRequests;
        private long offset;
        public RecordingState State { get; set; } = null!;
        // ts-auth's canonical organizations, projects and memberships for this fixture; the producer stores no identity.
        public TestWorkspace Workspace { get; set; } = null!;
        public ITsAuthPlatform? Platform { get; set; }
        public Func<string, (string UserId, string Email)?> ResolveAccessToken { get; set; } = null!;
        public Func<string, (string UserId, string OrganizationId, string ProjectId, IReadOnlyList<string> Scopes)?> ResolveProjectToken { get; set; } = null!;
        public TestAuthApiKeys? ApiKeys { get; set; }
        // ts-auth's current project role; the role control is ts-auth's own override for current members ("" removes them).
        public string KeyRole(string userId, string organizationId, string projectId) => Workspace.ProjectRole(userId, organizationId, projectId);
        private bool CanonicalProject(string organizationId, string projectId)
        {
            lock (Workspace.Gate) return Workspace.Organizations.GetValueOrDefault(organizationId)?.Projects.ContainsKey(projectId) == true;
        }
        // The stored dashboard record omits ts-auth identity, so ownership comes from the fixture's ts-auth registry.
        private string Owner(string organizationId)
        {
            lock (Workspace.Gate)
                return Workspace.Organizations.GetValueOrDefault(organizationId) is { } org && org.RoleFor(org.OwnerUserId) == "owner" ? org.OwnerUserId : "";
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

        public async Task<IResult> DispatchAsync(HttpContext http)
        {
            try
            {
                var path = http.Request.Path.Value!;
                if (path.StartsWith("/v1/projects/", StringComparison.Ordinal) &&
                    (http.Request.Method == "GET" && (path.EndsWith("/replication", StringComparison.Ordinal) || path.EndsWith("/analytics", StringComparison.Ordinal) || path.Contains("/schema/", StringComparison.Ordinal) || path.EndsWith("/point-in-time", StringComparison.Ordinal)) ||
                     http.Request.Method == "POST" && (path.EndsWith("/schema/preview", StringComparison.Ordinal) || path.Contains("/point-in-time/", StringComparison.Ordinal))))
                {
                    var parts = path.Split('/', StringSplitOptions.RemoveEmptyEntries);
                    var projectId = parts.Length >= 4 ? parts[2] : "";
                    var text = http.Request.Headers.Authorization.ToString();
                    var grant = text.StartsWith("Bearer ", StringComparison.Ordinal) ? ResolveProjectToken(text[7..]) : null;
                    Require(projectId.Length > 0 && grant is not null && grant.Value.ProjectId == projectId,
                        "Project reporting and schema use an exchanged project-scoped bearer for the selected project");
                    Require(CanonicalProject(grant!.Value.OrganizationId, projectId), "Project replication bearer organization matches the canonical project");
                    if (path.EndsWith("/point-in-time", StringComparison.Ordinal)) Require(grant.Value.Scopes.SequenceEqual(new[] { "databases:read" }), "PITR window uses only database read authority");
                    if (http.Request.Method == "POST")
                    {
                        var expected = path.EndsWith("/recovery-preview", StringComparison.Ordinal) ? new[] { "databases:write", "documents:read" }
                            : path.EndsWith("/recover", StringComparison.Ordinal) ? new[] { "databases:write", "documents:write" }
                            : path.EndsWith("/plan", StringComparison.Ordinal) ? new[] { "databases:read" }
                            : path.EndsWith("/restore", StringComparison.Ordinal) ? new[] { "databases:write" }
                            : new[] { "documents:read", "documents:write" };
                        Require(grant.Value.Scopes.Order(StringComparer.Ordinal).SequenceEqual(expected), "Preview/recovery uses the exact action-scoped project grant");
                    }
                    lock (gate)
                    {
                        var databaseId = http.Request.Query["database"].ToString();
                        Require(databaseId.Length == 0 || databases.TryGetValue(databaseId, out var database) && database.ProjectId == projectId,
                            "Project replication database filter is inside the bearer project");
                    }
                }
                if (Extension is not null && await Extension(http) is { } extensionResult) return extensionResult;
                if (path == "/__test/control") return await ControlAsync(http);
                if (path == "/__test/observations") return Observations();
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
                    return Error(421, "misdirected_request");
                if (path == "/healthz")
                    return Results.Json(new { status = "ok", version = "test", region = answering, storageFormat = "test", revision = (string?)null, machine = "local" });
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
                    if (route is [_, _, _, "backup-destination"]) return await DestinationAsync(http, route[2]);
                    if (route is not [_, _, _, "databases", _, "collections", ..]) return await CanonicalAsync(http);
                    Interlocked.Increment(ref documentsRequests);
                    return await DocumentsAsync(http);
                }
                throw new InvalidOperationException("Unexpected producer route: " + http.Request.Method + " " + path);
            }
            catch (Exception error) when (error is InvalidOperationException or JsonException)
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
                case "expire-changes": lock (gate) changeFloor = sequence; break;
                case "changes-initial-cursor": changesInitialCursor = body.GetProperty("value").GetBoolean(); break;
                case "changes-replay-boundary": changesReplayBoundary = body.GetProperty("value").GetBoolean(); break;
                case "changes-transaction": TransactionChanges(body); break;
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
                    Volatile.Write(ref anchorPatchFailures, body.GetProperty("patch").GetInt32());
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
                        foreach (var removed in databases.Values.Where(value => value.ProjectId == body.GetProperty("projectId").GetString() && value.Name == body.GetProperty("value").GetString()).ToArray())
                            databases.Remove(removed.Id);
                    break;
                case "add-replica":
                    var added = body.GetProperty("value").GetString()!;
                    lock (gate)
                        foreach (var placed in databases.Values.ToArray())
                            databases[placed.Id] = placed with { ReplicaRegions = [.. (placed.ReplicaRegions ?? []).Where(region => region != added), added] };
                    break;
                case "fail-database-metadata":
                case "database-status":
                    var databaseOrgId = body.GetProperty("organizationId").GetString()!;
                    var databaseProjectId = body.GetProperty("projectId").GetString()!;
                    var databaseValue = await State.GetAsync(AccountService.OrganizationsCollection, databaseOrgId, http.RequestAborted)
                        ?? throw new InvalidOperationException("Database fixture control targets a canonical organization");
                    var databaseOrg = databaseValue.Document.Deserialize<OrganizationRecord>()!;
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
                        lock (gate) Require(databases.TryGetValue(fixtureDatabaseId, out var remoteDatabase) && remoteDatabase.ProjectId == databaseProjectId,
                            "The presentation fixture owns its actual producer database");
                        fixtureDatabase!.Status = status;
                        await State.PutAsync(AccountService.OrganizationsCollection, databaseOrgId, JsonSerializer.SerializeToElement(databaseOrg),
                            TerraBasePrecondition.Matches(databaseValue.Version), http.RequestAborted);
                    }
                    break;
                case "fail-project-metadata":
                    var faultOrganizationId = body.GetProperty("organizationId").GetString()!;
                    var faultName = body.GetProperty("name").GetString()!.Trim();
                    var faultValue = await State.GetAsync(AccountService.OrganizationsCollection, faultOrganizationId, http.RequestAborted)
                        ?? throw new InvalidOperationException("Project metadata failure targets an existing organization");
                    var faultOrg = faultValue.Document.Deserialize<OrganizationRecord>()!;
                    bool faultNameTaken;
                    lock (Workspace.Gate) faultNameTaken = Workspace.Organizations.GetValueOrDefault(faultOrganizationId)?.Projects.Values.Any(project => project.Name == faultName) != false;
                    Require(TerraScaleIds.IsValid(faultOrganizationId) && faultValue.Id == faultOrg.Id && faultOrg.Id == faultOrganizationId &&
                        faultOrg.Status == "active" && Owner(faultOrganizationId).Length > 0 && faultName.Length > 0 && !faultNameTaken,
                        "Project metadata fault is limited to a new project of a current canonical owner");
                    State.ArmProjectMetadataFailure(faultOrganizationId, faultName);
                    break;
                case "fail-project-deletion-metadata":
                    var deletionOrgId = body.GetProperty("organizationId").GetString()!;
                    var deletionProjectId = body.GetProperty("projectId").GetString()!;
                    var deletionValue = await State.GetAsync(AccountService.OrganizationsCollection, deletionOrgId, http.RequestAborted)
                        ?? throw new InvalidOperationException("Project deletion metadata failure targets an existing organization");
                    var deletionOrg = deletionValue.Document.Deserialize<OrganizationRecord>()!;
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
                    var current = await State.GetAsync(AccountService.OrganizationsCollection, organizationId, http.RequestAborted)
                        ?? throw new InvalidOperationException("The fixture plan targets an existing organization");
                    var organization = current.Document.Deserialize<OrganizationRecord>()!;
                    organization.PlanId = body.GetProperty("value").GetString()!;
                    await State.PutAsync(AccountService.OrganizationsCollection, organizationId, JsonSerializer.SerializeToElement(organization),
                        TerraBasePrecondition.Matches(current.Version), http.RequestAborted);
                    break;
                default: throw new InvalidOperationException("Unknown test producer control");
            }
            return Results.NoContent();
        }

        private IResult Observations()
        {
            object[] workspaceProjects;
            lock (Workspace.Gate) workspaceProjects = Workspace.Organizations.Values.SelectMany(org => org.Projects.Values)
                .Select(value => new { value.Id, value.OrganizationId, value.Name }).ToArray();
            lock (gate) return Results.Json(new
            {
                failures = failures.ToArray(),
                requests = requests.ToArray(),
                pausedLists = ApiKeys?.PausedLists ?? 0,
                pausedCreates,
                creates,
                branchCreates,
                branchConflicts,
                documentsRequests,
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
                changes = changes.Select(value => new { value.Sequence, value.Transaction, value.DatabaseId, value.Collection, value.Id, value.Op }).ToArray(),
                encrypted = State.Encrypted,
                credentialReads = State.CredentialReads,
                pausedQuotaCommits = State.PausedQuotaCommits,
                quotaCasFailures = State.QuotaCasFailures,
                databases = databases.Values.ToArray()
            });
        }

        // Producer project backup contract: /v1/projects/{project}/databases/{name}/backups and backup-policy
        // with an action-scoped project token, full-only policy, complete snapshots, new-database restore.
        // The test producer really copies its stored snapshot; it is not live
        // engine/storage admission or scheduler qualification.
        private async Task<IResult> BackupsAsync(HttpContext http, string[] parts, string project, ManagedDatabase source, string callerRole)
        {
            if (callerRole.Length == 0) return Error(404, "not_found");
            if (http.Request.Method != "GET" && callerRole is not ("owner" or "admin")) return Error(403, "role_too_low");
            if (backupFailure == "outage") return Error(503, "unavailable");
            if (parts.Length == 6 && parts[5] == "export-schedule")
            {
                lock (gate)
                {
                    if (http.Request.Method == "GET") return Results.Json(schedules.GetValueOrDefault(source.Id) ?? new ExportScheduleView(null, null, null));
                    if (!destinations.ContainsKey(project)) return Error(409, "conflict");
                }
                Require(http.Request.Method == "PUT", "Export schedule uses PUT");
                var cron = (await JsonAsync(http)).GetProperty("cron").GetString();
                if (!string.IsNullOrEmpty(cron) && !CronExpression.TryParse(cron, CronFormat.Standard, out _)) return Error(400, "validation");
                lock (gate) return Results.Json(schedules[source.Id] = new(string.IsNullOrEmpty(cron) ? null : cron, DateTimeOffset.UtcNow, null));
            }
            if (parts.Length == 6 && parts[5] == "backup-policy")
            {
                if (http.Request.Method == "GET")
                { lock (gate) return Results.Json(policies.GetValueOrDefault(source.Id) ?? new ProducerBackupPolicy("0 2 * * *", 7, false, null)); }
                Require(http.Request.Method == "PUT", "Producer full policy uses PUT");
                var body = await JsonAsync(http);
                Require(body.EnumerateObject().Select(field => field.Name).Order(StringComparer.Ordinal)
                    .SequenceEqual(new[] { "disabled", "fullBackupCron", "retentionDays" }), "Producer accepts no invented incremental policy field");
                var cron = body.GetProperty("fullBackupCron").GetString();
                var days = body.GetProperty("retentionDays").GetInt32();
                if (days is < 1 or > 35 || !string.IsNullOrWhiteSpace(cron) && !CronExpression.TryParse(cron.Trim(), CronFormat.Standard, out _))
                    return Error(400, "validation");
                var policy = new ProducerBackupPolicy(string.IsNullOrWhiteSpace(cron) ? null : cron.Trim(), days,
                    body.GetProperty("disabled").GetBoolean(), DateTimeOffset.UtcNow);
                lock (gate) policies[source.Id] = policy;
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
                var prefix = project + "/" + source.Name + "/";
                lock (gate)
                {
                    var snapshot = collections.Where(item => item.Key.StartsWith(prefix, StringComparison.Ordinal))
                        .ToDictionary(item => item.Key[prefix.Length..], item => new Dictionary<string, TerraBaseDocument>(item.Value), StringComparer.Ordinal);
                    var size = snapshot.Values.SelectMany(rows => rows.Values).Sum(document => (long)System.Text.Encoding.UTF8.GetByteCount(document.Document.GetRawText()));
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
            lock (gate)
            {
                if (!snapshots.TryGetValue(parts[6], out var snapshot) || snapshot.Backup.DatabaseId != source.Id) return Error(404, "not_found");
                var name = request.GetProperty("name").GetString();
                if (name is null)
                {
                    for (var attempt = 1; ; attempt++)
                    {
                        var suffix = attempt == 1 ? "-restore" : "-restore-" + attempt;
                        name = source.Name[..Math.Min(source.Name.Length, 63 - suffix.Length)] + suffix;
                        if (!databases.Values.Any(database => database.ProjectId == project && database.Name == name)) break;
                    }
                }
                if (databases.Values.Any(database => database.ProjectId == project && database.Name == name)) return Error(409, "name_taken");
                Interlocked.Increment(ref backupRestores);
                var restored = new ManagedDatabase(ids.NewId(), project, name, DateTimeOffset.UtcNow, source.Id,
                    http.Request.Host.Host == "gru.api.terrascale.tech" ? "gru" : "sfo", null);
                if (backupFailure == "wrong-project") restored = restored with { ProjectId = ids.NewId() };
                if (backupFailure == "same-id") restored = restored with { Id = source.Id };
                if (backupFailure == "wrong-source") restored = restored with { BranchedFrom = ids.NewId() };
                if (backupFailure == "wrong-name") restored = restored with { Name = source.Name };
                if (backupFailure == "invalid-name") restored = restored with { Name = "INVALID" };
                if (backupFailure is not ("phantom" or "same-id" or "wrong-project"))
                {
                    databases.Add(restored.Id, restored);
                    if (backupFailure != "wrong-name")
                        foreach (var collection in snapshot.Collections)
                            collections.Add(project + "/" + restored.Name + "/" + collection.Key, new Dictionary<string, TerraBaseDocument>(collection.Value));
                }
                return Results.Json(restored, statusCode: 201);
            }
        }

        // I2 canonical project lifecycle and database operations. {database} is the name; each operation demands the exact
        // action scope its SDK method documents (databases:* lifecycle/SQL/index/stats, documents:read for the feed).
        private async Task<IResult> CanonicalAsync(HttpContext http)
        {
            var parts = http.Request.Path.Value!.Split('/', StringSplitOptions.RemoveEmptyEntries).Select(Uri.UnescapeDataString).ToArray();
            Require(parts.Length >= 4 && parts[3] == "databases", "Canonical project database route");
            var project = parts[2];
            var method = http.Request.Method;
            var answering = http.Response.Headers["X-TerraBase-Region"].ToString();
            var scope = parts switch
            {
                [_, _, _, _, _, "changes"] => "documents:read",
                [_, _, _, _] or [_, _, _, _, _] when method == "GET" => "databases:read",
                [_, _, _, _, _, "sql", "execute"] => "documents:read",
                [_, _, _, _, _, "schema"] => "databases:read",
                [_, _, _, _, _, "schema", _] when method == "GET" => "databases:read",
                [_, _, _, _, _, "indexes"] when method == "GET" => "databases:read",
                [_, _, _, _, _, "backups" or "backup-policy" or "export-schedule"] when method == "GET" => "databases:read",
                _ => "databases:write"
            };
            if (Authorize(http, project, scope) is { } denied) return denied;
            var callerRole = (string)http.Items["producer-role"]!;
            if (parts.Length == 4 && method == "GET")
            {
                var query = http.Request.Query;
                Require(query.Keys.All(key => key is "after" or "limit"), "Database listing accepts only after and limit");
                var limit = query.ContainsKey("limit") ? int.Parse(query["limit"].ToString(), System.Globalization.CultureInfo.InvariantCulture) : 20;
                if (limit is < 1 or > 100) return Error(400, "validation");
                var after = query["after"].ToString();
                lock (gate)
                {
                    var owned = databases.Values.Where(value => value.ProjectId == project && (value.AllRegions ||
                        value.HomeRegion == answering || value.ReplicaRegions?.Contains(answering, StringComparer.Ordinal) == true))
                        .OrderBy(value => value.Id, StringComparer.Ordinal).ToArray();
                    if (after.Length > 0 && !owned.Any(value => value.Id == after)) return Error(400, "validation");
                    var page = owned.Where(value => string.CompareOrdinal(value.Id, after) > 0).Take(limit + 1).ToArray();
                    return Results.Json(new { items = page.Take(limit).Select(value => Wire(value, answering)).ToArray(), next = page.Length > limit ? page[limit - 1].Id : null });
                }
            }
            if (parts.Length == 4)
            {
                Require(method == "POST", "Canonical database create");
                var body = await JsonAsync(http);
                Require(body.EnumerateObject().All(field => field.Name is "name" or "homeRegion" or "replicaRegions" or "allRegions"), "Create sends only name and placement");
                var name = body.GetProperty("name").GetString()!;
                var home = body.TryGetProperty("homeRegion", out var named) ? named.GetString()! : answering;
                var all = body.TryGetProperty("allRegions", out var every) && every.GetBoolean();
                if (all && body.TryGetProperty("replicaRegions", out _)) return Error(400, "placement_conflict");
                // As production TerraBase: without replicaRegions (and not allRegions) it places the database in every other serving region.
                var replicas = body.TryGetProperty("replicaRegions", out var listed) ? listed.EnumerateArray().Select(value => value.GetString()!).ToArray()
                    : all ? [] : (Catalog ?? ["gru", "sfo"]).Where(region => region != home).ToArray();
                // As production TerraBase on a cell: explicit placement requires at least two regions.
                if (listed.ValueKind == JsonValueKind.Array && replicas.Length == 0) return Error(400, "placement_needs_two_regions");
                if (home != answering) return WrongRegion(http, new(ids.NewId(), project, name, DateTimeOffset.UtcNow, HomeRegion: home, ReplicaRegions: replicas));
                if (createGate is { } paused) { Interlocked.Increment(ref pausedCreates); await paused.Task.WaitAsync(http.RequestAborted); }
                lock (gate)
                {
                    if (databases.Values.Any(value => value.ProjectId == project && value.Name == name)) return Error(409, "name_taken");
                    if (createConflict.Length > 0) return Error(409, createConflict);
                    var database = new ManagedDatabase(ids.NewId(), project, name, DateTimeOffset.UtcNow, HomeRegion: home, ReplicaRegions: replicas, AllRegions: all);
                    databases[database.Id] = database;
                    Interlocked.Increment(ref creates);
                    return Results.Json(Wire(database, answering), statusCode: 201);
                }
            }
            ManagedDatabase current;
            lock (gate)
            {
                var found = databases.Values.SingleOrDefault(value => value.ProjectId == project && value.Name == parts[4]);
                if (found is null) return Error(404, "not_found");
                current = found;
            }
            if (parts.Length >= 6 && parts[5] is "backups" or "backup-policy" or "export-schedule") return await BackupsAsync(http, parts, project, current, callerRole);
            var atHome = current.HomeRegion == answering;
            switch (parts.Length, method, parts.ElementAtOrDefault(5))
            {
                // The managed GET normalizes the replica list even though raw restore 201 permits null.
                case (5, "GET", _): return Results.Json(Wire(backupFailure == "foreign-confirm" && current.BranchedFrom is not null ? current with { ProjectId = ids.NewId() } : current, answering));
                case (5, "PATCH", _):
                    if (callerRole is not ("owner" or "admin")) return Error(403, "role_too_low");
                    if (!atHome) return WrongRegion(http, current);
                    var body = await JsonAsync(http);
                    if (body.EnumerateObject().Select(field => field.Name).SequenceEqual(new[] { "allRegions" }) && body.GetProperty("allRegions").GetBoolean())
                        lock (gate) { var everywhere = current with { ReplicaRegions = [], AllRegions = true }; databases[current.Id] = everywhere; return Results.Json(Wire(everywhere, answering)); }
                    Require(body.EnumerateObject().Select(field => field.Name).SequenceEqual(new[] { "replicaRegions" }),
                        "Placement updates send only the complete producer replicaRegions list or allRegions");
                    var replicas = body.GetProperty("replicaRegions").EnumerateArray().Select(value => value.GetString()!).ToArray();
                    Require(replicas.All(value => value is "sfo" or "gru" || Catalog?.Contains(value) == true) && !replicas.Contains(current.HomeRegion, StringComparer.Ordinal) &&
                        replicas.Distinct(StringComparer.Ordinal).Count() == replicas.Length, "Placement preserves its home and unique discovered replicas");
                    lock (gate) { var changed = current with { ReplicaRegions = replicas, AllRegions = false }; databases[current.Id] = changed; return Interlocked.Decrement(ref anchorPatchFailures) >= 0 ? AnchorUnavailable() : Results.Json(Wire(changed, answering)); }
                case (5, "DELETE", _):
                    if (!atHome) return WrongRegion(http, current);
                    lock (gate)
                    {
                        databases.Remove(current.Id);
                        indexes.Remove(current.Id);
                        var prefix = project + "/" + current.Name + "/";
                        foreach (var collection in collections.Keys.Where(name => name.StartsWith(prefix, StringComparison.Ordinal)).ToArray()) collections.Remove(collection);
                    }
                    return Interlocked.Decrement(ref anchorDeleteFailures) >= 0 ? AnchorUnavailable() : Results.NoContent();
                case (6, "POST", "branches"):
                    if (callerRole is not ("owner" or "admin")) return Error(403, "role_too_low");
                    if (!atHome) return WrongRegion(http, current);
                    var request = await JsonAsync(http);
                    Require(request.EnumerateObject().Select(field => field.Name).SequenceEqual(new[] { "name" }), "The branch body accepts only its new database name");
                    var branchName = request.GetProperty("name").GetString()!;
                    lock (gate)
                    {
                        if (databases.Values.Any(database => database.ProjectId == project && database.Name == branchName))
                        { Interlocked.Increment(ref branchConflicts); return Error(409, "name_taken"); }
                        var branch = new ManagedDatabase(ids.NewId(), project, branchName, DateTimeOffset.UtcNow, current.Id, current.HomeRegion, []);
                        databases.Add(branch.Id, branch);
                        var prefix = project + "/" + current.Name + "/";
                        foreach (var collection in collections.Where(item => item.Key.StartsWith(prefix, StringComparison.Ordinal)).ToArray())
                            collections.Add(project + "/" + branchName + "/" + collection.Key[prefix.Length..], new Dictionary<string, TerraBaseDocument>(collection.Value));
                        if (indexes.TryGetValue(current.Id, out var copied)) indexes[branch.Id] = new(copied, StringComparer.Ordinal);
                        Interlocked.Increment(ref branchCreates);
                        return Results.Json(Wire(branch, answering), statusCode: 201);
                    }
                case (6, "GET", "schema"):
                    lock (gate) return Results.Json(new
                    {
                        items = collections.Where(item => item.Key.StartsWith(project + "/" + current.Name + "/", StringComparison.Ordinal)).Select(item =>
                            new { name = item.Key.Split('/')[2], documents = (long)item.Value.Count, bytes = item.Value.Values.Sum(value => (long)System.Text.Encoding.UTF8.GetByteCount(value.Document.GetRawText())) }).ToArray()
                    });
                case (7, "GET", "schema"):
                    lock (gate)
                    {
                        if (!collections.TryGetValue(project + "/" + current.Name + "/" + parts[6], out var inferredRows)) return Error(404, "not_found");
                        // I2 inferred-schema.json: sampled document presence and type counts, separate from declared schema.
                        var sampled = inferredRows.Values.OrderBy(row => row.Id, StringComparer.Ordinal).Take(1000).ToArray();
                        var inferredFields = sampled.SelectMany(row => row.Document.EnumerateObject()).GroupBy(field => field.Name)
                            .OrderBy(field => field.Key, StringComparer.Ordinal)
                            .Select(field => new
                            {
                                path = field.Key,
                                present = field.Count(),
                                types = field.GroupBy(value => value.Value.ValueKind switch
                                {
                                    JsonValueKind.Number => value.Value.TryGetInt64(out _) ? "integer" : "number",
                                    JsonValueKind.True or JsonValueKind.False => "boolean",
                                    _ => value.Value.ValueKind.ToString().ToLowerInvariant()
                                }).ToDictionary(kind => kind.Key, kind => kind.Count())
                            }).ToArray();
                        return Results.Json(new { collection = parts[6], documents = inferredRows.Count, sampledDocuments = sampled.Length, fields = inferredFields, uniqueConstraints = Array.Empty<object>() });
                    }
                case (7, "POST", "sql") when parts[6] == "execute":
                    // TerraBase's relational SQL API, reduced to SELECT * over a document collection and acknowledged writes.
                    var statements = (await JsonAsync(http)).GetProperty("statements");
                    Require(statements.GetArrayLength() == 1, "The dashboard runs one statement per request");
                    var sql = statements[0].GetProperty("sql").GetString()!;
                    if (sql == "SELECT name FROM sqlite_master WHERE type='table' ORDER BY name LIMIT 101")
                        return Results.Json(new { results = new[] { new { columns = new[] { new { name = "name", decltype = (string?)null } }, rows = Array.Empty<object>(), rows_affected = 0 } } });
                    var match = System.Text.RegularExpressions.Regex.Match(sql, @"^\s*SELECT\s+\*\s+FROM\s+([A-Za-z0-9_-]+)(?:\s+LIMIT\s+(\d+))?\s*;?\s*$", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
                    if (!match.Success)
                    {
                        if (!System.Text.RegularExpressions.Regex.IsMatch(sql, @"^\s*(INSERT|UPDATE|DELETE|CREATE|DROP)\b", System.Text.RegularExpressions.RegexOptions.IgnoreCase))
                            return SqlError(400, "sql_error", "SQL syntax error at line 1, column 1", "SQLITE_ERROR");
                        if (Authorize(http, project, "documents:write") is not null) return SqlError(403, "forbidden_scope", "not authorized", "SQLITE_AUTH");
                        return Results.Json(new { results = new[] { new { columns = Array.Empty<object>(), rows = Array.Empty<object>(), rows_affected = 1, last_insert_rowid = (long?)null } } });
                    }
                    var take = match.Groups[2].Success ? int.Parse(match.Groups[2].Value, System.Globalization.CultureInfo.InvariantCulture) : 100;
                    lock (gate)
                    {
                        if (!collections.TryGetValue(project + "/" + current.Name + "/" + match.Groups[1].Value, out var rows))
                            return SqlError(400, "sql_error", "no such table: " + match.Groups[1].Value, "SQLITE_ERROR");
                        var selected = rows.Values.OrderBy(value => value.Id, StringComparer.Ordinal).Take(take).ToArray();
                        var names = selected.SelectMany(value => value.Document.EnumerateObject().Select(field => field.Name)).Where(name => name != "id").Distinct().ToArray();
                        return Results.Json(new
                        {
                            results = new[] { new
                            {
                                columns = new[] { new { name = "id", decltype = (string?)"TEXT" } }.Concat(names.Select(name => new { name, decltype = (string?)null })).ToArray(),
                                rows = selected.Select(value => new object?[] { value.Id }.Concat(names.Select(name => value.Document.TryGetProperty(name, out var cell) ? (object?)cell : null)).ToArray()).ToArray(),
                                rows_affected = 0, last_insert_rowid = (long?)null
                            } }
                        });
                    }
                case (6, "GET", "indexes"):
                    var only = http.Request.Query["collection"].ToString();
                    lock (gate) return Results.Json(new
                    {
                        items = indexes.GetValueOrDefault(current.Id)?.Values.Where(index => only.Length == 0 || index.Collection == only)
                        .Select(index => new { name = index.Name, collection = index.Collection, fields = index.Fields, state = "active" }).ToArray() ?? []
                    });
                case (6, "POST", "indexes"):
                    var create = await JsonAsync(http);
                    Require(create.EnumerateObject().All(field => field.Name is "collection" or "fields" or "name"), "Index create sends collection, fields and optional name");
                    var indexCollection = create.GetProperty("collection").GetString()!;
                    var fields = create.GetProperty("fields").EnumerateArray().Select(value => value.GetString()!).ToArray();
                    if (fields.Length is < 1 or > 4 || fields.Distinct(StringComparer.Ordinal).Count() != fields.Length) return Error(400, "validation");
                    var indexName = create.TryGetProperty("name", out var given) ? given.GetString()! : indexCollection + "_" + string.Join('_', fields).Replace('.', '_');
                    lock (gate)
                    {
                        var owned = indexes.TryGetValue(current.Id, out var existing) ? existing : indexes[current.Id] = new(StringComparer.Ordinal);
                        if (owned.ContainsKey(indexName)) return Error(409, "index_exists");
                        owned[indexName] = new(indexName, indexCollection, fields);
                    }
                    return Results.Json(new { name = indexName, collection = indexCollection, fields, state = "active" }, statusCode: 201);
                case (7, "DELETE", "indexes"):
                    lock (gate) return indexes.GetValueOrDefault(current.Id)?.Remove(parts[6]) == true ? Results.NoContent() : Error(404, "not_found");
                case (6, "GET", "changes"):
                    return await ChangesAsync(http, current);
            }
            throw new InvalidOperationException("Unsupported canonical database route: " + method + " " + http.Request.Path);
        }

        // An exchanged project bearer (TestAuth RFC 8693 grant) for exactly this project and its tenant, holding the action scope.
        // A bare session token, a ts-auth API key secret, or another project's bearer is refused.
        // TerraBase's project backup destination: the credentials are write-only and the bucket probe always passes here.
        private async Task<IResult> DestinationAsync(HttpContext http, string project)
        {
            if (Authorize(http, project, http.Request.Method == "GET" ? "databases:read" : "databases:write") is { } denied) return denied;
            if (http.Request.Method != "GET" && (string)http.Items["producer-role"]! is not ("owner" or "admin")) return Error(403, "role_too_low");
            lock (gate)
            {
                if (http.Request.Method == "GET") return destinations.TryGetValue(project, out var saved) ? Results.Json(saved) : Error(404, "not_found");
                if (http.Request.Method == "DELETE") return destinations.Remove(project) ? Results.NoContent() : Error(404, "not_found");
            }
            Require(http.Request.Method == "PUT", "Backup destination uses PUT");
            var body = await JsonAsync(http);
            Require(body.EnumerateObject().Select(field => field.Name).Order(StringComparer.Ordinal)
                .SequenceEqual(new[] { "accessKeyId", "bucket", "endpoint", "mode", "region", "secretKey" }) && body.GetProperty("mode").GetString() == "both",
                "The destination sends the documented fields and keeps platform backups");
            lock (gate) return Results.Json(destinations[project] = new(body.GetProperty("endpoint").GetString()!, body.GetProperty("region").GetString()!,
                body.GetProperty("bucket").GetString()!, "both", DateTimeOffset.UtcNow));
        }

        private IResult? Authorize(HttpContext http, string project, string scope)
        {
            var text = http.Request.Headers.Authorization.ToString();
            var grant = text.StartsWith("Bearer ", StringComparison.Ordinal) ? ResolveProjectToken(text[7..]) : null;
            if (grant is not { } admitted) return Error(401, "unauthorized");
            Require(TerraScaleIds.IsValid(project), "The current producer accepts canonical ULID projects");
            // The grant's tenant is re-checked against ts-auth's current membership, so a removed member's bearer stops working.
            var callerRole = admitted.ProjectId == project ? KeyRole(admitted.UserId, admitted.OrganizationId, project) : "";
            if (callerRole.Length == 0) return Error(404, "not_found");
            if (!admitted.Scopes.Contains(scope, StringComparer.Ordinal)) return Error(403, "forbidden_scope");
            http.Items["producer-subject"] = admitted.UserId;
            http.Items["producer-role"] = callerRole;
            return null;
        }

        // SSE per I2 contracts/terrabase changes.sse plus the COS SDK14 repair: a transaction's changes carry an id only on its
        // final event, the opening checkpoint may be absent, and a resume at a transaction boundary may replay that transaction.
        // The stream ends cleanly after the retained events and a closing checkpoint; the consumer reconnects with Last-Event-ID.
        private async Task<IResult> ChangesAsync(HttpContext http, ManagedDatabase database)
        {
            var query = http.Request.Query;
            Require(query.Keys.All(key => key is "cursor" or "collections"), "Changes accepts only cursor and collections");
            string? text = http.Request.Headers.TryGetValue("Last-Event-ID", out var resume) ? resume.ToString() : query.ContainsKey("cursor") ? query["cursor"].ToString() : null;
            var filter = query.ContainsKey("collections") ? query["collections"].ToString().Split(',').ToHashSet(StringComparer.Ordinal) : null;
            var events = new System.Text.StringBuilder();
            lock (gate)
            {
                long start;
                if (text is null) start = changeFloor;
                else if (text == "now") start = sequence;
                else if (DecodeCursor(text, database.Id) is { } decoded) start = decoded;
                else return Error(400, "invalid_cursor");
                if (start < changeFloor) return Error(410, "cursor_expired");
                if (changesInitialCursor) Cursor(events, database.Id, start);
                var own = changes.Where(change => change.DatabaseId == database.Id).ToArray();
                var replayed = changesReplayBoundary ? own.Where(change => change.Sequence == start).Select(change => change.Transaction).FirstOrDefault(-1) : -1;
                foreach (var group in own.Where(change => change.Sequence > start || change.Transaction == replayed).GroupBy(change => change.Transaction))
                {
                    var delivered = group.Where(change => filter is null || filter.Contains(change.Collection)).ToArray();
                    for (var index = 0; index < delivered.Length; index++)
                    {
                        var change = delivered[index];
                        TerraBaseDocument? value = null;
                        collections.GetValueOrDefault(database.ProjectId + "/" + database.Name + "/" + change.Collection)?.TryGetValue(change.Id, out value);
                        events.Append("event: change\n");
                        if (index == delivered.Length - 1) events.Append("id: ").Append(EncodeCursor(database.Id, group.Max(item => item.Sequence))).Append('\n');
                        events.Append("data: ").Append(JsonSerializer.Serialize(new
                        {
                            collection = change.Collection,
                            id = change.Id,
                            op = change.Op,
                            version = change.Sequence.ToString(System.Globalization.CultureInfo.InvariantCulture),
                            origin = "00000000000000a1",
                            document = value?.Document
                        })).Append("\n\n");
                    }
                }
                Cursor(events, database.Id, Math.Max(start, own.Select(change => change.Sequence).DefaultIfEmpty(0).Max()));
            }
            http.Response.Headers.CacheControl = "no-store";
            http.Response.ContentType = "text/event-stream";
            await http.Response.WriteAsync(events.ToString(), http.RequestAborted);
            return Results.Empty;

            static void Cursor(System.Text.StringBuilder events, string databaseId, long position)
            {
                var cursor = EncodeCursor(databaseId, position);
                events.Append("event: cursor\nid: ").Append(cursor).Append("\ndata: ").Append(JsonSerializer.Serialize(new { cursor })).Append("\n\n");
            }
        }

        // Test control: one committed multi-change transaction; only its final SSE event names the cursor after it.
        private void TransactionChanges(JsonElement body)
        {
            var project = body.GetProperty("projectId").GetString()!;
            var name = body.GetProperty("database").GetString()!;
            var collection = body.GetProperty("collection").GetString()!;
            var keys = body.GetProperty("ids").EnumerateArray().Select(value => value.GetString()!).ToArray();
            Require(keys.Length >= 2 && keys.Distinct(StringComparer.Ordinal).Count() == keys.Length, "A fixture transaction holds several distinct changes");
            lock (gate)
            {
                var database = databases.Values.SingleOrDefault(value => value.ProjectId == project && value.Name == name)
                    ?? throw new InvalidOperationException("The fixture transaction targets an existing producer database");
                Require(collections.TryGetValue(project + "/" + name + "/" + collection, out var rows), "The fixture transaction targets an existing collection");
                var transaction = sequence + 1;
                foreach (var key in keys)
                {
                    rows![key] = new(key, "tbv1-" + Guid.NewGuid().ToString("N"), "value", JsonSerializer.SerializeToElement(new { transaction }));
                    changes.Add(new(++sequence, transaction, database.Id, collection, key, "upsert"));
                }
            }
        }

        private static string EncodeCursor(string databaseId, long position) =>
            WebEncoders.Base64UrlEncode(System.Text.Encoding.UTF8.GetBytes(databaseId + ":" + position.ToString(System.Globalization.CultureInfo.InvariantCulture)));
        private static long? DecodeCursor(string cursor, string databaseId)
        {
            try
            {
                var text = System.Text.Encoding.UTF8.GetString(WebEncoders.Base64UrlDecode(cursor));
                return text.StartsWith(databaseId + ":", StringComparison.Ordinal) && long.TryParse(text[(databaseId.Length + 1)..], System.Globalization.NumberStyles.None,
                    System.Globalization.CultureInfo.InvariantCulture, out var position) ? position : null;
            }
            catch (FormatException) { return null; }
        }

        // I2 ManagedDatabaseBody: camelCase, answering region and complete placement.
        private static object Wire(ManagedDatabase database, string region) => new
        {
            id = database.Id,
            projectId = database.ProjectId,
            name = database.Name,
            branchedFrom = database.BranchedFrom,
            createdAt = database.CreatedAt,
            region,
            homeRegion = database.HomeRegion,
            replicaRegions = database.ReplicaRegions ?? Array.Empty<string>(),
            allRegions = database.AllRegions
        };

        private async Task<IResult> DocumentsAsync(HttpContext http)
        {
            // Match the current producer's public point route. Document keys
            // are query encoded exactly once and never occupy a path segment.
            var raw = http.Features.Get<Microsoft.AspNetCore.Http.Features.IHttpRequestFeature>()!.RawTarget!.Split('?')[0];
            var parts = raw.Split('/', StringSplitOptions.RemoveEmptyEntries).Select(Uri.UnescapeDataString).ToArray();
            Require(parts.Length >= 6 && parts[0] == "v1" && parts[1] == "projects" && parts[3] == "databases", "Official Documents SDK path");
            var project = parts[2]; var database = parts[4];
            Require(TerraScaleIds.IsValid(project), "The current producer accepts canonical ULID projects");
            lock (gate) Require(databases.Values.Any(value => value.ProjectId == project && value.Name == database), "Documents database belongs to scoped project");
            var token = http.Request.Headers.Authorization.ToString();
            var read = http.Request.Method == "GET" || parts.LastOrDefault() == "query";
            if (Authorize(http, project, read ? "documents:read" : "documents:write") is { } denied) return denied;
            requests.Enqueue(new(http.Request.Method, raw, token, http.Request.ContentType ?? "", http.Request.Headers.IfMatch.ToString(), http.Request.Headers.IfNoneMatch.ToString(), http.Request.QueryString.Value ?? ""));
            Require(parts[5] == "collections", "SDK collection path");
            var prefix = project + "/" + database + "/";
            if (parts.Length == 6)
            {
                if (http.Request.Method == "GET")
                {
                    Require(http.Request.Query["limit"] == "200", "Collection list uses bounded SDK pagination");
                    lock (gate) return Results.Json(new { items = collections.Keys.Where(name => name.StartsWith(prefix, StringComparison.Ordinal)).Select(name => new { name = name[prefix.Length..], created_at = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds().ToString(), id = ids.NewId() }).ToArray(), next_cursor = (string?)null });
                }
                var body = await JsonAsync(http); var name = body.GetProperty("name").GetString()!;
                lock (gate) { if (!collections.TryAdd(prefix + name, [])) return Error(409, "name_taken"); }
                return Results.Json(new { name, created_at = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds().ToString(), id = ids.NewId() }, statusCode: 201);
            }
            if (parts.Length == 7 && http.Request.Method == "DELETE")
            {
                lock (gate) { if (!collections.Remove(prefix + parts[6])) return Error(404, "not_found"); }
                return Results.Json(new { name = parts[6], created_at = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds().ToString(), id = ids.NewId() });
            }
            Require(parts.Length >= 8 && parts[7] is "documents" or "query", "Official document route");
            var collectionKey = prefix + parts[6];
            Dictionary<string, TerraBaseDocument> rows;
            lock (gate) { if (!collections.TryGetValue(collectionKey, out rows!)) return Error(404, "not_found"); }
            if (parts.Length == 8 && http.Request.Method is "GET" or "POST")
            {
                var limit = http.Request.Method == "GET" ? int.Parse(http.Request.Query["limit"].ToString()) : 25;
                var cursor = http.Request.Query["cursor"].ToString();
                var skip = cursor.Length == 0 ? 0 : int.Parse(cursor);
                lock (gate)
                {
                    var ordered = rows.Values.OrderBy(value => value.Id, StringComparer.Ordinal).ToArray(); var page = ordered.Skip(skip).Take(limit).ToArray();
                    return Results.Json(new { items = page, next_cursor = skip + page.Length < ordered.Length ? (skip + page.Length).ToString() : null, count = new { value = ordered.Length.ToString(System.Globalization.CultureInfo.InvariantCulture), exact = true } });
                }
            }
            Require(parts.Length == 9 && parts[8] == "by-key", "Point operations use the current producer by-key route");
            Require(http.Request.Query.Count == 1 && http.Request.Query.TryGetValue("id", out var queryId) && queryId.Count == 1 && queryId[0] is { Length: > 0 },
                "A point operation carries exactly one UTF-8 query-encoded key");
            var id = http.Request.Query["id"].ToString();
            if (http.Request.Method == "GET")
            {
                lock (gate)
                {
                    if (!rows.TryGetValue(id, out var current)) return Error(404, "not_found");
                    http.Response.Headers.ETag = '"' + current.Version + '"';
                    return Results.Json(current);
                }
            }
            JsonElement document = default;
            if (http.Request.Method != "DELETE") document = await JsonAsync(http);
            lock (gate)
            {
                rows.TryGetValue(id, out var current);
                var match = http.Request.Headers.IfMatch.ToString(); var absent = http.Request.Headers.IfNoneMatch.ToString();
                if (absent == "*" && current is not null || match.Length > 0 && (current is null || match != '"' + current.Version + '"')) return Error(412, "precondition_failed");
                Require(absent == "*" || match.Length > 0, "Dashboard writes always carry an optimistic concurrency condition");
                if (http.Request.Method == "PATCH")
                {
                    Require(http.Request.ContentType == "application/merge-patch+json", "SDK patch uses RFC7396 content type");
                    document = JsonSerializer.SerializeToElement(Merge(JsonNode.Parse(current!.Document.GetRawText()), JsonNode.Parse(document.GetRawText())));
                }
                var version = "tbv1-" + Guid.NewGuid().ToString("N");
                if (http.Request.Method == "DELETE") rows.Remove(id); else rows[id] = new(id, version, "value", document.Clone());
                var changed = databases.Values.Single(value => value.ProjectId == project && value.Name == database);
                ++sequence;
                changes.Add(new(sequence, sequence, changed.Id, parts[6], id, http.Request.Method == "DELETE" ? "delete" : "upsert"));
                http.Response.Headers.ETag = '"' + version + '"';
                return Results.Json(new { id, version, offset = (++offset).ToString(System.Globalization.CultureInfo.InvariantCulture), durability = "local_durable" }, statusCode: current is null ? 201 : 200);
            }
        }

        private void Require(bool condition, string message) { if (!condition) throw new InvalidOperationException(message); }
        // TerraBase DatabaseRegions.Misdirected: 421 wrong_region naming the home and every serving region.
        private static IResult WrongRegion(HttpContext http, ManagedDatabase database)
        {
            http.Response.Headers.CacheControl = "no-store";
            http.Response.Headers["X-TerraBase-Database-Home"] = database.HomeRegion;
            http.Response.Headers["X-TerraBase-Database-Regions"] = string.Join(',', (database.ReplicaRegions ?? []).Prepend(database.HomeRegion));
            return Results.Json(new { error = new { code = "wrong_region", message = "the database is served in " + database.HomeRegion } }, statusCode: 421);
        }
        private static IResult SqlError(int status, string code, string message, string sqliteCode) =>
            Results.Json(new { error = new { code, message, statement = 0, sqlite_code = sqliteCode } }, statusCode: status);
        private static IResult AnchorUnavailable() => Results.Json(new
        {
            error = new { code = "catalog_anchor_unavailable", message = "the change is committed on this machine and replicates, but its restore anchor could not be written in time; retry" }
        }, statusCode: 503);
        private static IResult Error(int status, string code) => Results.Json(new { error = new { code, message = "Protocol producer rejected the request" } }, statusCode: status);
        private static async Task<JsonElement> JsonAsync(HttpContext http) { using var json = await JsonDocument.ParseAsync(http.Request.Body, cancellationToken: http.RequestAborted); return json.RootElement.Clone(); }
        private static JsonNode? Merge(JsonNode? current, JsonNode? patch)
        {
            if (patch is not JsonObject changes) return patch?.DeepClone();
            var target = current as JsonObject ?? new JsonObject();
            foreach (var entry in changes) { if (entry.Value is null) target.Remove(entry.Key); else target[entry.Key] = Merge(target[entry.Key], entry.Value); }
            return target;
        }
        private sealed record ProducerSnapshot(ProducerBackup Backup, Dictionary<string, Dictionary<string, TerraBaseDocument>> Collections);
        private sealed record ProducerIndex(string Name, string Collection, string[] Fields);
        private sealed record ChangeRecord(long Sequence, long Transaction, string DatabaseId, string Collection, string Id, string Op);
        private sealed record RequestObservation(string Method, string Path, string Authorization, string ContentType, string IfMatch, string IfNoneMatch, string Query);
    }
}
