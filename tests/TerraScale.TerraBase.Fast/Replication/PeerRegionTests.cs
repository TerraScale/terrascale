using System.Collections.Concurrent;
using Bogus;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using TerraBase.Features.Management.Usage;
using TerraBase.Features.Replication;
using TerraBase.Features.Replication.V1;
using TerraBase.Tenancy;
using TerraBase.Testing;

namespace TerraBase.Fast.Replication;

public sealed class PeerRegionTests
{
    [Test]
    [Arguments("")]
    [Arguments("iad-1")]
    [Arguments("GRU")]
    [Arguments("gru-")]
    [Arguments("bad\nregion")]
    [Arguments("too-long")]
    public async Task Peer_regions_use_the_node_syntax_and_older_peers_may_omit_them(string region)
    {
        using var tls = new PeerTls();
        await using var receiver = await TerraBaseTestServer.StartAsync(settings =>
        {
            tls.Apply(settings);
            settings["region"] = "gru-1";
        });
        var database = await receiver.RegisterAsync(new TenantContext(Guid.NewGuid().ToString(), PlanTier.Hobby, Guid.NewGuid().ToString(), Guid.NewGuid().ToString()));
        using var channel = tls.CreateChannel(receiver.PeerAddress!);
        using var call = new Features.Replication.V1.Replication.ReplicationClient(channel).Sync(cancellationToken: TUnit.Core.TestContext.Current!.Execution.CancellationToken);
        await call.RequestStream.WriteAsync(new SyncRequest
        {
            Hello = ReplicationProtocol.CreateHello(database, Guid.NewGuid().ToByteArray(bigEndian: true), region == "too-long" ? new string('a', 64) : region),
        });
        await Assert.That(await call.ResponseStream.MoveNext(CancellationToken.None)).IsTrue();
        if (region is "" or "iad-1")
        {
            await Assert.That(call.ResponseStream.Current.Welcome.LocalIdentity.Region).IsEqualTo("gru-1");
        }
        else
        {
            await Assert.That(call.ResponseStream.Current.Error.Code).IsEqualTo(StreamErrorCode.StreamErrorUnsupportedFormat);
        }

        await call.RequestStream.CompleteAsync();
        await Assert.That(await call.ResponseStream.MoveNext(CancellationToken.None)).IsFalse();
    }

    [Test]
    public async Task Peers_exchange_and_log_distinct_regions_while_replicating()
    {
        using var tls = new PeerTls();
        using var clientTls = new ClientTls();
        using var receiving = new RegionLogs();
        using var sending = new RegionLogs();
        await using var receiver = await TerraBaseTestServer.StartAsync(settings =>
        {
            tls.Apply(settings);
            clientTls.Apply(settings);
            settings["region"] = "gru-1";
        }, services => services.AddSingleton<ILoggerProvider>(receiving));
        await using var sender = await TerraBaseTestServer.StartAsync(settings =>
        {
            tls.Apply(settings);
            clientTls.Apply(settings);
            settings["region"] = "iad-1";
            settings["peer-addresses"] = receiver.PeerAddress!.ToString();
        }, services => services.AddSingleton<ILoggerProvider>(sending));
        var tenant = await ReplicationTenant.CreateAsync(sender, "regions");
        await tenant.BindAsync(receiver);
        using var writer = clientTls.CreateClient(sender.ClientAddress!, await tenant.KeyAsync(sender, "documents:write"));
        using var reader = clientTls.CreateClient(receiver.ClientAddress!, await tenant.KeyAsync(receiver, "documents:read"));
        using var collection = await writer.PostAsJsonAsync(new Uri($"{tenant.Path("regions")}/collections", UriKind.Relative), new { name = "items" });
        collection.EnsureSuccessStatusCode();
        var path = $"{tenant.Path("regions")}/collections/items/documents/{ReplicationDocuments.Id("regional")}";
        using var put = await writer.PutAsJsonAsync(new Uri(path, UriKind.Relative), new { region = "iad-1" });
        put.EnsureSuccessStatusCode();
        await TerraBaseTestServer.ReplicatedAsync(sender);
        using var get = await reader.GetAsync(new Uri(path, UriKind.Relative));
        get.EnsureSuccessStatusCode();
        await Assert.That(await get.Content.ReadAsStringAsync()).Contains("iad-1");
        await TerraBaseTestServer.EventuallyAsync(_ => Task.FromResult(
            receiving.Messages.Contains("Replication receiving in region gru-1 from peer region iad-1")
            && sending.Messages.Contains("Replication sending from region iad-1 to peer #0 in region gru-1")));
    }

