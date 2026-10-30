using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using TerraScale.Dashboard.Features.Auth;
using TerraScale.Dashboard.Features.Billing;
using TerraScale.Dashboard.Features.Profile;
using TerraScale.Dashboard.Platform.Persistence;
using TerraScale.Dashboard.Platform.TsAuth;
using TerraScale.TerraBase.Contracts;
using TerraScale.TerraBase.Contracts.Documents;

namespace TerraScale.Dashboard.E2E
{
    internal sealed class DirectAuthDeletionScenario : IAsyncDisposable
    {
        public const string Password = "test-outbox-password";
        public DemoEnvironment Environment { get; } = new();
        public OutboxProducer Producer { get; } = new();
        public Func<HttpRequestMessage, Task>? BeforeForwardAsync { get; set; }
        public Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage?>>? OverrideForwardAsync { get; set; }
        public Func<HttpRequestMessage, HttpResponseMessage, Task>? AfterForwardAsync { get; set; }
        public string User { get; } = new TerraScaleIdGenerator().NewId();
        public string Other { get; } = new TerraScaleIdGenerator().NewId();
        public WebApplication App { get; private set; } = null!;
        public TestAuth Auth => (TestAuth)App.Services.GetRequiredService<IAuthGateway>();
        public IDashboardState State => App.Services.GetRequiredService<IDashboardState>();
        private readonly HashSet<string> registered = [];
        private StateSdkScenario? stateServer;
        private Func<string, string, Task>? beforeStateWrite;
        public Func<OrganizationRecord, Task>? BeforeProjectMutationAsync { get; set; }
        public TaskCompletionSource CreationPaused { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource ResumeCreation { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public static string Email(string user)
        {
            return user.ToLowerInvariant() + "@example.test";
        }

        public string Id()
        {
            return new TerraScaleIdGenerator().NewId();
        }

        // Compose the real TsAuth gateway in the host fixture; journeys exercise it through HTTP.
        public static Task<DirectAuthDeletionScenario> StartReceiptStatusAsync()
        {
            return StartAsync(configure: builder =>
            builder.Services.Replace(ServiceDescriptor.Singleton<ITsAuthPlatform>(services => services.GetRequiredService<TsAuthGateway>())));
        }

        public static async Task<DirectAuthDeletionScenario> StartAsync(bool productionState = false, Action<WebApplicationBuilder>? configure = null)
        {
            var scenario = new DirectAuthDeletionScenario();
            await scenario.Environment.InitializeAsync();
            if (productionState)
            {
                scenario.stateServer = await StateSdkScenario.PrepareAsync(new DashboardE2EFixture().Root);
            }

            scenario.App = scenario.Environment.CreateTestHost(new DashboardE2EFixture().Root, 0, "http://127.0.0.1", builder =>
            {
                if (scenario.stateServer is { } server)
                {
                    server.ConfigureState(builder, factory => new Factory(scenario, factory));
                    builder.Services.AddSingleton<TerraBaseDashboardState>();
                    builder.Services.Replace(ServiceDescriptor.Singleton<IDashboardState>(services => new PausedState(services.GetRequiredService<TerraBaseDashboardState>(), scenario)));
                }
                else
                {
                    var fallback = (IHttpClientFactory)builder.Services.Last(service => service.ServiceType == typeof(IHttpClientFactory)).ImplementationInstance!;
                    builder.Services.Replace(ServiceDescriptor.Singleton<IHttpClientFactory>(new Factory(scenario, fallback)));
                }
                configure?.Invoke(builder);
            });
            scenario.Producer.Credentials = await scenario.App.Services.GetRequiredService<IDashboardIdentity>().ReadAsync(default);
            scenario.App.MapPost("/__test/deletions/reconcile", async () =>
            {
                using var consumer = ActivatorUtilities.CreateInstance<DeletionReconciler>(scenario.App.Services);
                await consumer.RunOnceAsync(default);
                return Microsoft.AspNetCore.Http.Results.NoContent();
            });
            scenario.App.MapPost("/__test/deletions/ensure", async (Microsoft.AspNetCore.Http.HttpContext http, DashboardSessions sessions, AccountService accounts) =>
            {
                var loaded = await sessions.ReadAsync(http);
                if (loaded is null)
                {
                    return Microsoft.AspNetCore.Http.Results.Unauthorized();
                }

                try { await accounts.EnsureAsync(loaded, http.RequestAborted); return Microsoft.AspNetCore.Http.Results.Ok(); }
                catch (AccountAccessException error) { return Microsoft.AspNetCore.Http.Results.StatusCode(error.StatusCode); }
            });
            scenario.App.MapPost("/__test/deletions/context", async (Microsoft.AspNetCore.Http.HttpContext http, DashboardSessions sessions, AccountService accounts) =>
            {
                var loaded = await sessions.ReadAsync(http);
                if (loaded is null)
                {
                    return Microsoft.AspNetCore.Http.Results.Unauthorized();
                }

                try
                {
                    var context = await accounts.GetContextAsync(loaded, http.RequestAborted);
                    if (http.Request.Query["mutate"] == "true")
                    {
                        await accounts.UpdateOrganizationAsync(context, org => scenario.BeforeProjectMutationAsync!(org), http.RequestAborted);
                    }

                    return Microsoft.AspNetCore.Http.Results.Json(context.Organization!.Projects.Keys.ToArray());
                }
                catch (AccountAccessException error) { return Microsoft.AspNetCore.Http.Results.StatusCode(error.StatusCode); }
            });
            await scenario.App.StartAsync();
            return scenario;
        }

        public async Task<HttpClient> SignInAsync(string user)
        {
            if (registered.Add(user))
            {
                Auth.RegisterFixturePrincipal(new(user, new Bogus.Faker().Name.FirstName(), Email(user), Password));
            }

            var client = Client();
            using var login = await HttpJourney.LoginAsync(client, Email(user), Password);
            await Assert.That(login.StatusCode).IsEqualTo(HttpStatusCode.SeeOther);
            return client;
        }

        public HttpClient Client(string? deletionCookie = null)
        {
            var origin = new Uri(App.Urls.Single().Replace("0.0.0.0", "127.0.0.1", StringComparison.Ordinal));
            var cookies = new CookieContainer();
            if (deletionCookie is not null)
            {
                cookies.Add(origin, new Cookie("ts-dashboard-deletion", deletionCookie, "/"));
            }

            return new(new HttpClientHandler { ServerCertificateCustomValidationCallback = TestHttps.Validate, AllowAutoRedirect = false, CookieContainer = cookies }) { BaseAddress = origin };
        }

        public Task<TerraBaseDocument?> GetAsync(string collection, string id)
        {
            return State.GetAsync(collection, id, default);
        }

        public async Task<AccountRecord> AccountAsync(string user)
        {
            return AccountService.Read<AccountRecord>((await GetAsync(AccountService.AccountsCollection, AccountService.AccountStorageKey(user)))!);
        }

        public async Task<TerraBaseDocument> StoreDeletionOperationAsync(string subject, string? cookie = null, string? receipt = null)
        {
            var key = cookie is null ? AccountService.AccountStorageKey(Id()) : Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(cookie)));
            var session = new SessionRecord(subject, "Synthetic owner", Email(subject), "synthetic-operation-grant", null, DateTimeOffset.UtcNow.AddHours(1));
            var plaintext = JsonSerializer.SerializeToUtf8Bytes(new { Session = session, Organizations = Array.Empty<string>(), Receipt = receipt ?? "synthetic-receipt", Authorization = "synthetic-deletion-authorization", ExpiresAt = DateTimeOffset.UtcNow.AddDays(15) });
            var nonce = RandomNumberGenerator.GetBytes(12);
            var ciphertext = new byte[plaintext.Length];
            var tag = new byte[16];
            try
            {
                var identity = await App.Services.GetRequiredService<IDashboardIdentity>().ReadAsync(default);
                using var aes = new AesGcm(Convert.FromBase64String(identity.SessionEncryptionKey), 16);
                aes.Encrypt(nonce, plaintext, ciphertext, tag, Encoding.UTF8.GetBytes("account_deletions" + key));
                await State.PutAsync("account_deletions", key, JsonSerializer.SerializeToElement(new
                {
                    Key = key,
                    UserId = subject,
                    Payload = new SealedSession(Convert.ToBase64String(nonce), Convert.ToBase64String(ciphertext), Convert.ToBase64String(tag))
                }), TerraBasePrecondition.Absent, default);
            }
            finally { CryptographicOperations.ZeroMemory(plaintext); }
            return (await GetAsync("account_deletions", key))!;
        }

