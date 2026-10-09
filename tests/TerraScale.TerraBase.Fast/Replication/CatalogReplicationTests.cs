using System.Net;
using System.Text;
using System.Text.Json;
using Google.Protobuf;
using Grpc.Core;
using Microsoft.Extensions.DependencyInjection;
using TerraBase.Engine;
using TerraBase.Errors;
using TerraBase.Features.ApiKeys;
using TerraBase.Features.Management;
using TerraBase.Features.Replication;
using TerraBase.Features.Replication.V1;
using TerraBase.Tenancy;
using TerraBase.Testing;

namespace TerraBase.Fast.Replication;

/// <summary>Catalog primary and followers (docs/decisions/0009).</summary>
public sealed class CatalogReplicationTests
{
    private static readonly string[] Scopes = [ApiKeyScopes.DocumentsRead, ApiKeyScopes.DocumentsWrite];

    [Test]
    public async Task Organizations_databases_and_keys_of_the_primary_serve_on_a_follower_that_writes_no_catalog_itself()
    {
        using var peers = new PeerTls();
        using var tls = new ClientTls();
        await using var primary = await TerraBaseTestServer.StartAsync(settings => { peers.Apply(settings); tls.Apply(settings); });
        var tenant = await CatalogTenant.CreateAsync(primary, "orders");
        await using var follower = await TerraBaseTestServer.StartAsync(settings =>
        {
            peers.Apply(settings);
            tls.Apply(settings);
            settings["catalog-primary"] = primary.PeerAddress!.ToString();
        });
        await ReadyAsync(follower);

        // The follower serves the primary's key and database, which it has no file of yet.
        using var data = tls.CreateClient(follower.ClientAddress!, tenant.Key);
        await Assert.That(await PutAsync(data, tenant, "orders", "first")).IsEqualTo(HttpStatusCode.Created);
        await Assert.That(await GetAsync(data, tenant, "orders", "first")).IsEqualTo(HttpStatusCode.OK);
        using (var existing = await data.PutAsync(tenant.Database("orders"), null))
        {
            await Assert.That(existing.StatusCode).IsEqualTo(HttpStatusCode.OK);
        }

        // A new database is created by the primary and serves here at once; the follower writes no catalog itself, so an
        // operator key (issued only on the primary's loopback listener) is refused with nothing written.
        using (var database = await data.PutAsync(tenant.Database("new"), null))
        {
            await Assert.That(database.StatusCode).IsEqualTo(HttpStatusCode.Created);
        }

        await Assert.That(await PutAsync(data, tenant, "new", "first")).IsEqualTo(HttpStatusCode.Created);
        using (var issued = await follower.Client.PostAsJsonAsync(
            new Uri("/admin/api-keys", UriKind.Relative), new { name = "denied", scopes = Scopes, organizationId = tenant.Organization }))
        {
            await NotPrimaryAsync(issued);
        }

        // Keys issued and revoked on the primary later take effect on the follower.
        var keys = primary.Services.GetRequiredService<IApiKeyStore>();
        var issuedLater = await keys.CreateProjectKeyAsync(tenant.User, tenant.Organization, tenant.Project, "later", Scopes, null, CancellationToken.None)
            is CreatedProjectApiKey created ? created : throw new InvalidOperationException("the primary did not issue the key");
        using var later = tls.CreateClient(follower.ClientAddress!, issuedLater.Secret);
        await TerraBaseTestServer.EventuallyAsync(
            async token => await GetAsync(later, tenant, "orders", "first", token) == HttpStatusCode.OK, TimeSpan.FromSeconds(5));
        await Assert.That(await keys.RevokeProjectKeyAsync(tenant.User, tenant.Organization, tenant.Project, issuedLater.Key.Id, CancellationToken.None) is Done)
            .IsTrue();
        await TerraBaseTestServer.EventuallyAsync(async token => await GetAsync(later, tenant, "orders", "first", token) == HttpStatusCode.Unauthorized);
        await Assert.That(await follower.MetricAsync("terrabase_catalog_follower_lag")).IsEqualTo(0);

        // The follower took the primary's origin as its incarnation, so dashboard ETags match on every member.
        var incarnations = await Task.WhenAll(new[] { primary, follower }.Select(async server =>
            (await server.Services.GetRequiredService<IStorageEngine>().OpenSystemDatabaseAsync(CancellationToken.None)).Incarnation));
        await Assert.That(incarnations[1]).IsEqualTo(incarnations[0]);

        // A replicated catalog version this build does not read is refused, not misread, until the version is one it reads.
        var version = await ReadVersionAsync(primary);
        await WriteSystemAsync(primary.Services.GetRequiredService<IStorageEngine>(),
            async (transaction, token) => await transaction.PutAsync("catalog", "version", "\"99\""u8.ToArray(), token));
        await TerraBaseTestServer.EventuallyAsync(async token => await GetAsync(data, tenant, "orders", "first", token) == HttpStatusCode.ServiceUnavailable);
        await WriteSystemAsync(primary.Services.GetRequiredService<IStorageEngine>(),
            async (transaction, token) => await transaction.PutAsync("catalog", "version", version, token));
        await TerraBaseTestServer.EventuallyAsync(async token => await GetAsync(data, tenant, "orders", "first", token) == HttpStatusCode.OK);
    }

