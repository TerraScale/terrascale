using System.Collections.Concurrent;
using System.Globalization;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Server.Kestrel.Core;
using Microsoft.AspNetCore.WebUtilities;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;
using TerraScale.Dashboard.Features.Auth;
using TerraScale.Dashboard.Features.Billing;
using TerraScale.Dashboard.Platform.Persistence;
using TerraScale.DotNet;

namespace TerraScale.Dashboard.E2E;

// An external, test-only HTTP producer for the production state adapter and
// unmodified official Documents SDK. The producer-seeded platform database is
// fixture-owned; this does not qualify OCI Vault secret delivery or a live deployment.
public sealed partial class StateSdkScenario : IAsyncDisposable
{
    private readonly string root;
    private readonly DemoEnvironment environment = new();
    private readonly StateProducer protocol = new();
    private readonly List<StateTransport> transports = [];
    private readonly Dictionary<HttpClient, CookieContainer> cookieJars = [];
    private WebApplication? producer;
    private WebApplication? first;
    private WebApplication? second;
    private DeliveredIdentity? identity;
    public Uri ProviderOrigin { get; private set; } = null!;
    public Uri Origin { get; private set; } = null!;
    public Uri ReplicaOrigin { get; private set; } = null!;
    public string? StartupError { get; private set; }
    // Requests to any destination other than the state listener that carried the peer credential.
    public int ForeignCredentials => foreignCredentials;
    private int foreignCredentials;

    private StateSdkScenario(string root) => this.root = root;

    public static async Task<StateSdkScenario> PrepareAsync(string root)
    {
        var scenario = new StateSdkScenario(root);
        try
        {
            var builder = WebApplication.CreateBuilder();
            builder.WebHost.ConfigureKestrel(options => options.Listen(IPAddress.Loopback, 0,
                listener => listener.Protocols = HttpProtocols.Http2));
            builder.Logging.ClearProviders();
            scenario.producer = builder.Build();
            scenario.producer.MapMethods("/{**path}", ["GET", "POST", "PUT", "DELETE"], (Func<HttpContext, Task<IResult>>)scenario.protocol.DispatchAsync);
            await scenario.producer.StartAsync();
            scenario.ProviderOrigin = Address(scenario.producer);
            return scenario;
        }
        catch { await scenario.DisposeAsync(); throw; }
    }

    public HttpClient CreateClient(bool replica = false) => Client(replica ? ReplicaOrigin : Origin);
    public HttpClient CreateProviderClient()
    {
        var client = Client(ProviderOrigin);
        client.DefaultRequestVersion = HttpVersion.Version20;
        client.DefaultVersionPolicy = HttpVersionPolicy.RequestVersionExact;
        return client;
    }
    public HttpClient CreateReplacementClient(HttpClient previous, bool replica = false) =>
        Client(replica ? ReplicaOrigin : Origin, cookieJars[previous]);
    private HttpClient Client(Uri origin, CookieContainer? cookies = null)
    {
        cookies ??= new();
        var client = new HttpClient(new HttpClientHandler { AllowAutoRedirect = false, CookieContainer = cookies })
        { BaseAddress = origin, Timeout = TimeSpan.FromSeconds(30) };
        cookieJars[client] = cookies;
        return client;
    }

    public async Task<bool> StartHostsAsync(bool simultaneous = false)
    {
        first = Host(); second = Host(); StartupError = null;
        try
        {
            if (simultaneous) await Task.WhenAll(first.StartAsync(), second.StartAsync());
            else { await first.StartAsync(); await second.StartAsync(); }
            Origin = Address(first); ReplicaOrigin = Address(second);
            return true;
        }
        catch (Exception error) when (error is HttpRequestException or IOException or JsonException)
        {
            StartupError = error.GetType().Name;
            await StopHostsAsync();
            return false;
        }
    }

    public async Task<bool> RestartAsync()
    {
        await StopHostsAsync();
        return await StartHostsAsync();
    }

    private WebApplication Host()
    {
        var app = environment.CreateTestHost(root, 0, "http://127.0.0.1", builder => ConfigureState(builder));
        MapSqlCleanupRoutes(app);
        return app;
    }

