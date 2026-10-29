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
using TerraScale.Dashboard.Features.Billing;
using TerraScale.Dashboard.Platform.Persistence;
using TerraScale.Dashboard.Platform.Mcp;
using TerraScale.DotNet;
using TerraScale.DotNet.TerraBase;

namespace TerraScale.Dashboard.E2E
{
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
        public TimeProvider? RegistrationTime { get; set; }
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

        public HttpClient CreateClient(bool replica = false)
        {
            return Client(replica ? ReplicaOrigin : Origin);
        }

        public HttpClient CreateProviderClient()
        {
            var client = Client(ProviderOrigin);
            client.DefaultRequestVersion = HttpVersion.Version20;
            client.DefaultVersionPolicy = HttpVersionPolicy.RequestVersionExact;
            return client;
        }
        public HttpClient CreateReplacementClient(HttpClient previous, bool replica = false)
        {
            return Client(replica ? ReplicaOrigin : Origin, cookieJars[previous]);
        }

        private HttpClient Client(Uri origin, CookieContainer? cookies = null)
        {
            cookies ??= new();
            var client = new HttpClient(new HttpClientHandler { ServerCertificateCustomValidationCallback = TestHttps.Validate, AllowAutoRedirect = false, CookieContainer = cookies })
            { BaseAddress = origin, Timeout = TimeSpan.FromSeconds(30) };
            cookieJars[client] = cookies;
            return client;
        }

        public async Task<bool> StartHostsAsync(bool simultaneous = false)
        {
            first = Host(); second = Host(); StartupError = null;
            try
            {
                if (simultaneous)
                {
                    await Task.WhenAll(first.StartAsync(), second.StartAsync());
                }
                else { await first.StartAsync(); await second.StartAsync(); }
                Origin = Address(first); ReplicaOrigin = Address(second);
                // TerraBase failures never stop startup: an app that could not initialize its state keeps running, not ready,
                // and retries until the producer recovers.
                if (Initialized)
                {
                    return true;
                }

                StartupError = "not-initialized";
                return false;
            }
            catch (Exception error) when (error is HttpRequestException or IOException or JsonException)
            {
                StartupError = error.GetType().Name;
                await StopHostsAsync();
                return false;
            }
        }

        public bool Initialized => new[] { first, second }.All(app => app?.Services.GetRequiredService<DashboardStateInitializer>().Initialized == true);

