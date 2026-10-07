using System.Collections.Concurrent;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using TerraScale.TerraBase.Features.Replication;
using TerraScale.TerraBase.Features.Replication.V1;
using TerraScale.TerraBase.Tenancy;
using TerraScale.TerraBase.Testing;

namespace TerraScale.TerraBase.Fast.Replication;

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
        using var call = new Features.Replication.V1.Replication.ReplicationClient(channel).Sync(deadline: DateTime.UtcNow.AddSeconds(30));
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