    internal void ConfigureState(WebApplicationBuilder builder, Func<IHttpClientFactory, IHttpClientFactory>? decorate = null)
    {
        var previousIdentity = (IDashboardIdentity)builder.Services.Last(service => service.ServiceType == typeof(IDashboardIdentity)).ImplementationInstance!;
        identity ??= new(previousIdentity, protocol);
        var fallback = (IHttpClientFactory)builder.Services.Last(service => service.ServiceType == typeof(IHttpClientFactory)).ImplementationInstance!;
        var transport = new StateTransport(ProviderOrigin, fallback, this);
        transports.Add(transport);
        builder.Services.Replace(ServiceDescriptor.Singleton<IDashboardIdentity>(identity));
        // Only the network hop is replaced: the state client keeps its production handler pipeline (TerraBasePeerCredential).
        builder.Services.AddHttpClient(TerraBaseDashboardState.ClientName).ConfigurePrimaryHttpMessageHandler(() =>
        {
            var stateTransport = new StateTransport(ProviderOrigin, fallback, this);
            lock (transports) transports.Add(stateTransport);
            return stateTransport;
        });
        builder.Services.Replace(ServiceDescriptor.Singleton<IHttpClientFactory>(services =>
        {
            var factory = new StateFactory(transport, services.GetRequiredService<IHttpMessageHandlerFactory>());
            return decorate is null ? factory : decorate(factory);
        }));
        builder.Services.Replace(ServiceDescriptor.Singleton<IDashboardState, TerraBaseDashboardState>());
    }

    // Gives the only stored organization an active paid plan, as a reconciled checkout does, and returns its id.
    public async Task<string> UpgradeOnlyOrganizationAsync(string planId)
    {
        var state = first!.Services.GetRequiredService<IDashboardState>();
        var document = (await state.ListAsync(AccountService.OrganizationsCollection, default)).Single();
        var organization = AccountService.Read<OrganizationRecord>(document);
        var stored = await state.GetAsync(AccountService.OrganizationsCollection, organization.Id, default) ?? throw new IOException("The organization is unavailable.");
        organization.PlanId = planId;
        organization.BillingStatus = "active";
        await state.PutAsync(AccountService.OrganizationsCollection, organization.Id, JsonSerializer.SerializeToElement(organization),
            TerraScale.DotNet.TerraBase.TerraBasePrecondition.Matches(stored.Version), default);
        return organization.Id;
    }

    // One pass of the production reconciler (test hosts run it on demand, never on its timer).
    public Task ReconcilePlansAsync() => ActivatorUtilities.CreateInstance<PlanReconciler>(first!.Services).RunOnceAsync(default);

    // Sends through the running host's credentialed state client to a destination other than the state listener.
    public async Task<string?> SendStateClientElsewhereAsync(Uri destination)
    {
        using var client = first!.Services.GetRequiredService<IHttpClientFactory>().CreateClient(TerraBaseDashboardState.ClientName);
        try { using var response = await client.GetAsync(destination); return null; }
        catch (HttpRequestException error) { return error.GetType().Name; }
    }

    // The handler chain production builds for the state client, without any test transport.
    public (string[] Handlers, bool FollowsRedirects) ProductionStateHandlers()
    {
        using var app = new DemoEnvironment().CreateHost(root, 0, "http://127.0.0.1");
        var handlers = new List<string>();
        HttpMessageHandler? handler = app.Services.GetRequiredService<IHttpMessageHandlerFactory>().CreateHandler(TerraBaseDashboardState.ClientName);
        for (; handler is DelegatingHandler delegating; handler = delegating.InnerHandler) handlers.Add(delegating.GetType().Name);
        return ([.. handlers], handler is not SocketsHttpHandler { AllowAutoRedirect: false });
    }

    private async Task StopHostsAsync()
    {
        if (second is not null) { await second.StopAsync(); await second.DisposeAsync(); second = null; }
        if (first is not null) { await first.StopAsync(); await first.DisposeAsync(); first = null; }
    }

    private static Uri Address(WebApplication app) => new(app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!
        .Addresses.Single().Replace("0.0.0.0", "127.0.0.1", StringComparison.Ordinal));