        public async Task RunAsync()
        {
            using var client = Client();
            using var response = await client.PostAsync("/__test/deletions/reconcile", null);
            response.EnsureSuccessStatusCode();
        }
        public void PauseCreation(string collection, string key)
        {
            var paused = 0;
            async Task BeforeAsync(string target, string id)
            {
                if (target == collection && id == key && Interlocked.CompareExchange(ref paused, 1, 0) == 0)
                {
                    CreationPaused.TrySetResult();
                    await ResumeCreation.Task;
                }
            }
            if (stateServer is null)
            {
                Environment.State.BeforeWriteAsync = BeforeAsync;
            }
            else
            {
                beforeStateWrite = BeforeAsync;
            }
        }
        public void PauseCheckoutPersistence(string organizationId)
        {
            var paused = 0;
            Environment.State.BeforeWriteAsync = async (collection, key) =>
            {
                if (collection != StripeBilling.Collection || key != organizationId)
                {
                    return;
                }

                var document = await GetAsync(collection, key);
                if (document is null)
                {
                    return;
                }

                var binding = StripeBilling.ReadBinding(document, key);
                if (binding.CustomerId.Length == 0 || binding.CheckoutId.Length > 0 || Interlocked.CompareExchange(ref paused, 1, 0) != 0)
                {
                    return;
                }

                CreationPaused.TrySetResult();
                await ResumeCreation.Task;
            };
        }
        public async ValueTask DisposeAsync()
        {
            await App.StopAsync(); await App.DisposeAsync();
            if (stateServer is not null)
            {
                await stateServer.DisposeAsync();
            }
        }

