using System.Globalization;
using System.Net;
using Bogus;
using TerraBase.Primitives;
using TerraBase.Testing;

namespace TerraBase.Fast.Replication;

public sealed class HintedHandoffTests
{
    [Test]
    public async Task A_restarted_sender_replays_durable_backlog_when_its_peer_returns_without_new_writes()
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(TUnit.Core.TestContext.Current!.Execution.CancellationToken);
        var token = deadline.Token;
        var faker = new Faker();
        var senderData = Directory.CreateTempSubdirectory("terrabase-handoff-sender-").FullName;
        var receiverData = Directory.CreateTempSubdirectory("terrabase-handoff-receiver-").FullName;
        var receiverPort = TerraBaseTestServer.FreePort();
        var servers = new List<TerraBaseTestServer>(4);
        var failedStarts = new List<Task>(1);
        PeerTls? ownedPeerTls = null;
        ClientTls? ownedClientTls = null;
        try
        {
            var peerTls = ownedPeerTls = new PeerTls();
            var clientTls = ownedClientTls = new ClientTls();
            void Receiver(Dictionary<string, string> settings)
            {
                peerTls.Apply(settings);
                clientTls.Apply(settings);
                settings["data-directory"] = receiverData;
                settings["peer-port"] = receiverPort.ToString(CultureInfo.InvariantCulture);
            }

            var receiver = await StartAsync(Receiver);
            var peerAddress = receiver.PeerAddress!;
            void Sender(Dictionary<string, string> settings)
            {
                peerTls.Apply(settings);
                clientTls.Apply(settings);
                settings["data-directory"] = senderData;
                settings["peer-addresses"] = peerAddress.ToString();
                settings["write-wait-ms"] = "0";
            }

            ReplicationTenant tenant;
            string senderKey, receiverKey;
            var updated = EntityIds.New().ToString();
            var deleted = EntityIds.New().ToString();
            var first = EntityIds.New().ToString();
            var second = EntityIds.New().ToString();
            var single = EntityIds.New().ToString();
            var expected = new Dictionary<string, (HttpStatusCode Status, string Version, string? Body)>(StringComparer.Ordinal);
            Uri Document(string id)
            {
                return new Uri($"{tenant.Path("db")}/collections/items/documents/{id}", UriKind.Relative);
            }

            await using (var sender = await StartAsync(Sender))
            {
                tenant = await ReplicationTenant.CreateAsync(sender, "db");
                await tenant.BindAsync(receiver);
                senderKey = await tenant.KeyAsync(sender, "documents:read", "documents:write");
                receiverKey = await tenant.KeyAsync(receiver, "documents:read");
                using var writer = clientTls.CreateClient(sender.ClientAddress!, senderKey);
                using var reader = clientTls.CreateClient(receiver.ClientAddress!, receiverKey);
                using (var collection = await writer.PostAsJsonAsync(new Uri($"{tenant.Path("db")}/collections", UriKind.Relative), new { name = "items" }, token))
                {
                    collection.EnsureSuccessStatusCode();
                }

                foreach (var id in new[] { updated, deleted })
                {
                    using var baseline = await writer.PutAsJsonAsync(Document(id), new { name = faker.Name.FullName(), revision = 1 }, token);
                    baseline.EnsureSuccessStatusCode();
                }

                await TerraBaseTestServer.ReplicatedAsync(sender);
                foreach (var id in new[] { updated, deleted })
                {
                    var baseline = await ReadAsync(writer, Document(id), token);
                    await Assert.That(baseline.Status).IsEqualTo(HttpStatusCode.OK);
                    await Assert.That(await ReadAsync(reader, Document(id), token)).IsEqualTo(baseline);
                }

                await receiver.DisposeAsync();
                using (var transaction = await writer.PostAsJsonAsync(new Uri($"{tenant.Path("db")}/transactions", UriKind.Relative), new
                {
                    ops = new[]
                    {
                        new { op = "put", collection = "items", id = first, document = new { name = faker.Name.FullName(), revision = 1 } },
                        new { op = "put", collection = "items", id = second, document = new { name = faker.Name.FullName(), revision = 1 } },
                    },
                }, token))
                {
                    transaction.EnsureSuccessStatusCode();
                }

                using (var added = await writer.PutAsJsonAsync(Document(single), new { name = faker.Name.FullName(), revision = 1 }, token))
                {
                    added.EnsureSuccessStatusCode();
                }

                using (var changed = await writer.PutAsJsonAsync(Document(updated), new { name = faker.Name.FullName(), revision = 2 }, token))
                {
                    changed.EnsureSuccessStatusCode();
                }

                using (var removed = await writer.DeleteAsync(Document(deleted), token))
                {
                    removed.EnsureSuccessStatusCode();
                }

                foreach (var id in new[] { updated, deleted, first, second, single })
                {
                    var snapshot = await ReadAsync(writer, Document(id), token);
                    await Assert.That(snapshot.Status).IsEqualTo(id == deleted ? HttpStatusCode.NotFound : HttpStatusCode.OK);
                    expected.Add(id, snapshot);
                }

                await TerraBaseTestServer.EventuallyAsync(async cancellationToken =>
                    await sender.MetricAsync("terrabase_replication_sender_durable_lag", "", cancellationToken) > 0);
            }

            // No API keys or tenant writes are created after restart: startup discovery must find the persisted journal.
            await using var restarted = await StartAsync(Sender);
            await TerraBaseTestServer.EventuallyAsync(async cancellationToken =>
                await restarted.MetricAsync("terrabase_replication_sender_initialized", "", cancellationToken) == 1
                && await restarted.MetricAsync("terrabase_replication_sender_durable_lag", "", cancellationToken) > 0);
            await using var returned = await StartAsync(Receiver);
            await Assert.That(returned.PeerAddress).IsEqualTo(peerAddress);
            await TerraBaseTestServer.ReplicatedAsync(restarted);
            using var sourceReader = clientTls.CreateClient(restarted.ClientAddress!, senderKey);
            using var remoteReader = clientTls.CreateClient(returned.ClientAddress!, receiverKey);
            foreach (var (id, snapshot) in expected)
            {
                await Assert.That(await ReadAsync(sourceReader, Document(id), token)).IsEqualTo(snapshot);
                await Assert.That(await ReadAsync(remoteReader, Document(id), token)).IsEqualTo(snapshot);
            }
        }
        finally
        {
            await TerraBaseTestServer.WaitForCleanupAsync(ReleaseResourcesAsync(), TerraBaseTestServer.ReportCleanupFailure, token);
        }

