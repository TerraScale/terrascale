using System.Collections.Concurrent;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Time.Testing;
using TerraBase.Features.Replication;
using TerraBase.Primitives;
using TerraBase.Tenancy;
using TerraBase.Testing;

namespace TerraBase.Fast.Replication;

public sealed partial class OperatorRecoveryTests
{
    [Test]
    public async Task Recovery_requires_the_peer_credential_an_existing_owner_and_the_exact_live_tenant()
    {
        var probe = new RebuildProbe();
        await using var api = await StartAsync(probe, new Freshness());
        var database = await api.CreateManagedDatabaseAsync();
        var request = new { faultedIncarnation = EntityIds.New().ToString(), reason = "Approved retry" };
        using var uncredentialed = new HttpClient
        {
            BaseAddress = api.Server.BaseAddress,
            DefaultRequestVersion = HttpVersion.Version20,
            DefaultVersionPolicy = HttpVersionPolicy.RequestVersionExact,
        };
        uncredentialed.DefaultRequestHeaders.Add("X-TerraBase-User-Id", api.User);
        using var bare = await uncredentialed.PostAsJsonAsync(Path(database), request);
        using var publicOwner = await api.Client.PostAsJsonAsync(Path(database), request);
        using var unnamed = await api.Server.Client.PostAsJsonAsync(Path(database), request);
        using var stranger = api.Service(EntityIds.New().ToString());
        using var crossTenant = await stranger.PostAsJsonAsync(Path(database), request);
        using var owner = api.Service(api.User);
        using var admin = await owner.PostAsJsonAsync(Path(database), request);
        var other = await api.CreateManagedDatabaseAsync("other");
        using var wrongProject = await owner.PostAsJsonAsync(ManagementApi.Uri($"{database.ProjectPath}/databases/{other.Id}/recovery/retry"), request);
        using var removed = await api.Client.DeleteAsync(ManagementApi.Uri(database.Path));
        removed.EnsureSuccessStatusCode();
        using var deleted = await owner.PostAsJsonAsync(Path(database), request);

        await Assert.That(bare.StatusCode).IsEqualTo(HttpStatusCode.Unauthorized);
        await Assert.That(publicOwner.StatusCode).IsEqualTo(HttpStatusCode.NotFound);
        await Assert.That(unnamed.StatusCode).IsEqualTo(HttpStatusCode.Unauthorized);
        await Assert.That(crossTenant.StatusCode).IsEqualTo(HttpStatusCode.NotFound);
        await Assert.That(admin.StatusCode).IsEqualTo(HttpStatusCode.Conflict).Because("an existing owner reaches the pending-record check");
        await Assert.That(wrongProject.StatusCode).IsEqualTo(HttpStatusCode.NotFound);
        await Assert.That(deleted.StatusCode).IsEqualTo(HttpStatusCode.NotFound);
        await Assert.That(probe.Calls).IsEqualTo(0);
    }