        // Waits for both running replicas to finish state initialization on their own, without a restart.
        public async Task<bool> WaitInitializedAsync(TimeSpan timeout)
        {
            var deadline = DateTimeOffset.UtcNow + timeout;
            while (!Initialized && DateTimeOffset.UtcNow < deadline)
            {
                await Task.Delay(100);
            }

            return Initialized;
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
            // Test-binary-only admission harness; exercises the running hosts' real persistence adapter over HTTP.
            app.MapPost("/__test/state-admission", async (HttpContext context, IDashboardState state) =>
            {
                using var json = await JsonDocument.ParseAsync(context.Request.Body);
                var body = json.RootElement; var command = body.GetProperty("command").GetString();
                const string clients = "dashboard_mcp_oauth_clients", grants = "dashboard_mcp_oauth_codes";
                var ct = context.RequestAborted;
                var filter = JsonSerializer.SerializeToElement(new { eq = new object[] { "/kind", "client" } });
                var budget = await state.GetAsync(grants, "budget", ct);
                var value = JsonSerializer.SerializeToElement(new { count = (budget?.Document.GetProperty("count").GetInt32() ?? -1) + 1 });
                var key = body.TryGetProperty("key", out var named) ? named.GetString()! : "";
                try
                {
                    if (command == "page")
                    {
                        return Results.Json(await state.ReadPageAsync(clients, body.GetProperty("filtered").GetBoolean() ? filter : null,
                        1, body.TryGetProperty("cursor", out var cursor) ? cursor.GetString() : null, ct));
                    }

                    if (command == "read")
                    {
                        return Results.Json(new { client = await state.GetAsync(clients, key, ct), budget });
                    }

                    if (command == "read-capacity")
                    {
                        return Results.Json(new
                    {
                        client = await state.GetAsync(clients, key, ct),
                        grants = await Task.WhenAll(Enumerable.Range(0, 3).Select(index => state.GetAsync(grants, key + "/" + index, ct)))
                    });
                    }

                    if (command == "remove")
                    {
                        var page = await state.ReadPageAsync(clients, filter, 1, null, ct);
                        return Results.Json(await state.TransactAsync([DashboardStateOperation.DeleteListed(clients, page.Items.Single())], ct));
                    }
                    if (command == "delete")
                    {
                        return Results.Json(await state.TransactAsync([DashboardStateOperation.Delete(grants, "budget", budget!.Version)], ct));
                    }

                    var client = JsonSerializer.SerializeToElement(new { kind = "client", key });
                    IReadOnlyList<DashboardStateOperation> operations = command switch
                    {
                        "too-many" => Enumerable.Repeat(DashboardStateOperation.AssertAbsent(clients, "missing"), 1025).ToArray(),
                        "too-many-counts" => Enumerable.Repeat(DashboardStateOperation.AssertCount(clients, null, null, 2), 17).ToArray(),
                        "too-large" => [DashboardStateOperation.Create(clients, key, JsonSerializer.SerializeToElement(new { text = new string('é', 2 * 1024 * 1024) }))],
                        "wildcard" => [DashboardStateOperation.Replace(grants, "budget", value, "*")],
                        "capacity" => new[] { DashboardStateOperation.Create(clients, key, client) }.Concat(Enumerable.Range(0, 3)
                            .Select(index => DashboardStateOperation.Create(grants, key + "/" + index, JsonSerializer.SerializeToElement(new { text = new string('x', 800 * 1024) })))).ToArray(),
                        "noise" => [DashboardStateOperation.Create(clients, key, JsonSerializer.SerializeToElement(new { kind = "unrelated", key }))],
                        "absent-conflict" => [DashboardStateOperation.Create(clients, key, client), DashboardStateOperation.AssertAbsent(clients, "natural-seed")],
                        "stale-delete" => [DashboardStateOperation.Delete(grants, "budget", "stale")],
                        "seed" => [DashboardStateOperation.Create(grants, "budget", value), DashboardStateOperation.Create(clients, key, client)],
                        "assert" => [DashboardStateOperation.Assert(clients, key, filter), DashboardStateOperation.AssertAbsent(clients, "missing"), DashboardStateOperation.AssertCount(clients, filter, 1, 2)],
                        "duplicate" => [DashboardStateOperation.Assert(clients, key, filter), DashboardStateOperation.Delete(clients, key, "stale")],
                        "rollback" => [DashboardStateOperation.Create(clients, key, client), DashboardStateOperation.Replace(grants, "budget", value, "stale")],
                        "admit" => [DashboardStateOperation.AssertCount(clients, null, null, 1), DashboardStateOperation.Create(clients, key, client),
                            DashboardStateOperation.Replace(grants, "budget", value, budget!.Version), DashboardStateOperation.Create(grants, key, client)],
                        _ => throw new InvalidOperationException("Unknown admission harness command.")
                    };
                    return Results.Json(await state.TransactAsync(operations, ct));
                }
                catch (TerraBaseException error) { return Results.Json(new { error.ErrorCode, error.OperationIndex }, statusCode: (int)error.StatusCode!.Value); }
                catch (ArgumentException) { return Results.BadRequest(); }
            });
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
                lock (transports)
                {
                    transports.Add(stateTransport);
                }

                return stateTransport;
            });
            builder.Services.Replace(ServiceDescriptor.Singleton<IHttpClientFactory>(services =>
            {
                var factory = new StateFactory(transport, services.GetRequiredService<IHttpMessageHandlerFactory>());
                return decorate is null ? factory : decorate(factory);
            }));
            builder.Services.Replace(ServiceDescriptor.Singleton<IDashboardState, TerraBaseDashboardState>());
            if (RegistrationTime is not null)
            {
                builder.Services.AddSingleton(RegistrationTime);
            }
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
        public Task PauseRegistrationCompletion()
        {
            return protocol.PauseRegistrationCompletion();
        }

        public void ReleaseRegistrationCompletion()
        {
            protocol.ReleaseRegistrationCompletion();
        }

        public IDashboardState RegistrationState => first!.Services.GetRequiredService<IDashboardState>();
        public McpRegistrationReclaimer RegistrationReclaimer()
        {
            return ActivatorUtilities.CreateInstance<McpRegistrationReclaimer>(first!.Services);
        }

        public Task ReconcilePlansAsync()
        {
            return ActivatorUtilities.CreateInstance<PlanReconciler>(first!.Services).RunOnceAsync(default);
        }

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
            for (; handler is DelegatingHandler delegating; handler = delegating.InnerHandler)
            {
                handlers.Add(delegating.GetType().Name);
            }

            return ([.. handlers], handler is not SocketsHttpHandler { AllowAutoRedirect: false });
        }

        private async Task StopHostsAsync()
        {
            if (second is not null) { await second.StopAsync(); await second.DisposeAsync(); second = null; }
            if (first is not null) { await first.StopAsync(); await first.DisposeAsync(); first = null; }
        }

        private static Uri Address(WebApplication app)
        {
            return new(app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!
            .Addresses.Single().Replace("0.0.0.0", "127.0.0.1", StringComparison.Ordinal));
        }

        public async ValueTask DisposeAsync()
        {
            protocol.Release();
            await StopHostsAsync();
            if (producer is not null) { await producer.StopAsync(); await producer.DisposeAsync(); producer = null; }
            foreach (var transport in transports)
            {
                transport.Dispose();
            }

            transports.Clear();
        }

        private sealed class StateFactory(StateTransport transport, IHttpMessageHandlerFactory handlers) : IHttpClientFactory
        {
            public HttpClient CreateClient(string name)
            {
                return name == TerraBaseDashboardState.ClientName
                ? new(handlers.CreateHandler(name), disposeHandler: false) : new(transport, disposeHandler: false);
            }
        }

        private sealed class StateTransport(Uri origin, IHttpClientFactory fallback, StateSdkScenario owner) : HttpMessageHandler
        {
            private readonly HttpClient local = new(new HttpClientHandler { ServerCertificateCustomValidationCallback = TestHttps.Validate, AllowAutoRedirect = false });
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
                {
                    throw new HttpRequestException("Invalid private-listener request in the state SDK protocol fixture.");
                }

                using var forwarded = new HttpRequestMessage(request.Method, stateRequest ? new Uri(origin, uri.PathAndQuery) : uri)
                { Version = request.Version, VersionPolicy = request.VersionPolicy };
                foreach (var header in request.Headers)
                {
                    forwarded.Headers.TryAddWithoutValidation(header.Key, header.Value);
                }

                foreach (var option in request.Options)
                {
                    forwarded.Options.Set(new HttpRequestOptionsKey<object?>(option.Key), option.Value);
                }

                if (stateRequest)
                {
                    forwarded.Headers.Host = uri.Authority;
                }

                if (request.Content is { } content)
                {
                    forwarded.Content = new ByteArrayContent(await content.ReadAsByteArrayAsync(cancellationToken));
                    foreach (var header in content.Headers)
                    {
                        forwarded.Content.Headers.TryAddWithoutValidation(header.Key, header.Value);
                    }
                }
                using var other = stateRequest ? null : fallback.CreateClient("platform");
                var response = await (other ?? local).SendAsync(forwarded, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
                response.RequestMessage = request;
                return response;
            }
            protected override void Dispose(bool disposing) { if (disposing) { local.Dispose(); } base.Dispose(disposing); }
        }

        private sealed class DeliveredIdentity(IDashboardIdentity underlying, StateProducer protocol) : IDashboardIdentity
        {
            public async Task<DashboardCredentials> ReadAsync(CancellationToken cancellationToken)
            {
                var value = await underlying.ReadAsync(cancellationToken);
                if (protocol.Mode == "secret-delivery-unavailable")
                {
                    throw new IOException("The test-only secret delivery is unavailable.");
                }

                if (protocol.Mode == "invalid-auth-client-secret")
                {
                    value = value with { AuthClientSecret = "" };
                }

                if (protocol.Mode == "invalid-session-key")
                {
                    value = value with { SessionEncryptionKey = "invalid" };
                }

                if (protocol.Mode == "invalid-peer-secret")
                {
                    value = value with { TerraBasePeerSecret = value.TerraBasePeerSecret[..63] + "g" };
                }

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
            private readonly Dictionary<string, (string Collection, string? Filter, string Last)> cursors = [];
            private readonly Dictionary<string, string> organizationPlans = new(StringComparer.Ordinal);
            private int planPublishes;
            private TaskCompletionSource? collectionGate;
            private TaskCompletionSource? planGate;
            private TaskCompletionSource? documentGate;
            private TaskCompletionSource? transactionGate;
            private TaskCompletionSource? registrationGate;
            private TaskCompletionSource registrationPaused = new(TaskCreationOptions.RunContinuationsAsynchronously);
            public Task PauseRegistrationCompletion()
            { registrationGate = new(TaskCreationOptions.RunContinuationsAsynchronously); return registrationPaused.Task; }
            public void ReleaseRegistrationCompletion() { registrationGate?.TrySetResult(); registrationGate = null; }
            private int pausedTransactions;
            private int pausedCreates;
            private int pausedWrites;
            private int databasePuts;
            private int databaseCreates;
            private int collectionPosts;
            private int collectionLists;
            private int collectionConflicts;
            private int documentWrites;
            private int preconditionFailures;
            private int privateRequests;
            private int redirectTargetRequests;
            private long offset;
            private readonly string? databaseId = "00000000000000000000000004";
            private string mode = "";
            public string Mode { get { lock (gate) { return mode; } } }
            public void Release() { ReleaseRegistrationCompletion(); planGate?.TrySetResult(); collectionGate?.TrySetResult(); documentGate?.TrySetResult(); transactionGate?.TrySetResult(); }

            public async Task<IResult> DispatchAsync(HttpContext http)
            {
                try
                {
                    var path = http.Request.Path.Value!;
                    if (path == "/__test/observations")
                    {
                        return Observe();
                    }

                    if (path == "/__test/control")
                    {
                        return await ControlAsync(http);
                    }

                    if (path == "/__test/redirected") { Interlocked.Increment(ref redirectTargetRequests); return Results.NoContent(); }
                    // TerraBase unreachable: the connection drops before any answer, as a restarting or partitioned listener does.
                    if (Mode == "unreachable") { http.Abort(); return Results.Empty; }
                    Require(http.Request.Host.Host == "127.0.0.1" && http.Request.Host.Port == 8081, "The state SDK must use the private listener origin.");
                    Require(http.Request.Headers.TryGetValue(TerraBasePeerCredential.Header, out var peer) && peer.Count == 1 && peer[0] == TestIdentity.PeerSecret,
                        "Every private-listener request carries exactly one delivered peer credential.");
                    if (Mode == "redirect")
                    {
                        return Results.Redirect("http://127.0.0.1:" + http.Connection.LocalPort + "/__test/redirected", preserveMethod: true);
                    }

                    http.Response.Headers["X-TerraBase-Region"] = "gru";
                    if (Mode == "region-unavailable")
                    {
                        return Error(503, "region_unavailable");
                    }

                    if (Mode == "authorization")
                    {
                        return Error(401, "unauthorized");
                    }

                    Require(!http.Request.Headers.ContainsKey("Authorization"), "The private listener takes no credential.");
                    Interlocked.Increment(ref privateRequests);
                    if (Mode == "forbidden" && http.Request.Method != "GET")
                    {
                        return Error(403, "forbidden");
                    }

                    var parts = path.Split('/', StringSplitOptions.RemoveEmptyEntries);
                    // TerraBasePlanSync publishes each organization's plan on the same private listener, and each project's while
                    // TerraBase may not serve the organization route yet.
                    if (parts is ["admin", "organizations" or "projects", var planOwner, "plan"])
                    {
                        // A stalled private listener: the publisher's own deadline must end the wait, not this producer.
                        if (planGate is { } stalled)
                        {
                            await stalled.Task.WaitAsync(http.RequestAborted);
                        }

                        Interlocked.Increment(ref planPublishes);
                        using var plan = await JsonDocument.ParseAsync(http.Request.Body, cancellationToken: http.RequestAborted);
                        var tier = plan.RootElement.TryGetProperty("plan", out var value) ? value.GetString() : null;
                        Require(http.Request.Method == "PUT" && TerraScaleIds.IsValid(planOwner) && tier is "hobby" or "starter" or "pro" or "team" or "enterprise",
                            "A plan publish is a PUT of a known plan for a canonical organization or project id.");
                        if (parts[1] == "organizations")
                        {
                            lock (gate)
                            {
                                // Like TerraBase, an organization without storage keeps the default Hobby without a record.
                                if (tier == "hobby" && !organizationPlans.ContainsKey(planOwner))
                                {
                                    return Error(404, "not_found");
                                }

                                organizationPlans[planOwner] = tier!;
                            }
                        }

                        return Results.Json(new { plan = tier });
                    }
                    Require(parts.Length >= 5 && parts[0] == "v1" && parts[1] == "projects" && parts[2] == Project &&
                        parts[3] == "databases" && parts[4] == "ts-dashboard", "State namespace must use only its delivered canonical project and fixed database.");
                    // TerraBase seeds ts-dashboard itself; the dashboard never creates it.
                    if (parts.Length == 5) { Interlocked.Increment(ref databasePuts); Interlocked.Increment(ref databaseCreates); Require(false, "The dashboard must not create or replace its seeded database."); }
                    if (Mode == "project-missing")
                    {
                        return Error(404, "unknown_database");
                    }

                    if (parts is [_, _, _, _, _, "transactions"] && http.Request.Method == "POST")
                    {
                        return await TransactAsync(http);
                    }

                    if (parts is [_, _, _, _, _, "collections", var queryCollection, "query"] && http.Request.Method == "POST")
                    {
                        if (queryCollection is "dashboard_sql_workspaces" or "dashboard_sql_database_deletions")
                        {
                            return await QueryAsync(queryCollection, http);
                        }

                        using var query = await JsonDocument.ParseAsync(http.Request.Body);
                        var q = query.RootElement;
                        lock (gate)
                        {
                            return Page(queryCollection, q.GetProperty("limit").GetInt32(), q.TryGetProperty("cursor", out var c) ? c.GetString() : null,
                            q.TryGetProperty("filter", out var f) ? f : null);
                        }
                    }
                    Require(parts.Length >= 6 && parts[5] == "collections", "Only genuine collection and by-key document routes are admitted.");
                    if (parts.Length == 6 && http.Request.Method == "POST")
                    {
                        return await CreateCollectionAsync(http);
                    }

                    if (parts.Length == 6 && http.Request.Method == "GET")
                    {
                        Interlocked.Increment(ref collectionLists);
                        var offset = int.TryParse(http.Request.Query["cursor"], out var parsed) ? parsed : 0;
                        var size = Mode == "paged-collections" ? 10 : 200;
                        lock (gate)
                        {
                            var items = collections.OrderBy(item => item.Key, StringComparer.Ordinal).Skip(offset).Take(size).ToArray();
                            return Results.Json(new
                            {
                                items = items.Select(item => CollectionBody(item.Key, item.Value)),
                                next_cursor = offset + items.Length < collections.Count ? (offset + items.Length).ToString(CultureInfo.InvariantCulture) : null
                            });
                        }
                    }
                    Require(parts.Length >= 7, "A collection name is required.");
                    if (parts.Length == 7 && http.Request.Method == "GET")
                    {
                        if (Mode == "wrong-missing-code")
                        {
                            return Error(404, "unknown_database");
                        }

                        lock (gate)
                        {
                            return collections.TryGetValue(parts[6], out var collection)
                            ? Results.Json(CollectionBody(parts[6], collection)) : Error(404, "unknown_collection");
                        }
                    }
                    if (parts.Length == 8 && parts[7] == "documents" && parts[6] == "dashboard_sql_workspaces")
                    {
                        Interlocked.Increment(ref sqlWorkspaceLists);
                    }

                    if (parts.Length == 8 && parts[7] == "documents" && http.Request.Method == "GET")
                    {
                        lock (gate)
                        {
                            return Page(parts[6], int.TryParse(http.Request.Query["limit"], out var limit) ? limit : 50, http.Request.Query["cursor"].FirstOrDefault(), null);
                        }
                    }

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
                    if (http.Request.Method == "PUT" && parts[6] == McpOAuth.ClientCollection && body is { } candidate &&
                        candidate.TryGetProperty("FirstSignInCompletedAt", out var completed) && completed.ValueKind != JsonValueKind.Null && registrationGate is { } pending)
                    { registrationPaused.TrySetResult(); await pending.Task.WaitAsync(http.RequestAborted); }
                    lock (gate)
                    {
                        if (!collections.TryGetValue(parts[6], out var collection))
                        {
                            return Error(404, "unknown_collection");
                        }

                        var existing = collection.Documents.GetValueOrDefault(id);
                        if (http.Request.Method == "GET")
                        {
                            if (existing is null || existing.Deleted)
                            {
                                return Error(404, "not_found");
                            }

                            http.Response.Headers.ETag = '"' + existing.Version + '"';
                            return Results.Json(new { id, version = existing.Version, state = "value", document = existing.Value });
                        }
                        Require(http.Request.Method is "PUT" or "DELETE", "Only production state point read/write/delete operations are admitted.");
                        if (http.Request.Method == "DELETE" && parts[6] == "dashboard_sql_workspaces" && Mode == "sql-delete-unavailable")
                        {
                            return Error(503, "unavailable");
                        }
                        // Test-only competing committed lifecycle transition between fresh read and delete.
                        if (http.Request.Method == "DELETE" && parts[6] == McpOAuth.ClientCollection && Mode == "complete-mcp-before-delete" && existing is { Deleted: false })
                        {
                            var row = existing.Value.Deserialize<McpRegistration>()!;
                            collection.Documents[id] = existing = new("tbv2-completed-race", JsonSerializer.SerializeToElement(row with
                            { FirstSignInCompletedAt = row.CreatedAt.AddHours(23), LastActivityAt = row.CreatedAt.AddHours(23) }), false);
                        }
                        var absent = http.Request.Headers.IfNoneMatch == "*";
                        var match = http.Request.Headers.IfMatch.ToString();
                        Require(absent != (match.Length > 0), "Every state mutation requires exactly one producer precondition.");
                        if (absent && existing is { Deleted: false } || match.Length > 0 && (existing is null || match != '"' + existing.Version + '"'))
                        { preconditionFailures++; return Error(412, "precondition_failed"); }
                        if (http.Request.Method == "DELETE" && existing is null)
                        {
                            return Error(404, "not_found");
                        }

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

            // Protocol fixture: evaluate every predicate against pre-write state, commit only after every operation succeeds.
            private async Task<IResult> TransactAsync(HttpContext http)
            {
                using var json = await JsonDocument.ParseAsync(http.Request.Body);
                var operations = json.RootElement.GetProperty("ops").EnumerateArray().ToArray();
                if (transactionGate is { } held) { Interlocked.Increment(ref pausedTransactions); await held.Task.WaitAsync(http.RequestAborted); }
                Require(operations.Length is >= 1 and <= 1024, "SDK operation-count contract.");
                lock (gate)
                {
                    var writes = new List<(Collection Collection, string Id, Document Document)>();
                    var results = new List<object>(); var targets = new HashSet<(string, string)>(); var capturedBytes = 0;
                    for (var index = 0; index < operations.Length; index++)
                    {
                        var op = operations[index]; var kind = op.GetProperty("op").GetString(); var name = op.GetProperty("collection").GetString()!;
                        if (!collections.TryGetValue(name, out var collection))
                        {
                            return Error(404, "unknown_collection", index);
                        }

                        var filter = op.TryGetProperty("filter", out var f) ? (JsonElement?)f : null;
                        if (kind == "assert_count")
                        {
                            var count = collection.Documents.Values.Count(d => !d.Deleted && Matches(d.Value, filter));
                            if (op.TryGetProperty("gte", out var gte) && count < gte.GetInt64() || op.TryGetProperty("lte", out var lte) && count > lte.GetInt64())
                            {
                                return Error(412, "precondition_failed", index);
                            }

                            results.Add(new { asserted = true, count }); continue;
                        }
                        var id = op.GetProperty("id").GetString()!;
                        Require(TerraScaleIds.IsValid(id) && targets.Add((name, id)), "Canonical IDs and unique transaction targets.");
                        var existing = collection.Documents.GetValueOrDefault(id); var live = existing is { Deleted: false };
                        if (kind == "assert")
                        {
                            if (op.TryGetProperty("absent", out var absent) && absent.GetBoolean() ? live : !live || !Matches(existing!.Value, filter))
                            {
                                return Error(412, "precondition_failed", index);
                            }

                            results.Add(new { asserted = true }); continue;
                        }
                        Require(kind is "create" or "put" or "delete", "Only the narrow adapter's mutation operations are supported.");
                        if (kind == "create" ? live : !op.TryGetProperty("if_match", out var match) || !live || match.GetString() != existing!.Version)
                        {
                            return Error(412, "precondition_failed", index);
                        }

                        var version = "tbv2-" + WebEncoders.Base64UrlEncode(RandomNumberGenerator.GetBytes(32));
                        var value = kind == "delete" ? existing!.Value : op.GetProperty("document").Clone();
                        var documentBytes = Encoding.UTF8.GetByteCount(value.GetRawText());
                        Require(documentBytes <= 1024 * 1024, "Each staged document satisfies the producer's 1 MiB document limit.");
                        capturedBytes += Encoding.UTF8.GetByteCount(name) + Encoding.UTF8.GetByteCount(id) + documentBytes;
                        if (capturedBytes > 2 * 1024 * 1024)
                        {
                            return Error(503, "capacity", index);
                        }

                        writes.Add((collection, id, new(version, value, kind == "delete")));
                        results.Add(new { id, version });
                    }
                    foreach (var write in writes)
                    {
                        write.Collection.Documents[write.Id] = write.Document;
                    }

                    documentWrites += writes.Count;
                    return Results.Json(new { offset = ++offset, durability = "local_durable", results });
                }
            }

            private static bool Matches(JsonElement document, JsonElement? filter)
            {
                if (filter is null)
                {
                    return true;
                }
                // Deliberately support only the fixture's eq grammar; never silently accept an unmodeled predicate.
                var eq = filter.Value.GetProperty("eq"); var path = eq[0].GetString()!;
                Require(path.StartsWith('/') && !path[1..].Contains('/'), "Fixture uses a top-level JSON Pointer.");
                return document.TryGetProperty(path[1..], out var value) && JsonElement.DeepEquals(value, eq[1]);
            }

            private IResult Page(string name, int limit, string? cursor, JsonElement? filter)
            {
                if (!collections.TryGetValue(name, out var collection))
                {
                    return Error(404, "unknown_collection");
                }

                string? last = null;
                if (cursor is not null)
                {
                    if (!cursors.TryGetValue(cursor, out var position) || position.Collection != name || position.Filter != filter?.GetRawText())
                    {
                        return Error(400, "invalid_cursor");
                    }

                    last = position.Last;
                }
                var remaining = collection.Documents.Where(d => !d.Value.Deleted && Matches(d.Value.Value, filter) && string.CompareOrdinal(d.Key, last) > 0).OrderBy(d => d.Key, StringComparer.Ordinal).Take(limit + 1).ToArray();
                string? next = null;
                if (remaining.Length > limit) { next = WebEncoders.Base64UrlEncode(RandomNumberGenerator.GetBytes(32)); cursors[next] = (name, filter?.GetRawText(), remaining[limit - 1].Key); }
                return Results.Json(new { items = remaining.Take(limit).Select(d => new { id = d.Key, version = d.Value.Version, state = "value", document = d.Value.Value }), next_cursor = next });
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
                    if (Mode == "interrupted" && name == "auth-flows")
                    {
                        return Error(503, "unavailable");
                    }

                    if (Mode == "other-conflict")
                    {
                        return Error(409, "schema_requires_empty_collection");
                    }

                    if (collections.ContainsKey(name)) { collectionConflicts++; return Error(409, "name_taken"); }
                    var collection = new Collection(ids.NewId(), DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
                    collections[name] = collection;
                    if (Mode == "bad-conflict-reread") { collectionConflicts++; return Error(409, "name_taken"); }
                    return Results.Json(CollectionBody(name, collection), statusCode: 201);
                }
            }

            private object CollectionBody(string name, Collection collection)
            {
                return new
                { id = collection.Id, name = Mode is "wrong-collection" or "bad-conflict-reread" ? "foreign_collection" : name, created_at = collection.CreatedAt.ToString(CultureInfo.InvariantCulture) };
            }

            private async Task<IResult> ControlAsync(HttpContext http)
            {
                using var document = await JsonDocument.ParseAsync(http.Request.Body, cancellationToken: http.RequestAborted);
                var body = document.RootElement;
                lock (gate)
                {
                    switch (body.GetProperty("command").GetString())
                    {
                        case "mode": mode = body.GetProperty("value").GetString()!; break;
                        case "pause-transactions": pausedTransactions = 0; transactionGate = new(TaskCreationOptions.RunContinuationsAsynchronously); break;
                        case "release-transactions": transactionGate?.TrySetResult(); transactionGate = null; break;
                        case "pause-collections": collectionGate = new(TaskCreationOptions.RunContinuationsAsynchronously); break;
                        case "release-collections": collectionGate?.TrySetResult(); collectionGate = null; break;
                        case "pause-account-writes": pausedWrites = 0; documentGate = new(TaskCreationOptions.RunContinuationsAsynchronously); break;
                        case "release-account-writes": documentGate?.TrySetResult(); documentGate = null; break;
                        case "drop-collection": collections.Remove(body.GetProperty("name").GetString()!); break;
                        case "wipe-plans": organizationPlans.Clear(); break;
                        case "pause-plans": planGate = new(TaskCreationOptions.RunContinuationsAsynchronously); break;
                        case "release-plans": planGate?.TrySetResult(); planGate = null; break;
                        default: return Results.BadRequest();
                    }
                }
                return Results.NoContent();
            }

            private IResult Observe()
            {
                lock (gate)
                {
                    return Results.Json(new
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
                    collectionLists,
                    privateRequests,
                    pausedTransactions,
                    redirectTargetRequests,
                    cleanupQueries = cleanupQueries.ToArray(),
                    sqlWorkspaceLists,
                    failures = failures.ToArray(),
                    organizationPlans = organizationPlans.ToDictionary(),
                    planPublishes = Volatile.Read(ref planPublishes),
                    collections = collections.OrderBy(item => item.Key, StringComparer.Ordinal).Select(item => new
                    { name = item.Key, id = item.Value.Id, createdAt = item.Value.CreatedAt, documents = item.Value.Documents.Count(entry => !entry.Value.Deleted) }).ToArray(),
                    documentDigest = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(string.Join('\n', collections.OrderBy(item => item.Key, StringComparer.Ordinal)
                        .SelectMany(collection => collection.Value.Documents.OrderBy(document => document.Key, StringComparer.Ordinal)
                            .Select(document => collection.Key + "/" + document.Key + ":" + document.Value.Version + ":" + document.Value.Deleted + ":" + document.Value.Value.GetRawText()))))))
                });
                }
            }

            private static IResult Error(int status, string code, int? index = null)
            {
                return Results.Json(new { error = new { code, message = "Test-only state producer refusal.", op = index } }, statusCode: status);
            }

            private static void Require(bool condition, string message) { if (!condition)
                {
                    throw new InvalidOperationException(message);
                }
            }
            private sealed record Document(string Version, JsonElement Value, bool Deleted);
            private sealed class Collection(string id, long createdAt)
            {
                public string Id { get; } = id;
                public long CreatedAt { get; } = createdAt;
                public Dictionary<string, Document> Documents { get; } = new(StringComparer.Ordinal);
            }
        }
    }
}