        async Task<TerraBaseTestServer> StartAsync(Action<Dictionary<string, string>> configure)
        {
            var server = await TerraBaseTestServer.StartAsync(configure, failedStartCleanup: failedStarts.Add);
            servers.Add(server);
            return server;
        }

        async Task ReleaseResourcesAsync()
        {
            var errors = new List<Exception>();
            foreach (var server in servers)
            {
                Observe(server.Client.Dispose);
            }

            var cleanup = Task.WhenAll(servers.Select(server => server.CleanupCompletion).Concat(failedStarts));
            try
            {
                await cleanup;
            }
            catch (Exception error)
            {
                errors.Add(cleanup.Exception ?? error);
            }

            Observe(() => Directory.Delete(senderData, recursive: true));
            Observe(() => Directory.Delete(receiverData, recursive: true));
            Observe(() => ownedClientTls?.Dispose());
            Observe(() => ownedPeerTls?.Dispose());
            if (errors.Count > 0)
            {
                throw new AggregateException(errors).Flatten();
            }

            void Observe(Action release)
            {
                try
                {
                    release();
                }
                catch (Exception error)
                {
                    errors.Add(error);
                }
            }
        }
    }

    private static async Task<(HttpStatusCode Status, string Version, string? Body)> ReadAsync(HttpClient client, Uri document, CancellationToken cancellationToken)
    {
        using var response = await client.GetAsync(document, cancellationToken);
        // Tombstones must retain an ETag; a plain missing-document 404 is insufficient.
        await Assert.That(response.Headers.ETag).IsNotNull();
        return (response.StatusCode, response.Headers.ETag!.ToString(), response.IsSuccessStatusCode
            ? await response.Content.ReadAsStringAsync(cancellationToken)
            : null);
    }
}