    [Test]
    public async Task Journal_replay_longer_than_one_turn_is_metered_as_handoff_until_the_peer_reaches_the_head()
    {
        using var tls = new PeerTls();
        using var clientTls = new ClientTls();
        var senderData = Directory.CreateTempSubdirectory("terrabase-handoff-turns-").FullName;
        try
        {
            void Sender(Dictionary<string, string> settings)
            {
                tls.Apply(settings);
                clientTls.Apply(settings);
                settings["region"] = "iad-1";
                settings["data-directory"] = senderData;
            }

            // More groups than the sender's 256-group turn, committed before it knows any peer: replay spans turns.
            ReplicationTenant tenant;
            await using (var alone = await TerraBaseTestServer.StartAsync(Sender))
            {
                tenant = await ReplicationTenant.CreateAsync(alone, "turns");
                using var writer = clientTls.CreateClient(alone.ClientAddress!, await tenant.KeyAsync(alone, "documents:write"));
                using (var collection = await writer.PostAsJsonAsync(new Uri($"{tenant.Path("turns")}/collections", UriKind.Relative), new { name = "items" }))
                {
                    collection.EnsureSuccessStatusCode();
                }

                var faker = new Faker();
                for (var i = 0; i < 300; i++)
                {
                    using var put = await writer.PutAsJsonAsync(
                        new Uri($"{tenant.Path("turns")}/collections/items/documents/{ReplicationDocuments.Id($"turn-{i}")}", UriKind.Relative),
                        new { name = faker.Name.FullName() });
                    put.EnsureSuccessStatusCode();
                }
            }

            await using var receiver = await TerraBaseTestServer.StartAsync(settings =>
            {
                tls.Apply(settings);
                clientTls.Apply(settings);
                settings["region"] = "gru-1";
            });
            await tenant.BindAsync(receiver);
            await using var sender = await TerraBaseTestServer.StartAsync(settings =>
            {
                Sender(settings);
                settings["peer-addresses"] = receiver.PeerAddress!.ToString();
            });
            await TerraBaseTestServer.ReplicatedAsync(sender);

            var hour = DateTimeOffset.UtcNow.ToUnixTimeSeconds() / 3600;
            var (lifetime, _) = await sender.Services.GetRequiredService<UsageMeter>()
                .GetObservationsAsync(tenant.Database("turns"), new UsageWindow(UsageWindow.Hour, hour, 1), CancellationToken.None);
            var meters = lifetime.Meters ?? new Dictionary<string, UsageReading>();
            await Assert.That(meters.GetValueOrDefault("replication_bytes|iad-1|gru-1|handoff:sent")?.Count ?? 0).IsGreaterThan(0);
            await Assert.That(meters.ContainsKey("replication_bytes|iad-1|gru-1|delta:sent")).IsFalse();
        }
        finally
        {
            Directory.Delete(senderData, recursive: true);
        }
    }

    private sealed class RegionLogs : ILoggerProvider, ILogger
    {
        public ConcurrentQueue<string> Messages { get; } = new();

        public ILogger CreateLogger(string categoryName) => this;

        public IDisposable? BeginScope<TState>(TState state)
            where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter) =>
            Messages.Enqueue(formatter(state, exception));

        public void Dispose()
        {
        }
    }
}
