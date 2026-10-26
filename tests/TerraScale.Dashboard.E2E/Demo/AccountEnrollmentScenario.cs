using System.Net;
using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using TerraScale.Dashboard.Platform.Identity;
using TerraScale.Dashboard.Platform.Persistence;
using TerraScale.Dashboard.Shared.Accounts;
using TerraScale.DotNet.TerraBase;

namespace TerraScale.Dashboard.E2E;

// Real Kestrel journeys through the production state adapter and official SDK;
// only the external state producer and the deterministic write barrier are test-owned.
internal sealed class AccountEnrollmentScenario : IAsyncDisposable
{
    private readonly DemoEnvironment environment = new();
    private StateSdkScenario stateServer = null!;
    private WebApplication app = null!;
    private IDashboardState State => app.Services.GetRequiredService<IDashboardState>();
    private string? pausedCollection;
    private int paused;
    public string User { get; } = AccountService.NewId();
    public string Other { get; } = AccountService.NewId();
    public string PausedId { get; private set; } = "";
    public TaskCompletionSource Paused { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    public TaskCompletionSource Resume { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    public const string Password = "test-enrollment-password";
    public static string Email(string user) => user.ToLowerInvariant() + "@example.test";

    public static async Task<AccountEnrollmentScenario> StartAsync(string root)
    {
        var scenario = new AccountEnrollmentScenario();
        await scenario.environment.InitializeAsync();
        scenario.stateServer = await StateSdkScenario.PrepareAsync(root);
        scenario.app = scenario.environment.CreateTestHost(root, 0, "http://127.0.0.1", builder =>
        {
            scenario.stateServer.ConfigureState(builder);
            builder.Services.AddSingleton<TerraBaseDashboardState>();
            builder.Services.Replace(ServiceDescriptor.Singleton<IDashboardState>(services =>
                new PausedState(services.GetRequiredService<TerraBaseDashboardState>(), scenario)));
        });
        var auth = (TestAuth)scenario.app.Services.GetRequiredService<IAuthGateway>();
        foreach (var user in new[] { scenario.User, scenario.Other })
            auth.RegisterFixturePrincipal(new(user, new Bogus.Faker().Name.FirstName(), Email(user), Password));
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
            var collection = http.Request.Query["collection"].ToString();
            var target = collection == AccountService.OrganizationsCollection
                ? (await scenario.OrganizationAsync(scenario.Other)).Id
                : (await scenario.ReferencesAsync(scenario.Other)).First(reference => reference.Collection == collection).RecordId;
            await scenario.State.PutAsync(AccountEnrollment.Collection, AccountService.NewId(),
                JsonSerializer.SerializeToElement(new AccountEnrollment.Reference(scenario.User, collection, target)), TerraBasePrecondition.Absent, default);
            return Results.NoContent();
        });
        await scenario.app.StartAsync();
        return scenario;
    }

    public HttpClient Client() => new(new HttpClientHandler { AllowAutoRedirect = false, CookieContainer = new() })
    { BaseAddress = new(app.Urls.Single().Replace("0.0.0.0", "127.0.0.1", StringComparison.Ordinal)) };

    public async Task<HttpClient> SignInAsync(string user)
    {
        var client = Client();
        using var response = await HttpJourney.LoginAsync(client, Email(user), Password);
        if (response.StatusCode != HttpStatusCode.SeeOther) throw new IOException("Fixture sign-in failed: " + response.StatusCode);
        return client;
    }

    public void Pause(string collection) => pausedCollection = collection;
    public Task<TerraBaseDocument?> GetAsync(string collection, string id) => State.GetAsync(collection, id, default);
    public Task<IReadOnlyList<AccountEnrollment.Reference>> ReferencesAsync(string user) => AccountEnrollment.ListAsync(State, user, default);
    public Task<IReadOnlyList<TerraBaseDocument>> ReferenceDocumentsAsync() => State.ListAsync(AccountEnrollment.Collection, default);

    public async Task<TerraBaseDocument> OrganizationAsync(string user)
    {
        var account = AccountService.Read<AccountRecord>((await GetAsync(AccountService.AccountsCollection, AccountService.AccountStorageKey(user)))!);
        return (await GetAsync(AccountService.OrganizationsCollection, account.PersonalOrganizationId))!;
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
        public Task InitializeAsync(CancellationToken ct) => inner.InitializeAsync(ct);
        public Task CheckReadyAsync(CancellationToken ct) => inner.CheckReadyAsync(ct);
        public Task<TerraBaseDocument?> GetAsync(string collection, string id, CancellationToken ct) => inner.GetAsync(collection, id, ct);
        public Task<IReadOnlyList<TerraBaseDocument>> ListAsync(string collection, CancellationToken ct) => inner.ListAsync(collection, ct);
        public Task<TerraBaseDocumentPage> QueryPageAsync(string collection, TerraBaseQuery query, CancellationToken ct) => inner.QueryPageAsync(collection, query, ct);
        public Task DeleteAsync(string collection, string id, string version, CancellationToken ct) => inner.DeleteAsync(collection, id, version, ct);
        public async Task PutAsync(string collection, string id, JsonElement document, TerraBasePrecondition precondition, CancellationToken ct)
        {
            if (collection == scenario.pausedCollection && Interlocked.CompareExchange(ref scenario.paused, 1, 0) == 0)
            {
                scenario.PausedId = id;
                scenario.Paused.TrySetResult();
                await scenario.Resume.Task.WaitAsync(ct);
            }
            await inner.PutAsync(collection, id, document, precondition, ct);
        }
    }
}
