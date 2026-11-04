using System.Collections.Concurrent;
using System.Net;
using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Server.Kestrel.Core;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;
using TerraScale.Dashboard.Features.Billing;
using TerraScale.Dashboard.Platform.Persistence;
using TerraScale.Dashboard.Platform.Mcp;
using TerraScale.Dashboard.Platform.Rpc;
using TerraScale.TerraBase.Rpc;
using System.Diagnostics.Metrics;
using TerraScale.Dashboard.Platform.TsAuth;
using TerraScale.TerraBase.Contracts;

namespace TerraScale.Dashboard.E2E
{
    // A test-only REST producer behind the real MagicOnion services for the production state adapter. The producer-seeded platform database is
    // fixture-owned; this does not qualify OCI Vault secret delivery or a live deployment.
    public sealed class StateSdkScenario : IAsyncDisposable
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

        private StateSdkScenario(string root)
        {
            this.root = root;
        }

        public static async Task<StateSdkScenario> PrepareAsync(string root)
        {
            var scenario = new StateSdkScenario(root);
            try
            {
                var builder = WebApplication.CreateBuilder();
                builder.WebHost.ConfigureKestrel(options => options.Listen(IPAddress.Loopback, 0,
                    listener => listener.Protocols = HttpProtocols.Http2));
                builder.Logging.ClearProviders();
                builder.Services.AddTerraBaseRestRpc(static (_, _) => { });
                scenario.producer = builder.Build();
                scenario.producer.UseTerraBaseRestPipeline();
                scenario.producer.UseRouting();
                scenario.producer.MapTerraBaseRestRpc();
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
            var client = new HttpClient(new H2cTestTransport()) { BaseAddress = ProviderOrigin, Timeout = TimeSpan.FromSeconds(30) };
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

        // Starts both replicas and waits until each has initialized its state, whether in its bounded startup attempt
        // or on a background retry: a loaded host can need longer than the startup attempt allows.
        public async Task<bool> StartHostsAsync(bool simultaneous = false)
        {
            return await StartHostsWithoutWaitingAsync(simultaneous) || (StartupError == "not-initialized" && await WaitInitializedAsync());
        }

        // Starts both replicas and reports whether their bounded startup attempt alone initialized the state.
        public async Task<bool> StartHostsWithoutWaitingAsync(bool simultaneous = false)
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
        public async Task<bool> WaitInitializedAsync()
        {
            try { await Task.WhenAll(new[] { first!, second! }.Select(TestDeadline.StateInitializedAsync)); }
            catch (TimeoutException) { return false; }
            StartupError = null;
            return true;
        }

        public async Task<bool> RestartAsync()
        {
            await StopHostsAsync();
            return await StartHostsAsync();
        }

        private WebApplication Host()
        {
            var app = environment.CreateTestHost(root, 0, "http://127.0.0.1", builder => ConfigureState(builder));
            return app;
        }

        internal void ConfigureState(WebApplicationBuilder builder, Func<IHttpClientFactory, IHttpClientFactory>? decorate = null)
        {
            var previousIdentity = (IDashboardIdentity)builder.Services.Last(service => service.ServiceType == typeof(IDashboardIdentity)).ImplementationInstance!;
            identity ??= new(previousIdentity, protocol);
            var fallback = (IHttpClientFactory)builder.Services.Last(service => service.ServiceType == typeof(IHttpClientFactory)).ImplementationInstance!;
            var transport = new StateTransport(fallback, this);
            transports.Add(transport);
            builder.Services.Replace(ServiceDescriptor.Singleton<IDashboardIdentity>(identity));
            builder.Services.Replace(ServiceDescriptor.Singleton<TerraBaseRpcClient>(services => new(
                services.GetRequiredService<IDashboardIdentity>(), ProviderOrigin, "gru", null, services.GetRequiredService<IMeterFactory>())));
            builder.Services.Replace(ServiceDescriptor.Singleton<IHttpClientFactory>(services =>
            {
                var factory = new StateFactory(transport);
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
                StatePrecondition.Matches(stored.Version), default);
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

        private sealed class StateFactory(StateTransport transport) : IHttpClientFactory
        {
            public HttpClient CreateClient(string name)
            {
                return new(transport, disposeHandler: false);
            }
        }

        private sealed class StateTransport(IHttpClientFactory fallback, StateSdkScenario owner) : HttpMessageHandler
        {
            protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            {
                var uri = request.RequestUri!;
                // State calls use RPC directly; ts-auth HTTP calls must never carry the peer credential.
                if (request.Headers.Contains(TerraScale.TerraBase.Contracts.TerraBaseRpc.PeerCredentialHeader))
                {
                    Interlocked.Increment(ref owner.foreignCredentials);
                    throw new HttpRequestException("The peer credential left for a destination other than the state listener.");
                }

                using var forwarded = new HttpRequestMessage(request.Method, uri)
                { Version = request.Version, VersionPolicy = request.VersionPolicy };
                foreach (var header in request.Headers)
                {
                    forwarded.Headers.TryAddWithoutValidation(header.Key, header.Value);
                }

                foreach (var option in request.Options)
                {
                    forwarded.Options.Set(new HttpRequestOptionsKey<object?>(option.Key), option.Value);
                }

                if (request.Content is { } content)
                {
                    forwarded.Content = new ByteArrayContent(await content.ReadAsByteArrayAsync(cancellationToken));
                    foreach (var header in content.Headers)
                    {
                        forwarded.Content.Headers.TryAddWithoutValidation(header.Key, header.Value);
                    }
                }
                using var other = fallback.CreateClient("platform");
                var response = await other.SendAsync(forwarded, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
                response.RequestMessage = request;
                return response;
            }
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

        private sealed class StateProducer
        {
            public const string Project = "00000000000000000000000005";
            private readonly object gate = new();
            private readonly TestSqlEngine engine = new();
            private readonly List<string> statementLog = [];
            private readonly ConcurrentQueue<string> failures = new();
            private readonly Dictionary<string, string> organizationPlans = new(StringComparer.Ordinal);
            private int planPublishes;
            private TaskCompletionSource? collectionGate;
            private TaskCompletionSource? planGate;
            private TaskCompletionSource? documentGate;
            private TaskCompletionSource? registrationGate;
            private TaskCompletionSource registrationPaused = new(TaskCreationOptions.RunContinuationsAsynchronously);
            public Task PauseRegistrationCompletion()
            { registrationGate = new(TaskCreationOptions.RunContinuationsAsynchronously); return registrationPaused.Task; }
            public void ReleaseRegistrationCompletion() { registrationGate?.TrySetResult(); registrationGate = null; }
            private int pausedCreates;
            private int pausedWrites;
            private int schemaReads;
            private int schemaWrites;
            private int rowWrites;
            private int conflicts;
            private int privateRequests;
            private int redirectTargetRequests;
            private string mode = "";
            public string Mode { get { lock (gate) { return mode; } } }
            public void Release() { ReleaseRegistrationCompletion(); planGate?.TrySetResult(); collectionGate?.TrySetResult(); documentGate?.TrySetResult(); engine.Dispose(); }

            public async Task<IResult> DispatchAsync(HttpContext http)
            {
                try
                {
                    var path = http.Request.Path.Value!;
                    if (path == "/__test/observations")
                    {
                        return await ObserveAsync();
                    }

                    if (path == "/__test/control")
                    {
                        return await ControlAsync(http);
                    }

                    if (path == "/__test/redirected") { Interlocked.Increment(ref redirectTargetRequests); return Results.NoContent(); }
                    // TerraBase unreachable: the connection drops before any answer, as a restarting or partitioned listener does.
                    if (Mode == "unreachable") { http.Abort(); return Results.Empty; }
                    Require(http.Request.Host.Host == "127.0.0.1", "The state RPC uses the fixture-owned private listener.");
                    Require(http.Request.Headers.TryGetValue(TerraScale.TerraBase.Contracts.TerraBaseRpc.PeerCredentialHeader, out var peer) && peer.Count == 1 && peer[0] == TestIdentity.PeerSecret,
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
                    Require(parts is ["v1", "projects", Project, "databases", "ts-dashboard", "sql", "execute"] && http.Request.Method == "POST",
                        "State uses only SQL on its delivered canonical project and fixed seeded database.");
                    if (Mode == "project-missing")
                    {
                        return Error(404, "unknown_database");
                    }

                    return await ExecuteAsync(http);
                }
                catch (Exception error) when (error is InvalidOperationException or JsonException)
                { failures.Enqueue(error.Message); return Error(500, "test_contract_failure"); }
            }

            private async Task<IResult> ExecuteAsync(HttpContext http)
            {
                using var json = await JsonDocument.ParseAsync(http.Request.Body, cancellationToken: http.RequestAborted);
                var statements = json.RootElement.GetProperty("statements").EnumerateArray().Select(statement => new TestSqlStatement(statement.GetProperty("sql").GetString()!,
                    statement.TryGetProperty("args", out var args) ? [.. args.EnumerateArray().Select(arg => arg.ValueKind == JsonValueKind.Null ? null : arg.GetString())] : [])).ToArray();
                Require(statements.Length is >= 1 and <= 256, "One SQL request holds 1 to 256 statements.");
                var schema = statements.Any(statement => statement.Sql.StartsWith("CREATE ", StringComparison.Ordinal));
                var writes = statements.Where(statement => statement.Sql.Split(' ')[0] is "INSERT" or "UPDATE" or "DELETE").ToArray();
                lock (gate)
                {
                    statementLog.AddRange(statements.Select(statement => statement.Sql));
                    if (statements.Any(statement => statement.Sql.StartsWith("SELECT name FROM sqlite_master", StringComparison.Ordinal)))
                    {
                        schemaReads++;
                    }
                }

                if (Mode == "forbidden" && (schema || writes.Length > 0))
                {
                    return SqlError(403, "forbidden_scope", "42501", 0);
                }

                if (schema)
                {
                    Interlocked.Increment(ref schemaWrites);
                    if (collectionGate is { } held) { Interlocked.Increment(ref pausedCreates); await held.Task.WaitAsync(http.RequestAborted); }
                    if (Mode == "interrupted")
                    {
                        return Error(503, "unavailable");
                    }
                }

                if (writes.Any(statement => Writes(statement, "dashboard_accounts")) && documentGate is { } accounts)
                { Interlocked.Increment(ref pausedWrites); await accounts.Task.WaitAsync(http.RequestAborted); }
                if (writes.Any(statement => Writes(statement, McpOAuth.ClientCollection) && statement.Args.Any(arg => arg?.Contains("\"FirstSignInCompletedAt\":\"", StringComparison.Ordinal) == true))
                    && registrationGate is { } pending)
                { registrationPaused.TrySetResult(); await pending.Task.WaitAsync(http.RequestAborted); }
                if (Mode == "sql-delete-unavailable" && writes.Any(statement => statement.Sql.StartsWith("DELETE FROM dashboard_sql_workspaces", StringComparison.Ordinal)))
                {
                    return Error(503, "unavailable");
                }

                if (Mode == "complete-mcp-before-delete" && writes.FirstOrDefault(statement => statement.Sql.StartsWith("DELETE FROM " + McpOAuth.ClientCollection, StringComparison.Ordinal)) is { } removal)
                {
                    var row = (await engine.ExecuteAsync([new("SELECT body FROM " + McpOAuth.ClientCollection + " WHERE id = ?", [removal.Args[0]])], http.RequestAborted))[0].Rows;
                    if (row.Count == 1 && JsonSerializer.Deserialize<McpRegistration>((string)row[0][0]!) is { } registration)
                    {
                        await engine.ExecuteAsync([new("UPDATE " + McpOAuth.ClientCollection + " SET version = ?, body = ? WHERE id = ?", ["completed-race",
                            JsonSerializer.Serialize(registration with { FirstSignInCompletedAt = registration.CreatedAt.AddHours(23), LastActivityAt = registration.CreatedAt.AddHours(23) }),
                            removal.Args[0]])], http.RequestAborted);
                    }
                }

                try
                {
                    var results = await engine.ExecuteAsync(statements, http.RequestAborted);
                    lock (gate)
                    {
                        for (var index = 0; index < statements.Length; index++)
                        {
                            if (writes.Contains(statements[index]))
                            {
                                if (results[index].RowsAffected == 0) { conflicts++; } else { rowWrites += (int)results[index].RowsAffected; }
                            }
                        }
                    }

                    return Results.Json(TestSqlEngine.Body(results));
                }
                catch (TestSqlFailure failure)
                {
                    if (failure.Sqlstate.StartsWith("23", StringComparison.Ordinal)) { Interlocked.Increment(ref conflicts); }
                    else if (!failure.Message.Contains("no such table", StringComparison.Ordinal)) { failures.Enqueue(failure.Message); }
                    return SqlError(400, failure.Code, failure.Sqlstate, failure.Statement);
                }
            }

            private static bool Writes(TestSqlStatement statement, string table)
            {
                return statement.Sql.StartsWith("INSERT INTO " + table + " ", StringComparison.Ordinal) || statement.Sql.StartsWith("UPDATE " + table + " ", StringComparison.Ordinal);
            }

            private static IResult SqlError(int status, string code, string sqlstate, int statement)
            {
                return Results.Json(new { error = new { code, message = "Test-only state producer refusal.", statement, sqlstate } }, statusCode: status);
            }

            private async Task<IResult> ControlAsync(HttpContext http)
            {
                using var document = await JsonDocument.ParseAsync(http.Request.Body, cancellationToken: http.RequestAborted);
                var body = document.RootElement;
                var command = body.GetProperty("command").GetString();
                if (command == "drop-table")
                {
                    await engine.ExecuteAsync([new("DROP TABLE " + body.GetProperty("name").GetString(), [])], http.RequestAborted);
                    return Results.NoContent();
                }

                lock (gate)
                {
                    switch (command)
                    {
                        case "mode": mode = body.GetProperty("value").GetString()!; break;
                        case "pause-schema": collectionGate = new(TaskCreationOptions.RunContinuationsAsynchronously); break;
                        case "release-schema": collectionGate?.TrySetResult(); collectionGate = null; break;
                        case "pause-account-writes": pausedWrites = 0; documentGate = new(TaskCreationOptions.RunContinuationsAsynchronously); break;
                        case "release-account-writes": documentGate?.TrySetResult(); documentGate = null; break;
                        case "wipe-plans": organizationPlans.Clear(); break;
                        case "pause-plans": planGate = new(TaskCreationOptions.RunContinuationsAsynchronously); break;
                        case "release-plans": planGate?.TrySetResult(); planGate = null; break;
                        default: return Results.BadRequest();
                    }
                }
                return Results.NoContent();
            }

            private async Task<IResult> ObserveAsync()
            {
                var tables = await engine.TablesAsync();
                var digest = await engine.DigestAsync();
                lock (gate)
                {
                    return Results.Json(new
                    {
                        schemaReads,
                        schemaWrites,
                        rowWrites,
                        conflicts,
                        pausedCreates,
                        pausedWrites,
                        privateRequests,
                        redirectTargetRequests,
                        statements = statementLog.ToArray(),
                        failures = failures.ToArray(),
                        organizationPlans = organizationPlans.ToDictionary(),
                        planPublishes = Volatile.Read(ref planPublishes),
                        tables = tables.Select(table => new { name = table.Name, rows = table.Rows }).ToArray(),
                        rowDigest = digest
                    });
                }
            }

            private static IResult Error(int status, string code)
            {
                return Results.Json(new { error = new { code, message = "Test-only state producer refusal." } }, statusCode: status);
            }

            private static void Require(bool condition, string message)
            {
                if (!condition)
                {
                    throw new InvalidOperationException(message);
                }
            }
        }
    }
}