        private sealed class PausedState(IDashboardState inner, DirectAuthDeletionScenario scenario) : IDashboardState
        {
            public Task InitializeAsync(CancellationToken token)
            {
                return inner.InitializeAsync(token);
            }

            public Task CheckReadyAsync(CancellationToken token)
            {
                return inner.CheckReadyAsync(token);
            }

            public Task<TerraBaseDocument?> GetAsync(string collection, string id, CancellationToken token)
            {
                return inner.GetAsync(collection, id, token);
            }

            Task<TerraBaseDocumentPage> IDashboardState.ReadPageAsync(string collection, JsonElement? filter, int limit, string? cursor, CancellationToken ct)
            {
                return inner.ReadPageAsync(collection, filter, limit, cursor, ct);
            }

            Task<TerraBaseTransactionResult> IDashboardState.TransactAsync(IReadOnlyList<DashboardStateOperation> operations, CancellationToken ct)
            {
                return inner.TransactAsync(operations, ct);
            }

            public Task<IReadOnlyList<TerraBaseDocument>> ListAsync(string collection, CancellationToken token)
            {
                return inner.ListAsync(collection, token);
            }

            public Task<TerraBaseDocumentPage> QueryPageAsync(string collection, TerraBaseQuery query, CancellationToken token)
            {
                return inner.QueryPageAsync(collection, query, token);
            }

            public Task DeleteAsync(string collection, string id, string version, CancellationToken token)
            {
                return inner.DeleteAsync(collection, id, version, token);
            }