    [Test]
    public async Task Recovery_checks_reason_incarnation_and_freshness_before_dispatch_and_audits_without_free_text()
    {
        var probe = new RebuildProbe();
        var freshness = new Freshness();
        var logs = new Logs();
        await using var api = await StartAsync(probe, freshness, logs);
        var database = await api.CreateManagedDatabaseAsync();
        using var owner = api.Service(api.User);
        var token = TestContext.Current!.Execution.CancellationToken;
        var tenant = new TenantContext(database.Organization, PlanTier.Hobby, database.Project, database.Id);
        var storage = api.Server.Services.GetRequiredService<IRebuildStorage>();
        var old = await storage.IncarnationAsync(tenant, token) ?? throw new InvalidOperationException("fixture file missing");
        var ledger = api.Server.Services.GetRequiredService<IRebuildLedger>();
        await ledger.SaveAsync(new RebuildRecord(tenant, old, storage.SidePath(tenant, old), RebuildPhase.Pending, null), token);
        foreach (var reason in new string?[] { null, " ", "\nretry", new('x', 257) })
        {
            using var invalid = await owner.PostAsJsonAsync(Path(database), new { faultedIncarnation = old.ToString(), reason }, token);
            await Assert.That(invalid.StatusCode).IsEqualTo(HttpStatusCode.BadRequest);
        }

        using var mismatch = await owner.PostAsJsonAsync(Path(database), new { faultedIncarnation = EntityIds.New().ToString(), reason = "retry" }, token);
        await Assert.That(mismatch.StatusCode).IsEqualTo(HttpStatusCode.Conflict);
        freshness.Passes = 0;
        using var stale = await owner.PostAsJsonAsync(Path(database), new { faultedIncarnation = old.ToString(), reason = "retry" }, token);
        await Assert.That(stale.StatusCode).IsEqualTo(HttpStatusCode.ServiceUnavailable);
        freshness.Passes = 1;
        using var expired = await owner.PostAsJsonAsync(Path(database), new { faultedIncarnation = old.ToString(), reason = "retry" }, token);
        await Assert.That(expired.StatusCode).IsEqualTo(HttpStatusCode.ServiceUnavailable);
        await Assert.That(probe.Calls).IsEqualTo(0);
        freshness.Passes = int.MaxValue;
        const string privateReason = "incident details must stay out of logs";
        using var pending = await owner.PostAsJsonAsync(Path(database), new { faultedIncarnation = old.ToString(), reason = privateReason }, token);
        await Assert.That(pending.StatusCode).IsEqualTo(HttpStatusCode.Conflict);
        await Assert.That(probe.Calls).IsEqualTo(1);
        await Assert.That(probe.Database).IsEqualTo(tenant);
        await Assert.That(probe.Incarnation).IsEqualTo(old);
        await Assert.That(await ledger.GetAsync(tenant, token)).IsNotNull();
        await Assert.That(await storage.IncarnationAsync(tenant, token)).IsEqualTo(old);
        var audit = string.Join('\n', logs.Messages.Where(message => message.StartsWith("Tenant recovery:", StringComparison.Ordinal)));
        await Assert.That(audit.Contains(privateReason, StringComparison.Ordinal)).IsFalse();
        await Assert.That(audit.Contains(Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(privateReason))), StringComparison.Ordinal)).IsTrue();
        await Assert.That(audit.Contains($"operator {api.User}, organization {database.Organization}, project {database.Project}, database {database.Id}", StringComparison.Ordinal)).IsTrue();
        await Assert.That(audit.Contains("action retry_certified_rebuild, outcome pending", StringComparison.Ordinal)).IsTrue();
    }

    private static Uri Path(ManagedDatabaseRef database) => ManagementApi.Uri(database.Path + "/recovery/retry");

    private static Task<ManagementApi> StartAsync(RebuildProbe probe, Freshness freshness, Logs? logs = null) =>
        ManagementApi.StartAsync(services: services =>
        {
            services.AddSingleton<IReplicaRebuild>(probe);
            services.AddSingleton<ICatalogFreshness>(freshness);
            if (logs is not null)
            {
                services.AddSingleton<ILoggerProvider>(logs);
            }
        }, time: new FakeTimeProvider(DateTimeOffset.UtcNow));

    private sealed class Freshness : ICatalogFreshness
    {
        public int Passes { get; set; } = int.MaxValue;
        public ValueTask<bool> IsFreshAsync(TimeSpan window, CancellationToken cancellationToken) => ValueTask.FromResult(Passes-- > 0);
    }

    private sealed class RebuildProbe : IReplicaRebuild
    {
        public int Calls { get; private set; }
        public TenantContext? Database { get; private set; }
        public Ulid Incarnation { get; private set; }
        public ValueTask<RebuildOutcome> RebuildAsync(TenantContext database, Ulid faultedIncarnation, CancellationToken cancellationToken)
        {
            Calls++;
            Database = database;
            Incarnation = faultedIncarnation;
            return ValueTask.FromResult<RebuildOutcome>(new RebuildOutcome.Refused("untrusted peer diagnostic must not enter the audit"));
        }
    }

    private sealed class Logs : ILoggerProvider
    {
        public ConcurrentQueue<string> Messages { get; } = new();
        public ILogger CreateLogger(string categoryName) => new CapturedLogger(Messages);
        public void Dispose() { }
        private sealed class CapturedLogger(ConcurrentQueue<string> messages) : ILogger
        {
            public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
            public bool IsEnabled(LogLevel logLevel) => true;
            public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter) =>
                messages.Enqueue(formatter(state, exception));
        }
    }
}
