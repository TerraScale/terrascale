using System.Collections.Concurrent;
using System.Diagnostics.Metrics;
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
using TerraScale.Dashboard.Platform.Persistence;
using TerraScale.Dashboard.Platform.Rpc;
using TerraScale.TerraBase.Contracts;
using TerraScale.TerraBase.Rpc;

namespace TerraScale.Dashboard.E2E
{
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
        public Uri ProviderOrigin { get; private set; } = null!;
        public Uri Origin { get; private set; } = null!;
        public Uri ReplicaOrigin { get; private set; } = null!;

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
                builder.WebHost.ConfigureKestrel(options => options.Listen(IPAddress.Loopback, 0, listener => listener.Protocols = HttpProtocols.Http2));
                builder.Logging.ClearProviders();
                builder.Services.AddTerraBaseRestRpc(static (_, _) => { });
                scenario.producer = builder.Build();
                scenario.producer.UseTerraBaseRestPipeline();
                scenario.producer.UseRouting();
                scenario.producer.MapTerraBaseRestRpc();
                scenario.producer.MapMethods("/{**path}", ["GET", "POST", "PUT"], (Func<HttpContext, Task<IResult>>)scenario.protocol.DispatchAsync);
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

        public async Task<bool> StartHostsAsync()
        {
            first = Host(); second = Host();
            await first.StartAsync(); await second.StartAsync();
            Origin = Address(first); ReplicaOrigin = Address(second);
            try { await Task.WhenAll(new[] { first, second }.Select(TestDeadline.StateInitializedAsync)); }
            catch (TimeoutException) { return false; }
            return true;
        }

        public async Task<bool> RestartAsync()
        {
            await StopHostsAsync();
            return await StartHostsAsync();
        }

        internal void ConfigureState(WebApplicationBuilder builder)
        {
            var fallback = (IHttpClientFactory)builder.Services.Last(service => service.ServiceType == typeof(IHttpClientFactory)).ImplementationInstance!;
            var transport = new StateTransport(fallback);
            transports.Add(transport);
            builder.Services.Replace(ServiceDescriptor.Singleton<TerraBaseRpcClient>(services => new(
                services.GetRequiredService<IDashboardIdentity>(), ProviderOrigin, "gru", null, services.GetRequiredService<IMeterFactory>())));
            builder.Services.Replace(ServiceDescriptor.Singleton<IHttpClientFactory>(new StateFactory(transport)));
            builder.Services.Replace(ServiceDescriptor.Singleton<IDashboardState, TerraBaseDashboardState>());
        }

        public async ValueTask DisposeAsync()
        {
            await StopHostsAsync();
            if (producer is not null) { await producer.StopAsync(); await producer.DisposeAsync(); producer = null; }
            protocol.Dispose();
            foreach (var transport in transports)
            {
                transport.Dispose();
            }

            transports.Clear();
        }

        private HttpClient Client(Uri origin, CookieContainer? cookies = null)
        {
            cookies ??= new();
            var client = new HttpClient(new HttpClientHandler { ServerCertificateCustomValidationCallback = TestHttps.Validate, AllowAutoRedirect = false, CookieContainer = cookies })
            { BaseAddress = origin, Timeout = TimeSpan.FromSeconds(30) };
            cookieJars[client] = cookies;
            return client;
        }

        private WebApplication Host()
        {
            return environment.CreateTestHost(root, 0, "http://127.0.0.1", ConfigureState);
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

        private sealed class StateFactory(StateTransport transport) : IHttpClientFactory
        {
            public HttpClient CreateClient(string name)
            {
                return new(transport, disposeHandler: false);
            }
        }

        private sealed class StateTransport(IHttpClientFactory fallback) : HttpMessageHandler
        {
            protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            {
                if (request.Headers.Contains(TerraBaseRpc.PeerCredentialHeader))
                {
                    throw new HttpRequestException("The peer credential left for a destination other than the state listener.");
                }

                using var forwarded = new HttpRequestMessage(request.Method, request.RequestUri) { Version = request.Version, VersionPolicy = request.VersionPolicy };
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

        private sealed class StateProducer : IDisposable
        {
            private const string Project = "00000000000000000000000005";
            private readonly TestSqlEngine engine = new();
            private readonly ConcurrentQueue<string> failures = new();

            public void Dispose()
            {
                engine.Dispose();
            }

            public async Task<IResult> DispatchAsync(HttpContext http)
            {
                var path = http.Request.Path.Value!;
                if (path == "/__test/observations")
                {
                    var tables = await engine.TablesAsync();
                    return Results.Json(new
                    {
                        tables = tables.Select(table => new { name = table.Name, rows = table.Rows }).ToArray(),
                        rowDigest = await engine.DigestAsync(),
                        failures = failures.ToArray()
                    });
                }

                if (path == "/__test/control")
                {
                    using var control = await JsonDocument.ParseAsync(http.Request.Body, cancellationToken: http.RequestAborted);
                    if (control.RootElement.GetProperty("command").GetString() != "drop-table")
                    {
                        return Results.BadRequest();
                    }

                    await engine.ExecuteAsync([new("DROP TABLE " + control.RootElement.GetProperty("name").GetString(), [])], http.RequestAborted);
                    return Results.NoContent();
                }

                if (!http.Request.Headers.TryGetValue(TerraBaseRpc.PeerCredentialHeader, out var peer) || peer.Count != 1 || peer[0] != TestIdentity.PeerSecret
                    || http.Request.Headers.ContainsKey("Authorization"))
                {
                    failures.Enqueue("A private-listener request lacked exactly one peer credential or carried a bearer.");
                    return Error(401, "unauthorized");
                }

                var parts = path.Split('/', StringSplitOptions.RemoveEmptyEntries);
                if (parts is ["admin", "organizations" or "projects", _, "plan"] && http.Request.Method == "PUT")
                {
                    using var plan = await JsonDocument.ParseAsync(http.Request.Body, cancellationToken: http.RequestAborted);
                    return Results.Json(new { plan = plan.RootElement.GetProperty("plan").GetString() });
                }

                if (parts is not ["v1", "projects", Project, "databases", "ts-dashboard", "sql", "execute"] || http.Request.Method != "POST")
                {
                    failures.Enqueue("Unexpected state route " + http.Request.Method + " " + path);
                    return Error(404, "not_found");
                }

                using var json = await JsonDocument.ParseAsync(http.Request.Body, cancellationToken: http.RequestAborted);
                var statements = json.RootElement.GetProperty("statements").EnumerateArray().Select(statement => new TestSqlStatement(statement.GetProperty("sql").GetString()!,
                    statement.TryGetProperty("args", out var args) ? [.. args.EnumerateArray().Select(arg => arg.ValueKind == JsonValueKind.Null ? null : arg.GetString())] : [])).ToArray();
                try { return Results.Json(TestSqlEngine.Body(await engine.ExecuteAsync(statements, http.RequestAborted))); }
                catch (TestSqlFailure failure)
                {
                    if (!failure.SqliteCode.StartsWith("SQLITE_CONSTRAINT", StringComparison.Ordinal) && !failure.Message.Contains("no such table", StringComparison.Ordinal))
                    {
                        failures.Enqueue(failure.Message);
                    }

                    return Results.Json(new { error = new { code = failure.Code, message = "Test-only state producer refusal.", statement = failure.Statement, sqlite_code = failure.SqliteCode } },
                        statusCode: 400);
                }
            }

            private static IResult Error(int status, string code)
            {
                return Results.Json(new { error = new { code, message = "Test-only state producer refusal." } }, statusCode: status);
            }
        }
    }
}