            public Task DeleteListedAsync(string collection, TerraBaseDocument document, CancellationToken token)
            {
                return inner.DeleteListedAsync(collection, document, token);
            }

            public async Task PutAsync(string collection, string id, JsonElement document, TerraBasePrecondition precondition, CancellationToken token)
            {
                if (scenario.beforeStateWrite is { } before)
                {
                    await before(collection, id);
                }

                await inner.PutAsync(collection, id, document, precondition, token);
            }
        }
        private sealed class Factory(DirectAuthDeletionScenario scenario, IHttpClientFactory fallback) : IHttpClientFactory
        {
            public HttpClient CreateClient(string name)
            {
                return name == "platform" ? new(new Router(scenario, fallback.CreateClient(name))) : fallback.CreateClient(name);
            }
        }

        private sealed class Router(DirectAuthDeletionScenario scenario, HttpClient fallback) : HttpMessageHandler
        {
            protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            {
                if (request.RequestUri is { Host: "auth.terrascale.tech", AbsolutePath: var path } && path.StartsWith("/v1/terrascale/deletions", StringComparison.Ordinal))
                {
                    return await scenario.Producer.SendAsync(request, cancellationToken);
                }

                if (scenario.BeforeForwardAsync is { } before)
                {
                    await before(request);
                }

                if (scenario.OverrideForwardAsync is { } replace && await replace(request, cancellationToken) is { } replacement)
                {
                    return replacement;
                }

                var response = await HttpForwarding.SendAsync(fallback, request, cancellationToken);
                if (scenario.AfterForwardAsync is { } after)
                {
                    await after(request, response);
                }

                return response;
            }
            protected override void Dispose(bool disposing) { if (disposing) { fallback.Dispose(); } base.Dispose(disposing); }
        }

        public sealed class OutboxProducer
        {
            public DashboardCredentials Credentials { private get; set; } = null!;
            public List<TsAuthDeletionProof> Proofs { get; } = [];
            public HttpStatusCode Status { get; set; } = HttpStatusCode.OK;
            public int AckFailures { get; set; }
            public int Acknowledgements { get; private set; }
            public int AuthenticatedCalls { get; private set; }

            public async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            {
                var expected = Convert.ToBase64String(Encoding.UTF8.GetBytes(Credentials.AuthClientId + ":" + Credentials.AuthClientSecret));
                if (request.RequestUri!.Scheme != "https" || request.Headers.Authorization is not { Scheme: "Basic", Parameter: var value } || value != expected)
                {
                    return new(HttpStatusCode.Unauthorized);
                }

                AuthenticatedCalls++;
                if (request.Method == HttpMethod.Get)
                {
                    return Status == HttpStatusCode.OK ? Json(new { items = Proofs.Select(proof => new { id = proof.Id, scope = proof.Scope, subjectId = proof.SubjectId, organizationId = proof.OrganizationId }).ToArray() }) : new(Status);
                }

                using var body = JsonDocument.Parse(await request.Content!.ReadAsStringAsync(cancellationToken));
                var root = body.RootElement;
                var proof = Proofs.Single(item => request.RequestUri.AbsolutePath == "/v1/terrascale/deletions/" + item.Id + "/acknowledge");
                if (request.Method != HttpMethod.Post || root.GetProperty("scope").GetString() != proof.Scope ||
                    root.GetProperty("subjectId").GetString() != proof.SubjectId || root.GetProperty("organizationId").GetString() != proof.OrganizationId)
                {
                    return new(HttpStatusCode.Conflict);
                }

                if (AckFailures > 0) { AckFailures--; return new(HttpStatusCode.ServiceUnavailable); }
                Acknowledgements++;
                Proofs.Remove(proof);
                return new(HttpStatusCode.NoContent);
            }

            private static HttpResponseMessage Json(object body)
            {
                return new(HttpStatusCode.OK)
                { Content = new StringContent(JsonSerializer.Serialize(body), Encoding.UTF8, new MediaTypeHeaderValue("application/json")) };
            }
        }
    }
}