    private static async Task<byte[]> ReadVersionAsync(TerraBaseTestServer server)
    {
        var store = await server.Services.GetRequiredService<IStorageEngine>().OpenSystemDatabaseAsync(CancellationToken.None);
        await using var transaction = await store.BeginTransactionAsync(write: false, CancellationToken.None);
        return (await transaction.GetAsync("catalog", "version", CancellationToken.None))!.Json.ToArray();
    }

    [Test]
    public async Task Members_configured_by_one_shared_peers_list_follow_the_named_primary_and_replicate_documents()
    {
        // The orchestrator's shape: every member gets the same peers and catalog-primary; only its node name differs.
        using var peers = new PeerTls();
        using var tls = new ClientTls();
        var (primary, follower) = await TerraBaseTestServer.StartPairAsync((settings, own, other) =>
        {
            peers.Apply(settings);
            tls.Apply(settings);
            settings["peer-port"] = own.ToString(System.Globalization.CultureInfo.InvariantCulture);
            var (a, b) = own < other ? (own, other) : (other, own);
            settings["peers"] = $"member-a=localhost:{a},member-b=localhost:{b}";
            settings["node-name"] = own == a ? "member-a" : "member-b";
            settings["catalog-primary"] = "member-a";
        });
        await using (primary)
        await using (follower)
        {
            var (catalog, replica) = primary.Services.GetRequiredService<TerraBase.Configuration.TerraBaseOptions>().CatalogPrimary is null
                ? (primary, follower)
                : (follower, primary);
            await Assert.That(replica.Services.GetRequiredService<TerraBase.Configuration.TerraBaseOptions>().CatalogPrimary).IsNotNull();
            var tenant = await CatalogTenant.CreateAsync(catalog, "orders");
            await ReadyAsync(replica);

            // A key and a document made on the primary are served by the other member.
            using var writer = tls.CreateClient(catalog.ClientAddress!, tenant.Key);
            await Assert.That(await PutAsync(writer, tenant, "orders", "shared")).IsEqualTo(HttpStatusCode.Created);
            using var reader = tls.CreateClient(replica.ClientAddress!, tenant.Key);
            await TerraBaseTestServer.EventuallyAsync(
                async token => await GetAsync(reader, tenant, "orders", "shared", token) == HttpStatusCode.OK, TimeSpan.FromSeconds(10));
        }
    }

    [Test]
    public async Task A_primary_on_a_new_volume_does_not_replace_the_catalog_its_followers_serve()
    {
        using var peers = new PeerTls();
        using var tls = new ClientTls();
        var port = TerraBaseTestServer.FreePort();
        Task<TerraBaseTestServer> StartPrimaryAsync()
        {
            return TerraBaseTestServer.StartAsync(settings =>
            {
                peers.Apply(settings);
                settings["peer-port"] = port.ToString(System.Globalization.CultureInfo.InvariantCulture);
            });
        }

        await using var follower = await TerraBaseTestServer.StartAsync(settings =>
        {
            peers.Apply(settings);
            tls.Apply(settings);
            settings["catalog-primary"] = $"https://localhost:{port}";
        });
        CatalogTenant tenant;
        await using (var primary = await StartPrimaryAsync())
        {
            tenant = await CatalogTenant.CreateAsync(primary, "orders");
            await ReadyAsync(follower);
            // Readiness can describe an earlier head; prove the tenant key arrived before removing its primary.
            using var seeded = tls.CreateClient(follower.ClientAddress!, tenant.Key);
            await TerraBaseTestServer.EventuallyAsync(async token =>
                await GetAsync(seeded, tenant, "orders", "kept", token) == HttpStatusCode.NotFound);
        }

        {
            // The same address now serves an empty catalog of another origin.
            await using var replacement = await StartPrimaryAsync();
            await TerraBaseTestServer.EventuallyAsync(async token =>
                await FailuresAsync(follower, "lineage", token) >= 1);
            await Assert.That(await ReadinessAsync(follower)).IsEqualTo(HttpStatusCode.OK);
            using var data = tls.CreateClient(follower.ClientAddress!, tenant.Key);
            await Assert.That(await PutAsync(data, tenant, "orders", "kept")).IsEqualTo(HttpStatusCode.Created);
        }
    }

