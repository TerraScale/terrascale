using Cronos;
using System.Collections.Concurrent;
using System.Net;
using System.Security.Cryptography;
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

// An external HTTP producer for application E2E tests. The dashboard keeps its
// production database adapter, official Documents SDK and encrypted key registry.
// Data identity/point routes/offsets match producer72cec95a's implemented
// compatibility contract. This fixture does not qualify a live deployment.
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
        Func<HttpContext, Task<IResult?>>? producerExtension = null)
    {
        var scenario = new DatabaseSdkScenario();
        scenario.protocol.Extension = producerExtension;
        try { await scenario.InitializeAsync(root, configureScenario); return scenario; }
        catch { await scenario.DisposeAsync(); throw; }
    }

    public HttpClient CreateClient(bool replica = false) => Client(replica ? ReplicaOrigin : Origin);
    public HttpClient CreateProviderClient() => Client(ProviderOrigin);
    private static HttpClient Client(Uri origin) => new(new HttpClientHandler { AllowAutoRedirect = false, CookieContainer = new() })
    { BaseAddress = origin, Timeout = TimeSpan.FromSeconds(30) };

    private async Task InitializeAsync(string root, Action<WebApplicationBuilder>? configureScenario)
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
            state ??= new((IDashboardState)application.Services.Last(item => item.ServiceType == typeof(IDashboardState)).ImplementationInstance!);
            protocol.State = state;
            var auth = (TestAuth)application.Services.Last(item => item.ServiceType == typeof(IAuthGateway)).ImplementationInstance!;
            protocol.ResolveAccessToken = auth.ResolveAccessToken;
            protocol.ResolveProjectToken = auth.ResolveProjectToken;
            application.Services.Replace(ServiceDescriptor.Singleton<IDashboardState>(state));
            application.Services.Replace(ServiceDescriptor.Singleton<IHttpClientFactory>(new ProducerFactory(transport)));
            application.Services.Replace(ServiceDescriptor.Singleton<IDatabaseBackend, DatabaseBackend>());
            application.Services.Replace(ServiceDescriptor.Singleton<IDocumentBackend>(services => services.GetRequiredService<DocumentBackend>()));
            application.Services.Replace(ServiceDescriptor.Singleton<IDataCredentialRegistry>(services => services.GetRequiredService<DocumentBackend>()));
            application.Services.AddSingleton<TsWorkspaceGateway>();
            application.Services.Replace(ServiceDescriptor.Singleton<IWorkspaceGateway>(services => services.GetRequiredService<TsWorkspaceGateway>()));
            application.Services.Replace(ServiceDescriptor.Singleton<IOrganizationAccess, TsOrganizationAccess>());
            application.Services.Replace(ServiceDescriptor.Singleton<IOrganizationLifecycle, TsOrganizationLifecycle>());
            configureScenario?.Invoke(application);
        }
        first = environment.CreateHost(root, 0, "http://127.0.0.1", Configure);
        second = environment.CreateHost(root, 0, "http://127.0.0.1", Configure);
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

    private sealed class ProducerTransport(Uri origin) : DelegatingHandler(new HttpClientHandler { AllowAutoRedirect = false })
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var uri = request.RequestUri!;
            if (uri.Scheme != "https" || !uri.IsDefaultPort || uri.UserInfo.Length != 0 || uri.Host is not
                ("api.terrascale.tech" or "gru.api.terrascale.tech" or "sfo.api.terrascale.tech"))
                throw new HttpRequestException("Unexpected outbound origin in the test-only database producer.");
            request.Headers.Host = uri.Authority;
            request.RequestUri = new(origin, uri.PathAndQuery);
            return base.SendAsync(request, cancellationToken);
        }
    }

    private sealed class RecordingState(IDashboardState inner) : IDashboardState
    {
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
            if (collection == AccountService.OrganizationsCollection && id == targetProjectOrg &&
                Volatile.Read(ref projectMetadataArmed) == 1 && document.GetProperty("Projects").EnumerateObject()
                    .Any(project => project.Value.GetProperty("Name").GetString() == targetProjectName))
            {
                var prior = await inner.GetAsync(collection, id, ct);
                if (prior is not null && !prior.Document.GetProperty("Projects").EnumerateObject()
                    .Any(project => project.Value.GetProperty("Name").GetString() == targetProjectName) &&
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
        public Task DeleteAsync(string collection, string id, string version, CancellationToken ct) => inner.DeleteAsync(collection, id, version, ct);
    }

    private sealed class DatabaseProducer
    {
        // The current producer creates canonical ULIDs. Keep those actual IDs
        // on both SDK boundaries instead of converting fixture UUIDs.
        private readonly TerraScaleIdGenerator ids = new();
        private readonly object gate = new();
        private readonly Dictionary<string, ManagedDatabase> databases = [];
        private readonly Dictionary<string, TerraBaseOrganization> organizations = [];
        private readonly Dictionary<string, TerraBaseProject> projects = [];
        private readonly Dictionary<string, Credential> credentials = [];
        private readonly Dictionary<string, ProducerSnapshot> snapshots = [];
        private readonly Dictionary<string, ProducerBackupPolicy> policies = [];
        private string backupFailure = "";
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
        private TaskCompletionSource? listGate;
        private TaskCompletionSource? createGate;
        private int pausedLists;
        private int pausedCreates;
        private int creates;
        private int branchCreates;
        private int branchConflicts;
        private int documentsRequests;
        private long offset;
        private string role = "owner";
        public RecordingState State { get; set; } = null!;
        public Func<string, (string UserId, string Email)?> ResolveAccessToken { get; set; } = null!;
        public Func<string, (string UserId, string OrganizationId, string ProjectId)?> ResolveProjectToken { get; set; } = null!;
        public Func<HttpContext, Task<IResult?>>? Extension { get; set; }
        // The region that answers api.terrascale.tech, as Fly picks it for the dashboard's machine.
        public string ApiRegion { get; set; } = "gru";

        public void Release() { listGate?.TrySetResult(); createGate?.TrySetResult(); State?.ReleaseQuotaCommits(); }

        public async Task<IResult> DispatchAsync(HttpContext http)
        {
            try
            {
                var path = http.Request.Path.Value!;
                if (http.Request.Method == "GET" && path.StartsWith("/v1/projects/", StringComparison.Ordinal) &&
                    (path.EndsWith("/replication", StringComparison.Ordinal) || path.Contains("/schema/", StringComparison.Ordinal)))
                {
                    var parts = path.Split('/', StringSplitOptions.RemoveEmptyEntries);
                    var projectId = parts.Length >= 4 ? parts[2] : "";
                    var text = http.Request.Headers.Authorization.ToString();
                    var grant = text.StartsWith("Bearer ", StringComparison.Ordinal) ? ResolveProjectToken(text[7..]) : null;
                    Require(projectId.Length > 0 && grant is not null && grant.Value.ProjectId == projectId,
                        "Project reporting and schema use an exchanged project-scoped bearer for the selected project");
                    lock (gate)
                    {
                        Require(projects.TryGetValue(projectId, out var project) && project!.OrganizationId == grant!.Value.OrganizationId,
                            "Project replication bearer organization matches the canonical project");
                        var databaseId = http.Request.Query["database"].ToString();
                        Require(databaseId.Length == 0 || databases.TryGetValue(databaseId, out var database) && database.ProjectId == projectId,
                            "Project replication database filter is inside the bearer project");
                    }
                }
                if (Extension is not null && await Extension(http) is { } extensionResult) return extensionResult;
                if (path == "/__test/control") return await ControlAsync(http);
                if (path == "/__test/observations") return Observations();
                Require(http.Request.Host.Host is "api.terrascale.tech" or "gru.api.terrascale.tech" or "sfo.api.terrascale.tech", "Trusted producer host");
                // terrascale e30ffdc2 (region-identity.md): every answer names its region; api.terrascale.tech answers in ApiRegion.
                var answering = http.Request.Host.Host switch { "sfo.api.terrascale.tech" => "sfo", "gru.api.terrascale.tech" => "gru", _ => ApiRegion };
                http.Response.Headers["X-TerraBase-Region"] = answering;
                if (http.Request.Headers.TryGetValue("X-TerraScale-Region-Pin", out var pin) && (pin.Count != 1 || pin[0] != answering))
                    return Error(421, "misdirected_request");
                if (path == "/healthz")
                    return Results.Json(new { status = "ok", version = "test", region = answering, storageFormat = "test", revision = (string?)null, machine = "local" });
                var token = http.Request.Headers.Authorization.ToString();
                if (path.StartsWith("/v1/management/", StringComparison.Ordinal))
                {
                    var admitted = token.StartsWith("Bearer ", StringComparison.Ordinal) ? ResolveAccessToken(token[7..]) : null;
                    Require(admitted is not null, "Management uses admitted user access token");
                    Require(TerraScaleIds.IsValid(admitted!.Value.UserId), "The current producer accepts canonical ULID bearer subjects");
                    http.Items["producer-subject"] = admitted!.Value.UserId;
                    return await ManagementAsync(http);
                }
                if (path.StartsWith("/v1/projects/", StringComparison.Ordinal))
                {
                    if (http.Request.Method == "GET" && path.EndsWith("/replication", StringComparison.Ordinal))
                    {
                        var parts = path.Split('/', StringSplitOptions.RemoveEmptyEntries);
                        Require(parts.Length == 4 && parts[3] == "replication", "Canonical project replication route");
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
                case "pause-key-list": Interlocked.Exchange(ref pausedLists, 0); listGate = new(TaskCreationOptions.RunContinuationsAsynchronously); break;
                case "release-key-list": listGate?.TrySetResult(); listGate = null; break;
                case "pause-database-creates": Interlocked.Exchange(ref pausedCreates, 0); createGate = new(TaskCreationOptions.RunContinuationsAsynchronously); break;
                case "release-database-creates": createGate?.TrySetResult(); createGate = null; break;
                case "pause-quota-commits": State.PauseQuotaCommits(); break;
                case "release-quota-commits": State.ReleaseQuotaCommits(); break;
                case "credential-conflict": State.ConflictCredentials = body.GetProperty("value").GetBoolean(); break;
                case "role": role = body.GetProperty("value").GetString()!; break;
                case "backup-failure": backupFailure = body.GetProperty("value").GetString()!; break;
                // Another TerraBase client placed a replica: the producer's placement is wider than the dashboard asked for.
                case "api-region": ApiRegion = body.GetProperty("value").GetString()!; break;
                case "add-replica":
                    var added = body.GetProperty("value").GetString()!;
                    lock (gate)
                        foreach (var placed in databases.Values.ToArray())
                            databases[placed.Id] = placed with { ReplicaRegions = [.. placed.ReplicaRegions ?? [], added] };
                    break;
                case "fail-database-metadata":
                case "database-status":
                    var databaseOrgId = body.GetProperty("organizationId").GetString()!;
                    var databaseProjectId = body.GetProperty("projectId").GetString()!;
                    var databaseValue = await State.GetAsync(AccountService.OrganizationsCollection, databaseOrgId, http.RequestAborted)
                        ?? throw new InvalidOperationException("Database fixture control targets a canonical organization");
                    var databaseOrg = databaseValue.Document.Deserialize<OrganizationRecord>()!;
                    databaseOrg.Projects.TryGetValue(databaseProjectId, out var fixtureProject);
                    Require(role == "owner" && databaseValue.Id == databaseOrg.Id && databaseOrg.Id == databaseOrgId && databaseOrg.Status == "active" &&
                        databaseOrg.RoleFor(databaseOrg.OwnerUserId) == "owner" && fixtureProject is not null,
                        "Database fixture control targets its current owner's canonical project");
                    lock (gate) Require(organizations.TryGetValue(databaseOrgId, out var databaseOwned) && databaseOwned.CreatedBy == databaseOrg.OwnerUserId &&
                        projects.TryGetValue(databaseProjectId, out var databaseRemoteProject) && databaseRemoteProject.OrganizationId == databaseOrgId,
                        "Database fixture control agrees with actual producer ownership");
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
                    Require(TerraScaleIds.IsValid(faultOrganizationId) && faultValue.Id == faultOrg.Id && faultOrg.Id == faultOrganizationId &&
                        faultOrg.Status == "active" && faultOrg.ContainsMember(faultOrg.OwnerUserId) && faultName.Length > 0 &&
                        !faultOrg.Projects.Values.Any(project => project.Name == faultName), "Project metadata fault is limited to a new project of a current canonical owner");
                    lock (gate) Require(organizations.TryGetValue(faultOrganizationId, out var ownedOrg) && ownedOrg.CreatedBy == faultOrg.OwnerUserId,
                        "Project metadata fault matches the producer-owned organization");
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
                        deletionOrg.RoleFor(deletionOrg.OwnerUserId) == "owner" && deletionOrg.Projects.Count > 1 &&
                        deletionOrg.Projects.TryGetValue(deletionProjectId, out var deleting) && deleting.Id == deletionProjectId &&
                        deleting.OrganizationId == deletionOrgId && !deleting.IsDefault && deleting.Databases.Count == 0,
                        "Project deletion metadata fault is limited to an existing empty nondefault project of a current canonical owner");
                    lock (gate) Require(role == "owner" && organizations.TryGetValue(deletionOrgId, out var deletionOwned) &&
                        deletionOwned.CreatedBy == deletionOrg.OwnerUserId && projects.TryGetValue(deletionProjectId, out var deletionRemote) &&
                        deletionRemote.OrganizationId == deletionOrgId, "Project deletion metadata fault matches the producer-owned project");
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
            lock (gate) return Results.Json(new
            {
                failures = failures.ToArray(),
                requests = requests.ToArray(),
                pausedLists,
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
                workspaceProjects = projects.Values.Select(value => new { value.Id, value.OrganizationId, value.Name }).ToArray(),
                backups = snapshots.Values.Select(value => value.Backup).ToArray(),
                credentials = credentials.Values.Select(value => new { value.Key.Id, value.Key.Name, value.Secret, value.Host }).ToArray(),
                encrypted = State.Encrypted,
                credentialReads = State.CredentialReads,
                pausedQuotaCommits = State.PausedQuotaCommits,
                quotaCasFailures = State.QuotaCasFailures,
                databases = databases.Values.ToArray()
            });
        }

        private async Task<IResult> ManagementAsync(HttpContext http)
        {
            var parts = http.Request.Path.Value!.Split('/', StringSplitOptions.RemoveEmptyEntries).Select(Uri.UnescapeDataString).ToArray();
            var subject = (string)http.Items["producer-subject"]!;
            if (http.Request.Headers.TryGetValue("X-TerraBase-User-Id", out var acting))
                Require(acting == subject, "Official management SDK acting subject matches admitted bearer");
            if (parts.Length == 3 && parts[2] == "organizations")
            {
                Require(acting == subject, "Organization bootstrap uses the official acting-user contract");
                if (http.Request.Method == "GET")
                { lock (gate) return Results.Json(new { items = organizations.Values.Where(org => org.CreatedBy == subject).ToArray(), next = (string?)null }); }
                var body = await JsonAsync(http);
                var organization = new TerraBaseOrganization(ids.NewId(), body.GetProperty("name").GetString()!, subject, DateTimeOffset.UtcNow, TerraBasePlan.Hobby);
                lock (gate) organizations[organization.Id] = organization;
                return Results.Json(organization, statusCode: 201);
            }
            var org = parts.Length > 3 ? parts[3] : "";
            Require(http.Request.Headers["X-TerraBase-Organization-Id"] == org, "Management organization header matches route");
            lock (gate) Require(organizations.TryGetValue(org, out var owned) ? owned.CreatedBy == subject : subject == "test-user", "Management tenant membership matches admitted subject");
            if (parts.Length == 4 && http.Request.Method == "GET")
            { lock (gate) return organizations.TryGetValue(org, out var organization) ? Results.Json(organization) : Results.Json(new { id = org, name = "Personal", createdBy = subject, createdAt = DateTimeOffset.UtcNow, plan = "hobby" }); }
            if (parts.Length == 5 && parts[4] == "members")
            {
                Require(acting == subject, "Official membership SDK acting subject");
                return Results.Json(new { items = role.Length == 0 ? Array.Empty<object>() : new object[] { new { userId = subject, role, joinedAt = DateTimeOffset.UtcNow } }, next = (string?)null });
            }
            if (parts.Length == 5 && http.Request.Method == "GET")
            {
                Require(acting == subject, "Official organization read SDK acting subject");
                if (parts[4] == "audit-retention") return Results.Json(new { days = 90 });
                if (parts[4] is "invitations" or "audit-events") return Results.Json(new { items = Array.Empty<object>(), next = (string?)null });
                if (parts[4] == "ownership-transfer") return Error(404, "not_found");
            }
            if (parts.Length == 5 && parts[4] == "projects")
            {
                Require(acting == subject, "Project bootstrap uses the official acting-user contract");
                if (http.Request.Method == "GET")
                { lock (gate) return Results.Json(new { items = projects.Values.Where(project => project.OrganizationId == org).ToArray(), next = (string?)null }); }
                var body = await JsonAsync(http);
                var projectName = body.GetProperty("name").GetString()!;
                lock (gate)
                {
                    if (projects.Values.Any(project => project.OrganizationId == org && project.Name == projectName))
                    { Interlocked.Increment(ref projectConflicts); return Error(409, "conflict"); }
                    var createdProject = new TerraBaseProject(ids.NewId(), org, projectName, DateTimeOffset.UtcNow);
                    projects[createdProject.Id] = createdProject;
                    Interlocked.Increment(ref projectCreates);
                    return Results.Json(createdProject, statusCode: 201);
                }
            }
            Require(parts.Length >= 6 && parts[4] == "projects", "Scoped project management route");
            var project = parts[5];
            if (parts.Length == 6 && http.Request.Method == "DELETE")
            {
                Require(acting == subject, "Official project delete SDK acting subject");
                lock (gate)
                {
                    if (!projects.TryGetValue(project, out var remote) || remote.OrganizationId != org)
                    { Interlocked.Increment(ref projectDeleteNotFound); return Error(404, "not_found"); }
                    if (role != "owner" && role != "admin") return Error(403, "forbidden");
                    if (databases.Values.Any(database => database.ProjectId == project) || credentials.Values.Any(credential => credential.Key.ProjectId == project))
                        return Error(409, "conflict");
                    projects.Remove(project);
                    Interlocked.Increment(ref projectDeletes);
                    return Results.NoContent();
                }
            }
            if (parts.Length == 6 && http.Request.Method == "GET")
            { lock (gate) return projects.TryGetValue(project, out var remote) ? Results.Json(remote) : Results.Json(new { id = project, organizationId = org, name = "Personal", createdAt = DateTimeOffset.UtcNow }); }
            if (parts.Length == 6 && http.Request.Method == "PATCH")
            {
                var body = await JsonAsync(http);
                lock (gate)
                {
                    Require(projects.TryGetValue(project, out var remote) && remote.OrganizationId == org, "Official project rename binds the canonical project");
                    var renamed = remote! with { Name = body.GetProperty("name").GetString()! }; projects[project] = renamed; return Results.Json(renamed);
                }
            }
            if (parts.Length == 7 && parts[6] == "api-keys")
            {
                if (http.Request.Method == "GET")
                {
                    if (listGate is { } paused) { Interlocked.Increment(ref pausedLists); await paused.Task.WaitAsync(http.RequestAborted); }
                    lock (gate) return Results.Json(new { items = credentials.Values.Where(value => value.Key.ProjectId == project && value.Host == http.Request.Host.Host).Select(value => value.Key).ToArray(), next = (string?)null });
                }
                var body = await JsonAsync(http);
                var secret = "tb_" + WebEncoders.Base64UrlEncode(RandomNumberGenerator.GetBytes(32));
                var key = new ProjectCredential(ids.NewId(), project, body.GetProperty("name").GetString()!,
                    body.GetProperty("scopes").EnumerateArray().Select(value => value.GetString()!).ToArray(), body.GetProperty("databaseId").GetString(), secret[^4..], DateTimeOffset.UtcNow);
                lock (gate) credentials[key.Id] = new(key, secret, http.Request.Host.Host);
                return Results.Json(new { key, secret }, statusCode: 201);
            }
            if (parts.Length == 8 && parts[6] == "api-keys" && http.Request.Method == "DELETE")
            {
                lock (gate) { Require(credentials.TryGetValue(parts[7], out var key) && key.Key.ProjectId == project && key.Host == http.Request.Host.Host, "Credential deletion matches issuing region and project"); credentials.Remove(parts[7]); }
                return Results.NoContent();
            }
            Require(parts.Length >= 7 && parts[6] == "databases", "Scoped management database route");
            if (parts.Length == 7)
            {
                if (http.Request.Method == "GET")
                { lock (gate) return Results.Json(new { items = databases.Values.Where(value => value.ProjectId == project).ToArray(), next = (string?)null }); }
                var body = await JsonAsync(http);
                if (createGate is { } paused) { Interlocked.Increment(ref pausedCreates); await paused.Task.WaitAsync(http.RequestAborted); }
                lock (gate)
                {
                    if (databases.Values.Any(value => value.ProjectId == project && value.Name == body.GetProperty("name").GetString())) return Error(409, "name_taken");
                    var database = new ManagedDatabase(ids.NewId(), project, body.GetProperty("name").GetString()!, DateTimeOffset.UtcNow,
                        HomeRegion: body.GetProperty("homeRegion").GetString()!, ReplicaRegions: body.GetProperty("replicaRegions").EnumerateArray().Select(value => value.GetString()!).ToArray());
                    databases[database.Id] = database;
                    Interlocked.Increment(ref creates);
                    return Results.Json(database, statusCode: 201);
                }
            }
            ManagedDatabase current;
            lock (gate) { Require(databases.TryGetValue(parts[7], out current!) && current.ProjectId == project, "Database ownership matches project"); }
            if (parts.Length >= 9 && parts[8] is "backups" or "backup-policy")
                return await BackupsAsync(http, parts, project, current);
            if (parts.Length == 9 && parts[8] == "branches" && http.Request.Method == "POST")
            {
                Require(acting == subject, "The official branch SDK acts as its admitted user");
                if (role is not ("owner" or "admin")) return Error(403, "role_too_low");
                var home = current.HomeRegion == "sfo" ? "sfo.api.terrascale.tech" : "gru.api.terrascale.tech";
                if (http.Request.Host.Host != home) return WrongRegion(http, current);
                var request = await JsonAsync(http);
                Require(request.EnumerateObject().Select(field => field.Name).SequenceEqual(new[] { "name" }),
                    "The frozen producer branch body accepts only its new database name");
                var name = request.GetProperty("name").GetString()!;
                lock (gate)
                {
                    if (databases.Values.Any(database => database.ProjectId == project && database.Name == name))
                    { Interlocked.Increment(ref branchConflicts); return Error(409, "name_taken"); }
                    var branch = new ManagedDatabase(ids.NewId(), project, name, DateTimeOffset.UtcNow, current.Id, current.HomeRegion, []);
                    databases.Add(branch.Id, branch);
                    var prefix = project + "/" + current.Name + "/";
                    foreach (var collection in collections.Where(item => item.Key.StartsWith(prefix, StringComparison.Ordinal)).ToArray())
                        collections.Add(project + "/" + name + "/" + collection.Key[prefix.Length..], new Dictionary<string, TerraBaseDocument>(collection.Value));
                    Interlocked.Increment(ref branchCreates);
                    return Results.Json(branch, statusCode: 201);
                }
            }
            if (parts.Length == 8 && http.Request.Method == "GET")
            {
                Require(acting == subject, "Official restored-database confirmation acting subject");
                // The real managed GET uses ManagedDatabaseBody, whose replica
                // list is normalized even though raw restore201 permits null.
                var confirmed = current with { ReplicaRegions = current.ReplicaRegions ?? [] };
                return Results.Json(backupFailure == "foreign-confirm" && current.BranchedFrom is not null ? confirmed with { ProjectId = ids.NewId() } : confirmed);
            }
            if (parts.Length == 8 && http.Request.Method == "PATCH")
            {
                if (role is not ("owner" or "admin")) return Error(403, "role_too_low");
                var home = current.HomeRegion == "sfo" ? "sfo.api.terrascale.tech" : "gru.api.terrascale.tech";
                if (http.Request.Host.Host != home) return WrongRegion(http, current);
                var body = await JsonAsync(http);
                Require(body.EnumerateObject().Select(field => field.Name).SequenceEqual(new[] { "replicaRegions" }),
                    "Placement updates send only the complete producer replicaRegions list");
                var replicas = body.GetProperty("replicaRegions").EnumerateArray().Select(value => value.GetString()!).ToArray();
                Require(replicas.All(value => value is "sfo" or "gru") && !replicas.Contains(current.HomeRegion, StringComparer.Ordinal) &&
                    replicas.Distinct(StringComparer.Ordinal).Count() == replicas.Length, "Placement preserves its home and unique discovered replicas");
                lock (gate)
                {
                    var changed = current with { ReplicaRegions = replicas };
                    databases[current.Id] = changed;
                    return Results.Json(changed);
                }
            }
            if (parts.Length == 8 && http.Request.Method == "DELETE")
            {
                Require(acting == subject, "Official deletion SDK acting subject");
                lock (gate)
                {
                    databases.Remove(current.Id);
                    var prefix = project + "/" + current.Name + "/";
                    foreach (var collection in collections.Keys.Where(name => name.StartsWith(prefix, StringComparison.Ordinal)).ToArray())
                        collections.Remove(collection);
                }
                return Results.NoContent();
            }
            if (parts.Length == 9 && parts[8] == "collections")
            {
                lock (gate) return Results.Json(new
                {
                    items = collections.Where(item => item.Key.StartsWith(project + "/" + current.Name + "/", StringComparison.Ordinal)).Select(item =>
                    new { name = item.Key.Split('/')[2], documents = item.Value.Count, bytes = item.Value.Values.Sum(value => (long)System.Text.Encoding.UTF8.GetByteCount(value.Document.GetRawText())) }).ToArray()
                });
            }
            throw new InvalidOperationException("Unsupported management protocol route");
        }

        // Frozen producer b4919bd3 backup contract: legacy authenticated ID
        // aliases, full-only policy, complete snapshots, new-database restore.
        // The test producer really copies its stored snapshot; it is not live
        // engine/storage admission or scheduler qualification.
        private async Task<IResult> BackupsAsync(HttpContext http, string[] parts, string project, ManagedDatabase source)
        {
            var subject = (string)http.Items["producer-subject"]!;
            Require(http.Request.Headers["X-TerraBase-User-Id"] == subject, "Backup acting subject equals admitted bearer");
            if (role.Length == 0) return Error(404, "not_found");
            if (http.Request.Method != "GET" && role is not ("owner" or "admin")) return Error(403, "role_too_low");
            if (backupFailure == "outage") return Error(503, "unavailable");
            if (parts.Length == 9 && parts[8] == "backup-policy")
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
            if (parts.Length == 9 && parts[8] == "backups")
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
            Require(parts.Length == 11 && parts[8] == "backups" && parts[10] == "restore" && http.Request.Method == "POST", "Producer restore route");
            var request = await JsonAsync(http);
            Require(request.EnumerateObject().Count() == 1 && request.TryGetProperty("name", out _), "Producer restore accepts only optional name");
            lock (gate)
            {
                if (!snapshots.TryGetValue(parts[9], out var snapshot) || snapshot.Backup.DatabaseId != source.Id) return Error(404, "not_found");
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
            Credential key;
            lock (gate) key = credentials.Values.SingleOrDefault(value => token == "Bearer " + value.Secret && value.Key.ProjectId == project && value.Host == http.Request.Host.Host)
                ?? throw new InvalidOperationException("Documents use an enrolled scoped key, never the dashboard state key");
            lock (gate) Require(string.IsNullOrEmpty(key.Key.DatabaseId) || databases.Values.Any(value => value.Id == key.Key.DatabaseId && value.ProjectId == project && value.Name == database),
                "The credential reaches only its producer-bound database");
            var read = http.Request.Method == "GET" || parts.LastOrDefault() == "query";
            if (!key.Key.Scopes.Contains(read ? "documents:read" : "documents:write", StringComparer.Ordinal)) return Error(403, "forbidden_scope");
            requests.Enqueue(new(http.Request.Method, raw, token, http.Request.ContentType ?? "", http.Request.Headers.IfMatch.ToString(), http.Request.Headers.IfNoneMatch.ToString(), http.Request.QueryString.Value ?? ""));
            Require(parts[5] == "collections", "SDK collection path");
            var prefix = project + "/" + database + "/";
            if (parts.Length == 6)
            {
                if (http.Request.Method == "GET")
                {
                    Require(http.Request.Query["limit"] == "200", "Collection list uses bounded SDK pagination");
                    lock (gate) return Results.Json(new { items = collections.Keys.Where(name => name.StartsWith(prefix, StringComparison.Ordinal)).Select(name => new { name = name[prefix.Length..], created_at = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds().ToString() }).ToArray(), next_cursor = (string?)null });
                }
                var body = await JsonAsync(http); var name = body.GetProperty("name").GetString()!;
                lock (gate) { if (!collections.TryAdd(prefix + name, [])) return Error(409, "name_taken"); }
                return Results.Json(new { name, created_at = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds().ToString() }, statusCode: 201);
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
        private sealed record Credential(ProjectCredential Key, string Secret, string Host);
        private sealed record RequestObservation(string Method, string Path, string Authorization, string ContentType, string IfMatch, string IfNoneMatch, string Query);
    }
}