    public async ValueTask DisposeAsync()
    {
        protocol.Release();
        await StopHostsAsync();
        if (producer is not null) { await producer.StopAsync(); await producer.DisposeAsync(); producer = null; }
        foreach (var transport in transports) transport.Dispose();
        transports.Clear();
    }

    private sealed class StateFactory(StateTransport transport, IHttpMessageHandlerFactory handlers) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => name == TerraBaseDashboardState.ClientName
            ? new(handlers.CreateHandler(name), disposeHandler: false) : new(transport, disposeHandler: false);
    }

    private sealed class StateTransport(Uri origin, IHttpClientFactory fallback, StateSdkScenario owner) : HttpMessageHandler
    {
        private readonly HttpClient local = new(new HttpClientHandler { AllowAutoRedirect = false });
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var uri = request.RequestUri!;
            // Development reaches TerraBase's private listener on loopback port 8081: cleartext HTTP/2 with the peer credential.
            var stateRequest = uri.Host == "127.0.0.1" && uri.Port == 8081;
            if (!stateRequest && request.Headers.Contains(TerraBasePeerCredential.Header))
            {
                Interlocked.Increment(ref owner.foreignCredentials);
                throw new HttpRequestException("The peer credential left for a destination other than the state listener.");
            }
            if (stateRequest && (uri.Scheme != "http" || uri.UserInfo.Length != 0 || request.Version != HttpVersion.Version20 ||
                request.VersionPolicy != HttpVersionPolicy.RequestVersionExact || request.Headers.Authorization is not null))
                throw new HttpRequestException("Invalid private-listener request in the state SDK protocol fixture.");
            using var forwarded = new HttpRequestMessage(request.Method, stateRequest ? new Uri(origin, uri.PathAndQuery) : uri)
            { Version = request.Version, VersionPolicy = request.VersionPolicy };
            foreach (var header in request.Headers) forwarded.Headers.TryAddWithoutValidation(header.Key, header.Value);
            foreach (var option in request.Options) forwarded.Options.Set(new HttpRequestOptionsKey<object?>(option.Key), option.Value);
            if (stateRequest) forwarded.Headers.Host = uri.Authority;
            if (request.Content is { } content)
            {
                forwarded.Content = new ByteArrayContent(await content.ReadAsByteArrayAsync(cancellationToken));
                foreach (var header in content.Headers) forwarded.Content.Headers.TryAddWithoutValidation(header.Key, header.Value);
            }
            using var other = stateRequest ? null : fallback.CreateClient("platform");
            var response = await (other ?? local).SendAsync(forwarded, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
            response.RequestMessage = request;
            return response;
        }
        protected override void Dispose(bool disposing) { if (disposing) local.Dispose(); base.Dispose(disposing); }
    }

    private sealed class DeliveredIdentity(IDashboardIdentity underlying, StateProducer protocol) : IDashboardIdentity
    {
        public async Task<DashboardCredentials> ReadAsync(CancellationToken cancellationToken)
        {
            var value = await underlying.ReadAsync(cancellationToken);
            if (protocol.Mode == "secret-delivery-unavailable") throw new IOException("The test-only secret delivery is unavailable.");
            if (protocol.Mode == "invalid-auth-client-secret") value = value with { AuthClientSecret = "" };
            if (protocol.Mode == "invalid-session-key") value = value with { SessionEncryptionKey = "invalid" };
            if (protocol.Mode == "invalid-peer-secret") value = value with { TerraBasePeerSecret = value.TerraBasePeerSecret[..63] + "g" };
            value.Validate();
            return value;
        }
    }

    private sealed partial class StateProducer
    {
        public const string Project = "00000000000000000000000005";
        private readonly object gate = new();
        private readonly TerraScaleIdGenerator ids = new();
        private readonly Dictionary<string, Collection> collections = new(StringComparer.Ordinal);
        private readonly ConcurrentQueue<string> failures = new();
        private readonly Dictionary<string, string> organizationPlans = new(StringComparer.Ordinal);
        private TaskCompletionSource? collectionGate;
        private TaskCompletionSource? documentGate;
        private int pausedCreates;
        private int pausedWrites;
        private int databasePuts;
        private int databaseCreates;
        private int collectionPosts;
        private int collectionConflicts;
        private int documentWrites;
        private int preconditionFailures;
        private int privateRequests;
        private int redirectTargetRequests;
        private long offset;
        private readonly string? databaseId = "00000000000000000000000004";
        private string mode = "";
        public string Mode { get { lock (gate) return mode; } }
        public void Release() { collectionGate?.TrySetResult(); documentGate?.TrySetResult(); }

        public async Task<IResult> DispatchAsync(HttpContext http)
        {
            try
            {
                var path = http.Request.Path.Value!;
                if (path == "/__test/observations") return Observe();
                if (path == "/__test/control") return await ControlAsync(http);
                if (path == "/__test/redirected") { Interlocked.Increment(ref redirectTargetRequests); return Results.NoContent(); }
                Require(http.Request.Host.Host == "127.0.0.1" && http.Request.Host.Port == 8081, "The state SDK must use the private listener origin.");
                Require(http.Request.Headers.TryGetValue(TerraBasePeerCredential.Header, out var peer) && peer.Count == 1 && peer[0] == TestIdentity.PeerSecret,
                    "Every private-listener request carries exactly one delivered peer credential.");
                if (Mode == "redirect") return Results.Redirect("http://127.0.0.1:" + http.Connection.LocalPort + "/__test/redirected", preserveMethod: true);
                http.Response.Headers["X-TerraBase-Region"] = "gru";
                if (Mode == "region-unavailable") return Error(503, "region_unavailable");
                if (Mode == "authorization") return Error(401, "unauthorized");
                Require(!http.Request.Headers.ContainsKey("Authorization"), "The private listener takes no credential.");
                Interlocked.Increment(ref privateRequests);
                if (Mode == "forbidden" && http.Request.Method != "GET") return Error(403, "forbidden");
                var parts = path.Split('/', StringSplitOptions.RemoveEmptyEntries);
                // TerraBasePlanSync publishes each organization's plan on the same private listener, and each project's while
                // TerraBase may not serve the organization route yet.
                if (parts is ["admin", "organizations" or "projects", var planOwner, "plan"])
                {
                    using var plan = await JsonDocument.ParseAsync(http.Request.Body, cancellationToken: http.RequestAborted);
                    var tier = plan.RootElement.TryGetProperty("plan", out var value) ? value.GetString() : null;
                    Require(http.Request.Method == "PUT" && TerraScaleIds.IsValid(planOwner) && tier is "hobby" or "starter" or "pro" or "team" or "enterprise",
                        "A plan publish is a PUT of a known plan for a canonical organization or project id.");
                    if (parts[1] == "organizations")
                        lock (gate)
                        {
                            // Like TerraBase, an organization without storage keeps the default Hobby without a record.
                            if (tier == "hobby" && !organizationPlans.ContainsKey(planOwner)) return Error(404, "not_found");
                            organizationPlans[planOwner] = tier!;
                        }
                    return Results.Json(new { plan = tier });
                }
                Require(parts.Length >= 5 && parts[0] == "v1" && parts[1] == "projects" && parts[2] == Project &&
                    parts[3] == "databases" && parts[4] == "ts-dashboard", "State namespace must use only its delivered canonical project and fixed database.");
                // TerraBase seeds ts-dashboard itself; the dashboard never creates it.
                if (parts.Length == 5) { Interlocked.Increment(ref databasePuts); Interlocked.Increment(ref databaseCreates); Require(false, "The dashboard must not create or replace its seeded database."); }
                if (Mode == "project-missing") return Error(404, "unknown_database");
                Require(parts.Length >= 6 && parts[5] == "collections", "Only genuine collection and by-key document routes are admitted.");
                if (parts.Length == 6 && http.Request.Method == "POST") return await CreateCollectionAsync(http);
                Require(parts.Length >= 7, "A collection name is required.");
                if (parts.Length == 7 && http.Request.Method == "GET")
                {
                    if (Mode == "wrong-missing-code") return Error(404, "unknown_database");
                    lock (gate) return collections.TryGetValue(parts[6], out var collection)
                        ? Results.Json(CollectionBody(parts[6], collection)) : Error(404, "unknown_collection");
                }
                if (parts.Length == 8 && parts[7] == "query" && http.Request.Method == "POST") return await QueryAsync(parts[6], http);
                if (parts.Length == 8 && parts[7] == "documents" && parts[6] == "dashboard_sql_workspaces") Interlocked.Increment(ref sqlWorkspaceLists);
                if (parts.Length == 8 && parts[7] == "documents" && http.Request.Method == "GET")
                    lock (gate) return collections.TryGetValue(parts[6], out var listed)
                        ? Results.Json(new
                        {
                            items = listed.Documents.Where(entry => !entry.Value.Deleted).OrderBy(entry => entry.Key, StringComparer.Ordinal)
                            .Select(entry => new { id = entry.Key, version = entry.Value.Version, state = "value", document = entry.Value.Value }).ToArray(),
                            next_cursor = (string?)null
                        })
                        : Error(404, "unknown_collection");
                Require(parts.Length == 9 && parts[7] == "documents" && parts[8] == "by-key" && http.Request.Query.Keys.SequenceEqual(new[] { "id" }),
                    "The official state SDK must use the genuine by-key point route and one opaque id parameter.");
                var id = http.Request.Query["id"].ToString();
                Require(TerraScaleIds.IsValid(id), "State document ids are canonical ULIDs.");
                if (http.Request.Method == "PUT" && parts[6] == "dashboard_accounts" && documentGate is { } held)
                { Interlocked.Increment(ref pausedWrites); await held.Task.WaitAsync(http.RequestAborted); }
                JsonElement? body = null;
                if (http.Request.Method == "PUT")
                {
                    using var json = await JsonDocument.ParseAsync(http.Request.Body, cancellationToken: http.RequestAborted);
                    Require(json.RootElement.ValueKind == JsonValueKind.Object, "A document write contains the actual JSON object.");
                    body = json.RootElement.Clone();
                }
                lock (gate)
                {
                    if (!collections.TryGetValue(parts[6], out var collection)) return Error(404, "unknown_collection");
                    var existing = collection.Documents.GetValueOrDefault(id);
                    if (http.Request.Method == "GET")
                    {
                        if (existing is null || existing.Deleted) return Error(404, "not_found");
                        http.Response.Headers.ETag = '"' + existing.Version + '"';
                        return Results.Json(new { id, version = existing.Version, state = "value", document = existing.Value });
                    }
                    Require(http.Request.Method is "PUT" or "DELETE", "Only production state point read/write/delete operations are admitted.");
                    if (http.Request.Method == "DELETE" && parts[6] == "dashboard_sql_workspaces" && Mode == "sql-delete-unavailable") return Error(503, "unavailable");
                    var absent = http.Request.Headers.IfNoneMatch == "*";
                    var match = http.Request.Headers.IfMatch.ToString();
                    Require(absent != (match.Length > 0), "Every state mutation requires exactly one producer precondition.");
                    if (absent && existing is { Deleted: false } || match.Length > 0 && (existing is null || match != '"' + existing.Version + '"'))
                    { preconditionFailures++; return Error(412, "precondition_failed"); }
                    if (http.Request.Method == "DELETE" && existing is null) return Error(404, "not_found");
                    var version = "tbv2-" + WebEncoders.Base64UrlEncode(RandomNumberGenerator.GetBytes(32));
                    var deleted = http.Request.Method == "DELETE";
                    collection.Documents[id] = new(version, body ?? existing!.Value, deleted);
                    documentWrites++;
                    http.Response.Headers.ETag = '"' + version + '"';
                    return Results.Json(new { id, version, offset = (++offset).ToString(CultureInfo.InvariantCulture), durability = "local_durable" },
                        statusCode: !deleted && (existing is null || existing.Deleted) ? 201 : 200);
                }
            }
            catch (Exception error) when (error is InvalidOperationException or JsonException)
            { failures.Enqueue(error.Message); return Error(500, "test_contract_failure"); }
        }

        private async Task<IResult> CreateCollectionAsync(HttpContext http)
        {
            using var json = await JsonDocument.ParseAsync(http.Request.Body, cancellationToken: http.RequestAborted);
            Require(json.RootElement.EnumerateObject().Count() == 1 && json.RootElement.TryGetProperty("name", out _), "Collection creation accepts only name.");
            var name = json.RootElement.GetProperty("name").GetString()!;
            Require(name.Length is > 0 and <= 63 && name[0] is >= 'a' and <= 'z' && name.All(c => c is >= 'a' and <= 'z' or >= '0' and <= '9' or '_' or '-'), "Collection names obey the producer grammar.");
            Interlocked.Increment(ref collectionPosts);
            if (collectionGate is { } held && name == "sessions")
            { Interlocked.Increment(ref pausedCreates); await held.Task.WaitAsync(http.RequestAborted); }
            lock (gate)
            {
                if (Mode == "interrupted" && name == "auth-flows") return Error(503, "unavailable");
                if (Mode == "other-conflict") return Error(409, "schema_requires_empty_collection");
                if (collections.ContainsKey(name)) { collectionConflicts++; return Error(409, "name_taken"); }
                var collection = new Collection(ids.NewId(), DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
                collections[name] = collection;
                if (Mode == "bad-conflict-reread") { collectionConflicts++; return Error(409, "name_taken"); }
                return Results.Json(CollectionBody(name, collection), statusCode: 201);
            }
        }

        private object CollectionBody(string name, Collection collection) => new
        { id = collection.Id, name = Mode is "wrong-collection" or "bad-conflict-reread" ? "foreign_collection" : name, created_at = collection.CreatedAt.ToString(CultureInfo.InvariantCulture) };

        private async Task<IResult> ControlAsync(HttpContext http)
        {
            using var document = await JsonDocument.ParseAsync(http.Request.Body, cancellationToken: http.RequestAborted);
            var body = document.RootElement;
            lock (gate)
            {
                switch (body.GetProperty("command").GetString())
                {
                    case "mode": mode = body.GetProperty("value").GetString()!; break;
                    case "pause-collections": collectionGate = new(TaskCreationOptions.RunContinuationsAsynchronously); break;
                    case "release-collections": collectionGate?.TrySetResult(); collectionGate = null; break;
                    case "pause-account-writes": pausedWrites = 0; documentGate = new(TaskCreationOptions.RunContinuationsAsynchronously); break;
                    case "release-account-writes": documentGate?.TrySetResult(); documentGate = null; break;
                    case "drop-collection": collections.Remove(body.GetProperty("name").GetString()!); break;
                    case "wipe-plans": organizationPlans.Clear(); break;
                    default: return Results.BadRequest();
                }
            }
            return Results.NoContent();
        }

        private IResult Observe()
        {
            lock (gate) return Results.Json(new
            {
                databaseId,
                databasePuts,
                databaseCreates,
                collectionPosts,
                collectionConflicts,
                documentWrites,
                preconditionFailures,
                pausedCreates,
                pausedWrites,
                privateRequests,
                redirectTargetRequests,
                cleanupQueries = cleanupQueries.ToArray(),
                sqlWorkspaceLists,
                failures = failures.ToArray(),
                organizationPlans = organizationPlans.ToDictionary(),
                collections = collections.OrderBy(item => item.Key, StringComparer.Ordinal).Select(item => new
                { name = item.Key, id = item.Value.Id, createdAt = item.Value.CreatedAt, documents = item.Value.Documents.Count(entry => !entry.Value.Deleted) }).ToArray(),
                documentDigest = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(string.Join('\n', collections.OrderBy(item => item.Key, StringComparer.Ordinal)
                    .SelectMany(collection => collection.Value.Documents.OrderBy(document => document.Key, StringComparer.Ordinal)
                        .Select(document => collection.Key + "/" + document.Key + ":" + document.Value.Version + ":" + document.Value.Deleted + ":" + document.Value.Value.GetRawText()))))))
            });
        }

        private static IResult Error(int status, string code) => Results.Json(new { error = new { code, message = "Test-only state producer refusal.", op = (int?)null } }, statusCode: status);
        private static void Require(bool condition, string message) { if (!condition) throw new InvalidOperationException(message); }
        private sealed record Document(string Version, JsonElement Value, bool Deleted);
        private sealed class Collection(string id, long createdAt)
        {
            public string Id { get; } = id;
            public long CreatedAt { get; } = createdAt;
            public Dictionary<string, Document> Documents { get; } = new(StringComparer.Ordinal);
        }
    }
}