    [Test]
    public async Task A_follower_waits_for_its_primary_catches_up_after_downtime_and_serves_while_the_primary_restarts()
    {
        using var peers = new PeerTls();
        using var tls = new ClientTls();
        var primaryDirectory = Directory.CreateTempSubdirectory("terrabase-catalog-primary-").FullName;
        var followerDirectory = Directory.CreateTempSubdirectory("terrabase-catalog-follower-").FullName;
        var port = TerraBaseTestServer.FreePort();
        TerraBaseTestServer? primary = null;
        TerraBaseTestServer? follower = null;
        Task<TerraBaseTestServer> StartPrimaryAsync()
        {
            return TerraBaseTestServer.StartAsync(settings =>
            {
                peers.Apply(settings);
                settings["peer-port"] = port.ToString(System.Globalization.CultureInfo.InvariantCulture);
                settings["data-directory"] = primaryDirectory;
            });
        }

        Task<TerraBaseTestServer> StartFollowerAsync()
        {
            return TerraBaseTestServer.StartAsync(settings =>
            {
                peers.Apply(settings);
                tls.Apply(settings);
                settings["data-directory"] = followerDirectory;
                settings["catalog-primary"] = $"https://localhost:{port}";
            });
        }

        try
        {
            // Without its primary, a new follower is not ready.
            follower = await StartFollowerAsync();
            await TerraBaseTestServer.EventuallyAsync(async token =>
                await FailuresAsync(follower, "stream", token) >= 1);
            await Assert.That(await ReadinessAsync(follower)).IsEqualTo(HttpStatusCode.ServiceUnavailable);

            primary = await StartPrimaryAsync();
            var first = await CatalogTenant.CreateAsync(primary, "orders");
            await ReadyAsync(follower);

            // Catch-up after downtime: what the primary committed meanwhile serves once the follower is ready again.
            await follower.DisposeAsync();
            follower = null;
            var second = await CatalogTenant.CreateAsync(primary, "invoices");
            follower = await StartFollowerAsync();
            await ReadyAsync(follower);
            using (var data = tls.CreateClient(follower.ClientAddress!, second.Key))
            {
                await Assert.That(await PutAsync(data, second, "invoices", "after-downtime")).IsEqualTo(HttpStatusCode.Created);
            }

            // The primary restarts while the follower serves both tenants and stays ready.
            await primary.DisposeAsync();
            primary = null;
            await Assert.That(await ReadinessAsync(follower)).IsEqualTo(HttpStatusCode.OK);
            foreach (var (tenant, database) in new[] { (first, "orders"), (second, "invoices") })
            {
                using var data = tls.CreateClient(follower.ClientAddress!, tenant.Key);
                await Assert.That(await PutAsync(data, tenant, database, "primary-down")).IsEqualTo(HttpStatusCode.Created);
                await Assert.That(await GetAsync(data, tenant, database, "primary-down")).IsEqualTo(HttpStatusCode.OK);
            }

            primary = await StartPrimaryAsync();
            var third = await CatalogTenant.CreateAsync(primary, "events");
            using var resumed = tls.CreateClient(follower.ClientAddress!, third.Key);
            await TerraBaseTestServer.EventuallyAsync(async token => await PutAsync(resumed, third, "events", "resumed", token) == HttpStatusCode.Created);
        }
        finally
        {
            if (follower is not null)
            {
                await follower.DisposeAsync();
            }

            if (primary is not null)
            {
                await primary.DisposeAsync();
            }

            Directory.Delete(followerDirectory, recursive: true);
            Directory.Delete(primaryDirectory, recursive: true);
        }
    }

