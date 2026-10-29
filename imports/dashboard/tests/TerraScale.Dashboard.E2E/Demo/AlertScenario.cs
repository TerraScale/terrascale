using System.Collections.Concurrent;
using System.Net;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using TerraScale.Dashboard.E2E.Features.Databases;
using TerraScale.Dashboard.Features.Logs;
using TerraScale.Dashboard.Features.Notifications;

namespace TerraScale.Dashboard.E2E;

// Alerts and log drains. Fakes only TerraBase analytics and logs, ts-auth email and the customer's receiver; storage,
// evaluation, shipping and pages stay real. The hosted workers are removed so each test drives runs explicitly.
internal sealed class AlertScenario(string root) : IAsyncDisposable
{
    private readonly DemoEnvironment environment = new();
    private WebApplication? host;
    public FakeAlertSource Source { get; } = new();
    public RecordingDelivery Delivery { get; } = new();
    public FakeLogSource Logs { get; } = new();
    public HttpClient Client { get; private set; } = null!;
    public string Csrf { get; private set; } = "";
    public string DatabaseId { get; private set; } = "";
    public string OrganizationId { get; private set; } = "";

    public async Task StartAsync()
    {
        await environment.InitializeAsync();
        host = environment.CreateTestHost(root, 0, "http://127.0.0.1", builder =>
        {
            builder.Services.Replace(ServiceDescriptor.Singleton<IAlertSource>(Source));
            builder.Services.Replace(ServiceDescriptor.Singleton<ICustomerDelivery>(Delivery));
            builder.Services.Replace(ServiceDescriptor.Singleton<ILogDrainSource>(Logs));
            foreach (var worker in new[] { typeof(AlertWorker), typeof(LogDrainWorker) })
                builder.Services.Remove(builder.Services.Single(service => service.ServiceType == typeof(IHostedService) && service.ImplementationType == worker));
        });
        await host.StartAsync();
        Client = new(new HttpClientHandler { ServerCertificateCustomValidationCallback = TestHttps.Validate, AllowAutoRedirect = false, CookieContainer = new() })
        { BaseAddress = new(host.Urls.Single().Replace("0.0.0.0", "127.0.0.1", StringComparison.Ordinal)), Timeout = TimeSpan.FromSeconds(20) };
        await DatabaseSdkJourneyTests.NativeWorkspaceAsync(Client);
        Csrf = await HttpJourney.CsrfAsync(Client, "/databases");
        using (var created = await HttpJourney.PostFormAsync(Client, "/api/v1/dashboard/databases", new() { ["_csrf"] = Csrf, ["name"] = "orders", ["region"] = "GRU" }))
        {
            await Assert.That(created.StatusCode).IsEqualTo(HttpStatusCode.Created);
            using var json = JsonDocument.Parse(await created.Content.ReadAsStringAsync());
            DatabaseId = json.RootElement.GetProperty("id").GetString()!;
        }
        using var identity = JsonDocument.Parse(await Client.GetStringAsync("/api/v1/auth/me"));
        OrganizationId = identity.RootElement.GetProperty("organizationId").GetString()!;
    }

    public void Role(string role) => environment.Workspace.GlobalRole = role;

    public Task RunAsync() => host!.Services.GetRequiredService<AlertRules>().RunOnceAsync(CancellationToken.None);
    public Task RunDrainsAsync() => host!.Services.GetRequiredService<LogDrains>().RunOnceAsync(CancellationToken.None);

    public Task<HttpResponseMessage> PostAsync(string path, Dictionary<string, string> form)
    {
        form["_csrf"] = Csrf;
        return HttpJourney.PostFormAsync(Client, path, form);
    }

    public async ValueTask DisposeAsync()
    {
        Client?.Dispose();
        if (host is not null) { await host.StopAsync(); await host.DisposeAsync(); }
    }
}

internal sealed class FakeAlertSource : IAlertSource
{
    private AlertAnalytics analytics = new(DateTimeOffset.MinValue, []);
    public ConcurrentQueue<AlertEmail> Emails { get; } = new();

    // Ten minutes of one-minute buckets ending in a partial bucket that must never be evaluated.
    public void Observe(string databaseId, double p95Ms, long requests = 100, long errors = 0)
    {
        var now = DateTimeOffset.UtcNow;
        var to = new DateTimeOffset(now.Year, now.Month, now.Day, now.Hour, now.Minute, 0, TimeSpan.Zero).AddMinutes(1);
        var buckets = Enumerable.Range(1, 10).Reverse().Select(minutes => new AlertBucket(to.AddMinutes(-minutes), requests, errors, p95Ms, 2_000_000_000)).ToList();
        buckets[^1] = buckets[^1] with { P95Ms = 1_000_000, Errors = requests };
        analytics = new(to, [new AlertSeries(databaseId, "orders", "sa-saopaulo-1", buckets)]);
    }

    public Task<AlertAnalytics> ReadAnalyticsAsync(string projectId, CancellationToken cancellationToken) => Task.FromResult(analytics);
    public Task<IReadOnlyDictionary<string, long>> ReadOperationUnitsAsync(CancellationToken cancellationToken) =>
        Task.FromResult<IReadOnlyDictionary<string, long>>(new Dictionary<string, long>());
    public Task<string> SendEmailAsync(AlertEmail email, CancellationToken cancellationToken) { Emails.Enqueue(email); return Task.FromResult("accepted"); }
}

internal sealed class RecordingDelivery : ICustomerDelivery
{
    public ConcurrentQueue<(Uri Url, string Body, Dictionary<string, string> Headers)> Requests { get; } = new();
    public int Status { get; set; } = 204;

    public Task<int> PostAsync(Uri url, byte[] body, IReadOnlyList<KeyValuePair<string, string>> headers, CancellationToken cancellationToken)
    {
        Requests.Enqueue((url, Encoding.UTF8.GetString(body), headers.ToDictionary(header => header.Key, header => header.Value)));
        return Task.FromResult(Status);
    }
}

internal sealed class FakeLogSource : ILogDrainSource
{
    private readonly ConcurrentStack<DatabaseLogEntry> entries = new();

    public DatabaseLogEntry Record(int status, DateTimeOffset? time = null)
    {
        var at = time ?? DateTimeOffset.UtcNow;
        var entry = new DatabaseLogEntry(NUlid.Ulid.NewUlid(at).ToString(), at, "sa-saopaulo-1", "POST", "/v1/collections/orders/documents", status, 12.5, 3, false, AccountService.NewId());
        entries.Push(entry);
        return entry;
    }

    public bool Gap { get; set; }

    // TerraBase's cursor read, without its settle delay: entries after the cursor, oldest first.
    public Task<DatabaseLogBatch> ReadAsync(string projectId, string databaseId, string after, CancellationToken cancellationToken)
    {
        var items = entries.Where(entry => string.CompareOrdinal(entry.Id, after) > 0).OrderBy(entry => entry.Id, StringComparer.Ordinal).Take(100).ToArray();
        return Task.FromResult(new DatabaseLogBatch(databaseId, items, false, false, items.Length > 0 ? items[^1].Id : after, Gap));
    }
}
