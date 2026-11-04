using System.Net;
using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using TerraScale.Dashboard.Platform.Persistence;

namespace TerraScale.Dashboard.E2E
{
    // Real Kestrel journeys through the production state adapter and MagicOnion transport;
    // only the external state producer and the deterministic write barrier are test-owned.
    internal sealed class AccountEnrollmentScenario : IAsyncDisposable
    {
        private readonly DemoEnvironment environment = new();
        private StateSdkScenario stateServer = null!;
        private WebApplication app = null!;
        private IDashboardState State => app.Services.GetRequiredService<IDashboardState>();
        private string? pausedTable;
        private int paused;
        public string User { get; } = AccountService.NewId();
        public string Other { get; } = AccountService.NewId();
        public string PausedId { get; private set; } = "";
        public TaskCompletionSource Paused { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Resume { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public const string Password = "test-enrollment-password";
        public static string Email(string user)
        {
            return user.ToLowerInvariant() + "@example.test";
        }

        public static async Task<AccountEnrollmentScenario> StartAsync(string root)
        {
            var scenario = new AccountEnrollmentScenario();
            await scenario.environment.InitializeAsync();
            scenario.stateServer = await StateSdkScenario.PrepareAsync(root);
            scenario.app = await scenario.environment.CreateTestHostAsync(root, 0, "http://127.0.0.1", builder =>
            {
                scenario.stateServer.ConfigureState(builder);
                builder.Services.AddSingleton<TerraBaseDashboardState>();
                builder.Services.Replace(ServiceDescriptor.Singleton<IDashboardState>(services =>
                    new PausedState(services.GetRequiredService<TerraBaseDashboardState>(), scenario)));
            });
            var auth = (TestAuth)scenario.app.Services.GetRequiredService<IAuthGateway>();
            foreach (var user in new[] { scenario.User, scenario.Other })
            {
                auth.RegisterFixturePrincipal(new(user, new Bogus.Faker().Name.FirstName(), Email(user), Password));
            }
            // Emulate the parent's retained proof: fence, first cleanup, ACK, then
            // replay using the same subject even when no producer proof remains.
            scenario.app.MapPost("/__test/enrollment/ack", async (DashboardSessions sessions) =>
            {
                await AccountService.FenceDeletionAsync(scenario.State, scenario.User, AccountService.NewId(), default);
                await sessions.CleanEnrollmentsAsync(scenario.User, default);
                auth.ChangePassword(Email(scenario.User), "erased-enrollment-password");
                return Results.NoContent();
            });
            scenario.app.MapPost("/__test/enrollment/replay", async (DashboardSessions sessions) =>
            {
                await sessions.CleanEnrollmentsAsync(scenario.User, default);
                return Results.NoContent();
            });
            scenario.app.MapPost("/__test/enrollment/substitute", async (HttpContext http) =>
            {
                var table = http.Request.Query["table"].ToString();
                var target = table == AccountService.OrganizationsTable
                    ? (await scenario.OrganizationAsync(scenario.Other)).Id
                    : (await scenario.ReferencesAsync(scenario.Other)).First(reference => reference.Table == table).RecordId;
                await scenario.State.PutAsync(AccountEnrollment.Table, AccountService.NewId(),
                    JsonSerializer.SerializeToElement(new AccountEnrollment.Reference(scenario.User, table, target)), StatePrecondition.Absent, default);
                return Results.NoContent();
            });
            await scenario.app.StartAsync();
            await TestDeadline.StateInitializedAsync(scenario.app);
            return scenario;
        }

        public HttpClient Client()
        {
            return new(new HttpClientHandler { ServerCertificateCustomValidationCallback = TestHttps.Validate, AllowAutoRedirect = false, CookieContainer = new() })
            { BaseAddress = new(app.Urls.Single().Replace("0.0.0.0", "127.0.0.1", StringComparison.Ordinal)) };
        }

        public async Task<HttpClient> SignInAsync(string user)
        {
            var client = Client();
            using var response = await HttpJourney.LoginAsync(client, Email(user), Password);
            if (response.StatusCode != HttpStatusCode.SeeOther)
            {
                throw new IOException("Fixture sign-in failed: " + response.StatusCode);
            }

            return client;
        }

        public void Pause(string table)
        {
            pausedTable = table;
        }

        public Task<StateRecord?> GetAsync(string table, string id)
        {
            return State.GetAsync(table, id, default);
        }

        public Task<IReadOnlyList<AccountEnrollment.Reference>> ReferencesAsync(string user)
        {
            return AccountEnrollment.ListAsync(State, user, default);
        }

        public Task<IReadOnlyList<StateRecord>> ReferenceRowsAsync()
        {
            return State.ListAsync(AccountEnrollment.Table, default);
        }

        public async Task<StateRecord> OrganizationAsync(string user)
        {
            var account = AccountService.Read<AccountRecord>((await GetAsync(AccountService.AccountsTable, AccountService.AccountStorageKey(user)))!);
            return (await GetAsync(AccountService.OrganizationsTable, account.PersonalOrganizationId))!;
        }

        public async ValueTask DisposeAsync()
        {
            Resume.TrySetResult();
            await app.StopAsync();
            await app.DisposeAsync();
            await stateServer.DisposeAsync();
        }

        private sealed class PausedState(IDashboardState inner, AccountEnrollmentScenario scenario) : IDashboardState
        {
            public Task InitializeAsync(CancellationToken ct)
            {
                return inner.InitializeAsync(ct);
            }

            public Task CheckReadyAsync(CancellationToken ct)
            {
                return inner.CheckReadyAsync(ct);
            }

            public Task<StateRecord?> GetAsync(string table, string id, CancellationToken ct)
            {
                return inner.GetAsync(table, id, ct);
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

            public async Task PutAsync(string table, string id, JsonElement row, StatePrecondition precondition, CancellationToken ct)
            {
                if (table == scenario.pausedTable && Interlocked.CompareExchange(ref scenario.paused, 1, 0) == 0)
                {
                    scenario.PausedId = id;
                    scenario.Paused.TrySetResult();
                    await scenario.Resume.Task.WaitAsync(ct);
                }
                await inner.PutAsync(table, id, row, precondition, ct);
            }
        }
    }
}
