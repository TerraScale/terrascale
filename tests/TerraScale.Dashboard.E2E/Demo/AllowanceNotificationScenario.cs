using System.Collections.Concurrent;
using System.Net;
using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using TerraScale.Dashboard.Features.Notifications;
using TerraScale.Dashboard.E2E.Features.Databases;
using TerraScale.Dashboard.Platform.Persistence;

namespace TerraScale.Dashboard.E2E;

// Controls only the fake producer and clock; the application's hosted worker and inbox stay real.
internal sealed class AllowanceNotificationScenario(string root) : IAsyncDisposable
{
    private readonly DemoEnvironment environment = new();
    private readonly AllowanceClock clock = new();
    private TestAllowanceNotifications backend = null!;
    private WebApplication? host;
    private string organizationId = "";
    private string accountKey = "";
    private (string Email, string Password)? principal;
    public HttpClient Client { get; private set; } = null!;
    public string Csrf { get; private set; } = "";

    public async Task StartAsync()
    {
        await environment.InitializeAsync();
        backend = new(clock);
        await StartHostAsync();
    }

    public async Task RestartAsync()
    {
        Client.Dispose();
        await host!.StopAsync();
        await host.DisposeAsync();
        host = null;
        await StartHostAsync();
    }

    private async Task StartHostAsync()
    {
        host = environment.CreateTestHost(root, 0, "http://127.0.0.1", builder =>
        {
            builder.Services.Replace(ServiceDescriptor.Singleton<IAllowanceNotificationBackend>(backend));
            builder.Services.Replace(ServiceDescriptor.Singleton<AllowanceNotifications>(services => new(
                services.GetRequiredService<IDashboardState>(), backend, clock,
                services.GetRequiredService<ILogger<AllowanceNotifications>>())));
            // Only allowance processing advances billing time; sign-in and session clocks stay real.
            var worker = builder.Services.Single(service => service.ServiceType == typeof(IHostedService) &&
                service.ImplementationType == typeof(AllowanceNotificationWorker));
            builder.Services.Remove(worker);
            builder.Services.AddSingleton<IHostedService>(services => new AllowanceNotificationWorker(
                services.GetRequiredService<AllowanceNotifications>(), clock,
                services.GetRequiredService<ILogger<AllowanceNotificationWorker>>()));
        });
        // These controls are mapped only on this isolated E2E host, never on normal Demo hosts.
        host.MapPost("/__test/allowance/cycle", async (HttpContext http) =>
        {
            var form = await http.Request.ReadFormAsync(http.RequestAborted);
            if (!TerraScale.Dashboard.Platform.Identity.Csrf.Validate(http, form)) return Results.BadRequest();
            if (form["nextMonth"] == "true") clock.NextMonth();
            var limits = PlanCatalog.Limits("free");
            long? Used(string key, long limit) => int.TryParse(form[key], out var percent) ? limit * percent / 100 : null;
            backend.Observe(new(organizationId, Used("operations", limits.MonthlyOperations), Used("storage", limits.StorageBytes)),
                form["outcome"].ToString() is { Length: > 0 } delivery ? delivery : "accepted", form["unsupported"] != "true",
                form["operationsContract"].ToString() is { Length: > 0 } contract ? contract : null);
            clock.Tick();
            await backend.WaitForReadAsync(http.RequestAborted);
            // A second, empty pass can only start after the observed pass finished, even if it failed closed.
            backend.ClearObservation();
            clock.Tick();
            await backend.WaitForReadAsync(http.RequestAborted);
            return Results.NoContent();
        });
        host.MapPost("/__test/allowance/role", async (HttpContext http) =>
        {
            var form = await http.Request.ReadFormAsync(http.RequestAborted);
            if (!TerraScale.Dashboard.Platform.Identity.Csrf.Validate(http, form)) return Results.BadRequest();
            var role = form["role"].ToString();
            if (role is not ("owner" or "admin" or "member")) return Results.BadRequest();
            environment.Workspace.GlobalRole = role;
            return Results.NoContent();
        });
        host.MapGet("/__test/allowance/snapshot", async () =>
        {
            var ledger = await environment.State.GetAsync(AllowanceNotifications.Collection, organizationId, default);
            var account = await environment.State.GetAsync(AccountService.AccountsCollection, accountKey, default);
            return Results.Json(new
            {
                attempts = backend.Attempts.Count,
                sent = backend.Sent.Count,
                ledgerExists = ledger is not null,
                alerts = ledger is null ? [] : AllowanceNotifications.Read(ledger, organizationId).Alerts,
                inbox = AccountService.Read<AccountRecord>(account!).Notifications
            });
        });
        await host.StartAsync();
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        await backend.WaitForReadAsync(deadline.Token);
        Client = new(new HttpClientHandler { ServerCertificateCustomValidationCallback = TestHttps.Validate, AllowAutoRedirect = false, CookieContainer = new() })
        { BaseAddress = new(host.Urls.Single().Replace("0.0.0.0", "127.0.0.1", StringComparison.Ordinal)), Timeout = TimeSpan.FromSeconds(20) };
        if (principal is null) principal = await DatabaseSdkJourneyTests.NativeWorkspaceAsync(Client);
        else
        {
            using var login = await HttpJourney.LoginAsync(Client, principal.Value.Email, principal.Value.Password);
            await Assert.That(login.StatusCode).IsEqualTo(HttpStatusCode.SeeOther);
        }
        Csrf = await HttpJourney.CsrfAsync(Client);
        // Resolve the account created by the actual sign-in, including its persisted identity binding.
        using var identity = JsonDocument.Parse(await Client.GetStringAsync("/api/v1/auth/me"));
        organizationId = identity.RootElement.GetProperty("organizationId").GetString()!;
        accountKey = AccountService.AccountStorageKey(identity.RootElement.GetProperty("userId").GetString()!);
    }

    public async ValueTask DisposeAsync()
    {
        Client?.Dispose();
        if (host is not null) { await host.StopAsync(); await host.DisposeAsync(); }
    }

    private sealed class AllowanceClock : TimeProvider
    {
        private readonly ConcurrentDictionary<ManualTimer, byte> timers = new();
        private long offset;
        public override DateTimeOffset GetUtcNow() => DateTimeOffset.UtcNow.AddTicks(Interlocked.Read(ref offset));
        public void NextMonth()
        {
            var now = GetUtcNow();
            var next = new DateTimeOffset(now.Year, now.Month, 1, 12, 0, 0, TimeSpan.Zero).AddMonths(1);
            Interlocked.Add(ref offset, (next - now).Ticks);
        }
        public void Tick() { foreach (var timer in timers.Keys) timer.Fire(); }
        public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
        {
            var timer = new ManualTimer(callback, state, timers);
            timers.TryAdd(timer, 0);
            return timer;
        }
        private sealed class ManualTimer(TimerCallback callback, object? state, ConcurrentDictionary<ManualTimer, byte> timers) : ITimer
        {
            public void Fire() { if (timers.ContainsKey(this)) callback(state); }
            public bool Change(TimeSpan dueTime, TimeSpan period) => timers.ContainsKey(this);
            public void Dispose() => timers.TryRemove(this, out _);
            public ValueTask DisposeAsync() { Dispose(); return ValueTask.CompletedTask; }
        }
    }
}