    [Test]
    public async Task A_member_with_a_catalog_of_its_own_keeps_it_and_does_not_follow()
    {
        using var peers = new PeerTls();
        var directory = Directory.CreateTempSubdirectory("terrabase-catalog-own-").FullName;
        try
        {
            CatalogTenant own;
            await using (var standalone = await TerraBaseTestServer.StartAsync(settings => settings["data-directory"] = directory))
            {
                own = await CatalogTenant.CreateAsync(standalone, "orders");
            }

            await using var primary = await TerraBaseTestServer.StartAsync(peers.Apply);
            await using var member = await TerraBaseTestServer.StartAsync(settings =>
            {
                peers.Apply(settings);
                settings["data-directory"] = directory;
                settings["catalog-primary"] = primary.PeerAddress!.ToString();
            });
            await TerraBaseTestServer.EventuallyAsync(async token =>
                await FailuresAsync(member, "lineage", token) >= 1);
            await Assert.That(await ReadinessAsync(member)).IsEqualTo(HttpStatusCode.ServiceUnavailable);
            await Assert.That(await member.Services.GetRequiredService<ITenantDirectory>()
                .FindDatabaseAsync(own.Organization, own.Project, "orders", CancellationToken.None)).IsNotNull();
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Test]
    [Arguments("digest")]
    [Arguments("gap")]
    [Arguments("envelope")]
    [Arguments("count")]
    public async Task A_follower_applies_nothing_of_a_tampered_group(string tamper)
    {
        using var peers = new PeerTls();
        var origin = System.Security.Cryptography.RandomNumberGenerator.GetBytes(16);
        var initial = JournalDigest.Initial(origin);
        var honest = new DocumentChange("catalog_test", "honest", 7, Encoding.UTF8.GetBytes("""{"ok":true}"""));
        var honestDigest = JournalDigest.Compute(origin, 1, initial, [honest]);
        var forged = new DocumentChange("catalog_test", "forged", 8, Encoding.UTF8.GetBytes("\"forged\""));
        await using var fake = await FakePeer.StartCatalogAsync(peers, async (request, responses, context) =>
        {
            await responses.WriteAsync(new FollowResponse { Welcome = new CatalogWelcome { Protocol = 1, Origin = ByteString.CopyFrom(origin), Head = 2 } });
            // A reconnecting follower, which adopted this origin, resumes after the honest group as from a real primary.
            if (!request.Origin.Span.SequenceEqual(origin))
            {
                await responses.WriteAsync(new FollowResponse { SnapshotBegin = new SnapshotBegin { Sequence = 0, Digest = ByteString.CopyFrom(initial) } });
                await responses.WriteAsync(new FollowResponse { SnapshotEnd = new SnapshotEnd { Sequence = 0, Digest = ByteString.CopyFrom(initial) } });
                await WriteGroupAsync(responses, 1, [honest], honestDigest);
            }

            var digest = JournalDigest.Compute(origin, 2, honestDigest, [forged]);
            switch (tamper)
            {
                case "digest":
                    digest[0] ^= 1;
                    await WriteGroupAsync(responses, 2, [forged], digest);
                    break;
                case "gap":
                    await WriteGroupAsync(responses, 3, [forged], JournalDigest.Compute(origin, 3, honestDigest, [forged]));
                    break;
                case "envelope":
                    var page = CatalogPage([forged]);
                    var bytes = page.Envelope.ToByteArray();
                    bytes[^1] ^= 1;
                    await responses.WriteAsync(new FollowResponse { GroupBegin = new GroupBegin { Sequence = 2, ChangeCount = 1 } });
                    await responses.WriteAsync(new FollowResponse { Changes = new CatalogPage { Codec = page.Codec, Envelope = ByteString.CopyFrom(bytes) } });
                    await responses.WriteAsync(new FollowResponse { GroupEnd = new GroupEnd { Digest = ByteString.CopyFrom(digest) } });
                    break;
                default:
                    await responses.WriteAsync(new FollowResponse { GroupBegin = new GroupBegin { Sequence = 2, ChangeCount = 1 } });
                    await responses.WriteAsync(new FollowResponse { Changes = CatalogPage([forged, forged with { Id = "second" }]) });
                    await responses.WriteAsync(new FollowResponse { GroupEnd = new GroupEnd { Digest = ByteString.CopyFrom(digest) } });
                    break;
            }

            await responses.WriteAsync(new FollowResponse { Heartbeat = new CatalogHeartbeat { Head = 2 } });
            await Task.Delay(Timeout.Infinite, context.CancellationToken);
        });
        await using var follower = await TerraBaseTestServer.StartAsync(settings =>
        {
            peers.Apply(settings);
            settings["catalog-primary"] = fake.Address.ToString();
        });

        await TerraBaseTestServer.EventuallyAsync(async token =>
            await FailuresAsync(follower, "invalid", token) >= 1);
        await Assert.That(await ReadinessAsync(follower)).IsEqualTo(HttpStatusCode.ServiceUnavailable);
        var replication = follower.Services.GetRequiredService<ISystemReplication>();
        var head = await replication.GetHeadAsync(CancellationToken.None);
        await Assert.That(head.Origin.ToArray()).IsEquivalentTo(origin);
        await Assert.That(head.Sequence).IsEqualTo(1);
        await Assert.That(head.Digest.ToArray()).IsEquivalentTo(honestDigest);
        var documents = await replication.ReadDocumentsAsync(null, null, 10, 1024 * 1024, CancellationToken.None);
        await Assert.That(documents.Documents.Select(document => document.Id)).IsEquivalentTo(["honest"]);
    }

    [Test]
    public async Task A_follower_that_falls_behind_its_primary_is_not_ready_until_it_applies_the_primary_head()
    {
        using var peers = new PeerTls();
        var origin = System.Security.Cryptography.RandomNumberGenerator.GetBytes(16);
        var initial = JournalDigest.Initial(origin);
        var first = new DocumentChange("catalog_test", "first", 3, Encoding.UTF8.GetBytes("1"));
        var firstDigest = JournalDigest.Compute(origin, 1, initial, [first]);
        var second = new DocumentChange("catalog_test", "second", 4, Encoding.UTF8.GetBytes("2"));
        var reconnect = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var deliver = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var fake = await FakePeer.StartCatalogAsync(peers, async (request, responses, context) =>
        {
            if (!request.Origin.Span.SequenceEqual(origin))
            {
                // First stream: a snapshot through group 1, the head, then the stream breaks once the test saw it ready.
                await responses.WriteAsync(new FollowResponse { Welcome = new CatalogWelcome { Protocol = 1, Origin = ByteString.CopyFrom(origin), Head = 1 } });
                await responses.WriteAsync(new FollowResponse { SnapshotBegin = new SnapshotBegin { Sequence = 0, Digest = ByteString.CopyFrom(initial) } });
                await WriteGroupAsync(responses, 1, [first], firstDigest);
                await responses.WriteAsync(new FollowResponse { SnapshotEnd = new SnapshotEnd { Sequence = 1, Digest = ByteString.CopyFrom(firstDigest), CommitOffset = 3 } });
                await responses.WriteAsync(new FollowResponse { Heartbeat = new CatalogHeartbeat { Head = 1 } });
                await reconnect.Task.WaitAsync(context.CancellationToken);
                return;
            }

            // The reconnected follower learns of group 2 before receiving it.
            await responses.WriteAsync(new FollowResponse { Welcome = new CatalogWelcome { Protocol = 1, Origin = ByteString.CopyFrom(origin), Head = 2 } });
            await deliver.Task.WaitAsync(context.CancellationToken);
            await WriteGroupAsync(responses, 2, [second], JournalDigest.Compute(origin, 2, firstDigest, [second]));
            await responses.WriteAsync(new FollowResponse { Heartbeat = new CatalogHeartbeat { Head = 2 } });
            await Task.Delay(Timeout.Infinite, context.CancellationToken);
        });
        await using var follower = await TerraBaseTestServer.StartAsync(settings =>
        {
            peers.Apply(settings);
            settings["catalog-primary"] = fake.Address.ToString();
        });

        await ReadyAsync(follower);
        reconnect.SetResult();
        await TerraBaseTestServer.EventuallyAsync(async token => await follower.MetricAsync("terrabase_catalog_follower_lag", "", token) == 1);
        await Assert.That(await ReadinessAsync(follower)).IsEqualTo(HttpStatusCode.ServiceUnavailable);
        deliver.SetResult();
        await ReadyAsync(follower);
        await Assert.That(await CountAsync(follower, "catalog_test")).IsEqualTo(2);
    }

    [Test]
    public async Task The_primary_resumes_only_a_frontier_of_its_own_journal()
    {
        using var peers = new PeerTls();
        await using var primary = await TerraBaseTestServer.StartAsync(peers.Apply);
        await CatalogTenant.CreateAsync(primary, "orders");
        using var channel = peers.CreateChannel(primary.PeerAddress!);
        var client = new CatalogReplication.CatalogReplicationClient(channel);

        var refused = await Assert.ThrowsAsync<RpcException>(async () =>
            await FramesAsync(client, new FollowRequest { Protocol = 2, Codecs = { PayloadCodec.Lz4BlockV1 } }, 1));
        await Assert.That(refused!.StatusCode).IsEqualTo(StatusCode.InvalidArgument);

        // A new follower gets a snapshot that ends at the primary's head.
        var snapshot = await FramesAsync(client, new FollowRequest { Protocol = 1, Codecs = { PayloadCodec.Lz4BlockV1 } },
            stop: frame => frame.FrameCase == FollowResponse.FrameOneofCase.Heartbeat);
        var welcome = snapshot[0].Welcome;
        var end = snapshot.Single(frame => frame.FrameCase == FollowResponse.FrameOneofCase.SnapshotEnd).SnapshotEnd;
        await Assert.That(snapshot[1].FrameCase).IsEqualTo(FollowResponse.FrameOneofCase.SnapshotBegin);
        await Assert.That(end.Sequence).IsEqualTo(welcome.Head);
        await Assert.That(end.CommitOffset).IsGreaterThan(0UL);
        await Assert.That(snapshot.Where(frame => frame.FrameCase == FollowResponse.FrameOneofCase.Documents)
            .SelectMany(frame => CatalogProtocol.Read(frame.Documents)).Select(document => document.Collection)).Contains("organizations");

        // Its frontier resumes with no snapshot; the same position with another digest is not its journal.
        var resumed = await FramesAsync(client, Frontier(welcome.Origin, end.Sequence, end.Digest), stop: _ => true, count: 2);
        await Assert.That(resumed[1].FrameCase).IsEqualTo(FollowResponse.FrameOneofCase.Heartbeat);
        var forged = end.Digest.ToByteArray();
        forged[0] ^= 1;
        var reset = await FramesAsync(client, Frontier(welcome.Origin, end.Sequence, ByteString.CopyFrom(forged)), stop: _ => true, count: 2);
        await Assert.That(reset[1].FrameCase).IsEqualTo(FollowResponse.FrameOneofCase.SnapshotBegin);

        // A follower is no primary.
        await using var follower = await TerraBaseTestServer.StartAsync(settings =>
        {
            peers.Apply(settings);
            settings["catalog-primary"] = primary.PeerAddress!.ToString();
        });
        using var followerChannel = peers.CreateChannel(follower.PeerAddress!);
        var notPrimary = await Assert.ThrowsAsync<RpcException>(async () =>
            await FramesAsync(new CatalogReplication.CatalogReplicationClient(followerChannel), new FollowRequest { Protocol = 1, Codecs = { PayloadCodec.Lz4BlockV1 } }, 1));
        await Assert.That(notPrimary!.StatusCode).IsEqualTo(StatusCode.FailedPrecondition);
    }

    [Test]
    public async Task Catalog_transactions_and_snapshots_larger_than_a_page_replicate_whole()
    {
        using var peers = new PeerTls();
        await using var primary = await TerraBaseTestServer.StartAsync(peers.Apply);
        var engine = primary.Services.GetRequiredService<IStorageEngine>();
        await WriteSystemAsync(engine, async (transaction, token) =>
        {
            for (var i = 0; i < 5000; i++)
            {
                await transaction.PutAsync("paging/a", $"{i:D5}", Encoding.UTF8.GetBytes($$"""{"n":{{i}}}"""), token);
            }
        });

        // The snapshot spans pages; so do the next groups: 5000 deletions of a prefix drop and 5000 puts.
        await using var follower = await TerraBaseTestServer.StartAsync(settings =>
        {
            peers.Apply(settings);
            settings["catalog-primary"] = primary.PeerAddress!.ToString();
        });
        await ReadyAsync(follower);
        await Assert.That(await CountAsync(follower, "paging/a")).IsEqualTo(5000);
        await WriteSystemAsync(engine, async (transaction, token) => await transaction.DeleteCollectionsAsync("paging/a", token));
        await WriteSystemAsync(engine, async (transaction, token) =>
        {
            for (var i = 0; i < 5000; i++)
            {
                await transaction.PutAsync("paging/b", $"{i:D5}", Encoding.UTF8.GetBytes("\"scalar\""), token);
            }
        });
        await TerraBaseTestServer.EventuallyAsync(async token => await CountAsync(follower, "paging/b", token) == 5000);
        await Assert.That(await CountAsync(follower, "paging/a")).IsEqualTo(0);
    }

    // Catalog streams of the follower that failed for reason; a counter without a sample yet reads as 0.
    private static async Task<double> FailuresAsync(TerraBaseTestServer follower, string reason, CancellationToken cancellationToken)
    {
        try
        {
            return await follower.MetricAsync("terrabase_catalog_follower_failures_total", $"reason=\"{reason}\"", cancellationToken);
        }
        catch (InvalidOperationException) when (!cancellationToken.IsCancellationRequested)
        {
            return 0;
        }
    }

    [Test]
    public async Task A_follower_behind_the_retained_journal_tail_catches_up_with_a_snapshot()
    {
        using var peers = new PeerTls();
        var directory = Directory.CreateTempSubdirectory("terrabase-catalog-tail-").FullName;
        try
        {
            await using var primary = await TerraBaseTestServer.StartAsync(peers.Apply);
            Task<TerraBaseTestServer> StartFollowerAsync()
            {
                return TerraBaseTestServer.StartAsync(settings =>
                {
                    peers.Apply(settings);
                    settings["data-directory"] = directory;
                    settings["catalog-primary"] = primary.PeerAddress!.ToString();
                });
            }

            var engine = primary.Services.GetRequiredService<IStorageEngine>();
            await WriteSystemAsync(engine, async (transaction, token) => await transaction.PutAsync("tail/a", "0", "0"u8.ToArray(), token));
            await using (var follower = await StartFollowerAsync())
            {
                await ReadyAsync(follower);
            }

            // While the follower is down, more groups commit than the primary retains.
            for (var i = 1; i <= ISystemReplication.RetainedGroups + 10; i++)
            {
                var id = i.ToString(System.Globalization.CultureInfo.InvariantCulture);
                await WriteSystemAsync(engine, async (transaction, token) => await transaction.PutAsync("tail/a", id, Encoding.UTF8.GetBytes(id), token));
            }

            var replication = primary.Services.GetRequiredService<ISystemReplication>();
            var head = await replication.GetHeadAsync(CancellationToken.None);
            await Assert.That((await replication.ReadGroupAsync(head.Sequence - ISystemReplication.RetainedGroups, 0, 1, 1, CancellationToken.None)).Count)
                .IsEqualTo(0);
            await Assert.That((await replication.ReadGroupAsync(head.Sequence, 0, 1, 1, CancellationToken.None)).Count).IsEqualTo(1);

            await using (var follower = await StartFollowerAsync())
            {
                await ReadyAsync(follower);
                await Assert.That(await CountAsync(follower, "tail/a")).IsEqualTo(ISystemReplication.RetainedGroups + 11);
                await Assert.That((await follower.Services.GetRequiredService<ISystemReplication>().GetHeadAsync(CancellationToken.None)).Sequence)
                    .IsGreaterThanOrEqualTo(head.Sequence);
            }
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    private static async Task ReadyAsync(TerraBaseTestServer server) =>
        await TerraBaseTestServer.EventuallyAsync(async token => await ReadinessAsync(server, token) == HttpStatusCode.OK);

    private static async Task<HttpStatusCode> ReadinessAsync(TerraBaseTestServer server, CancellationToken cancellationToken = default)
    {
        using var response = await server.Client.GetAsync(new Uri("/readyz", UriKind.Relative), cancellationToken);
        return response.StatusCode;
    }

    private static async Task NotPrimaryAsync(HttpResponseMessage response)
    {
        await Assert.That(response.StatusCode).IsEqualTo(HttpStatusCode.ServiceUnavailable);
        await Assert.That(response.Headers.RetryAfter?.Delta).IsEqualTo(TimeSpan.FromSeconds(1));
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        await Assert.That(body.GetProperty("error").GetProperty("code").GetString()).IsEqualTo("not_primary");
    }

    private static async Task<HttpStatusCode> PutAsync(HttpClient data, CatalogTenant tenant, string database, string label, CancellationToken cancellationToken = default)
    {
        using var collection = await data.PostAsJsonAsync(tenant.Database(database, "/collections"), new { name = "items" }, cancellationToken);
        if (collection.StatusCode is not (HttpStatusCode.Created or HttpStatusCode.Conflict))
        {
            return collection.StatusCode;
        }

        using var put = await data.PutAsJsonAsync(tenant.Document(database, label), new { label }, cancellationToken);
        return put.StatusCode;
    }

    private static async Task<HttpStatusCode> GetAsync(HttpClient data, CatalogTenant tenant, string database, string label, CancellationToken cancellationToken = default)
    {
        using var response = await data.GetAsync(tenant.Document(database, label), cancellationToken);
        return response.StatusCode;
    }

    private static async Task WriteSystemAsync(IStorageEngine engine, Func<IStorageTransaction, CancellationToken, Task> write)
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(TUnit.Core.TestContext.Current!.Execution.CancellationToken);
        var store = await engine.OpenSystemDatabaseAsync(deadline.Token);
        await using var transaction = await store.BeginTransactionAsync(write: true, deadline.Token);
        await write(transaction, deadline.Token);
        await transaction.CommitAsync(deadline.Token);
    }

    private static async Task<long> CountAsync(TerraBaseTestServer server, string collection, CancellationToken cancellationToken = default)
    {
        var store = await server.Services.GetRequiredService<IStorageEngine>().OpenSystemDatabaseAsync(cancellationToken);
        await using var transaction = await store.BeginTransactionAsync(write: false, cancellationToken);
        return await transaction.CountDocumentsAsync(collection, cancellationToken);
    }

    private static CatalogPage CatalogPage(IReadOnlyList<DocumentChange> changes) => CatalogProtocol.Page(changes);

    private static async Task WriteGroupAsync(IServerStreamWriter<FollowResponse> responses, ulong sequence, IReadOnlyList<DocumentChange> changes, byte[] digest)
    {
        await responses.WriteAsync(new FollowResponse { GroupBegin = new GroupBegin { Sequence = sequence, ChangeCount = (uint)changes.Count } });
        await responses.WriteAsync(new FollowResponse { Changes = CatalogPage(changes) });
        await responses.WriteAsync(new FollowResponse { GroupEnd = new GroupEnd { Digest = ByteString.CopyFrom(digest) } });
    }

    private static FollowRequest Frontier(ByteString origin, ulong sequence, ByteString digest) =>
        new() { Protocol = 1, Codecs = { PayloadCodec.Lz4BlockV1 }, Origin = origin, Sequence = sequence, Digest = digest };

    // Reads frames until count of them arrived or stop holds for one after the first (always Welcome), within 30 seconds, then
    // ends the stream.
    private static async Task<List<FollowResponse>> FramesAsync(
        CatalogReplication.CatalogReplicationClient client, FollowRequest request, int count = int.MaxValue, Func<FollowResponse, bool>? stop = null)
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(TUnit.Core.TestContext.Current!.Execution.CancellationToken);
        using var call = client.Follow(request, cancellationToken: deadline.Token);
        var frames = new List<FollowResponse>();
        while (frames.Count < count && await call.ResponseStream.MoveNext(deadline.Token))
        {
            frames.Add(call.ResponseStream.Current);
            if (frames.Count > 1 && stop?.Invoke(call.ResponseStream.Current) == true)
            {
                break;
            }
        }

        return frames;
    }

    private sealed record CatalogTenant(string User, string Organization, string Project, string Key)
    {
        public Uri Database(string name, string path = "") => new($"/v1/projects/{Project}/databases/{name}{path}", UriKind.Relative);

        public Uri Document(string database, string label) => Database(database, $"/collections/items/documents/{ReplicationDocuments.Id(label)}");

        // An organization with one project and database on a catalog primary, and an API key of the organization.
        public static async Task<CatalogTenant> CreateAsync(TerraBaseTestServer primary, string database)
        {
            using var deadline = CancellationTokenSource.CreateLinkedTokenSource(TUnit.Core.TestContext.Current!.Execution.CancellationToken);
            var user = Guid.NewGuid().ToString();
            var management = primary.Services.GetRequiredService<IManagementService>();
            var organization = (await management.CreateOrganizationAsync(user, $"Org {Guid.NewGuid():N}", deadline.Token)).Value
                ?? throw new InvalidOperationException("the primary did not create the organization");
            var project = (await management.CreateProjectAsync(user, organization.Id, "main", deadline.Token)).Value
                ?? throw new InvalidOperationException("the primary did not create the project");
            _ = (await management.CreateDatabaseAsync(user, organization.Id, project.Id, database, deadline.Token)).Value
                ?? throw new InvalidOperationException("the primary did not create the database");
            return new CatalogTenant(user, organization.Id, project.Id, await primary.IssueAsync(organization.Id, Scopes));
        }
    }
}
