using System.Net;
using System.Security.Cryptography;
using System.Text.Json;
using Google.Protobuf;
using Microsoft.Extensions.DependencyInjection;
using TerraBase.Engine;
using TerraBase.Features.ApiKeys;
using TerraBase.Features.Management;
using TerraBase.Features.Replication;
using TerraBase.Features.Replication.V1;
using TerraBase.Hosting;
using TerraBase.Primitives;
using TerraBase.Tenancy;
using TerraBase.Testing;

namespace TerraBase.Fast.Replication;

/// <summary>Catalog followers delete what their primary deletes, and replication brings none of it back (docs/decisions/0009).</summary>
public sealed class CatalogDeletionTests
{
    private static readonly string[] Scopes = [ApiKeyScopes.DocumentsRead, ApiKeyScopes.DocumentsWrite];

    [Test]
    public async Task A_database_deleted_on_the_primary_goes_from_its_followers_even_one_that_was_down_and_is_not_received_again()
    {
        using var peers = new PeerTls();
        using var tls = new ClientTls();
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(TUnit.Core.TestContext.Current!.Execution.CancellationToken);
        var token = deadline.Token;
        var primaryDirectory = Directory.CreateTempSubdirectory("terrabase-delete-primary-").FullName;
        var followerDirectory = Directory.CreateTempSubdirectory("terrabase-delete-follower-").FullName;
        TerraBaseTestServer? primary = null;
        TerraBaseTestServer? follower = null;
        // A Sync stream for the database named here stops between its registration check and its open, until released.
        string? paused = null;
        using var reached = new SemaphoreSlim(0);
        using var release = new SemaphoreSlim(0);
        void Pause(string step, TenantContext database)
        {
            if (step == "replication.registered" && database.DatabaseId == Volatile.Read(ref paused))
            {
                reached.Release();
                _ = release.Wait(TimeSpan.FromSeconds(30));
            }
        }

        Task<TerraBaseTestServer> StartFollowerAsync()
        {
            return TerraBaseTestServer.StartAsync(settings =>
            {
                peers.Apply(settings);
                tls.Apply(settings);
                settings["data-directory"] = followerDirectory;
                settings["catalog-primary"] = primary!.PeerAddress!.ToString();
            }, services => services.AddSingleton<DatabaseLifecycleCheckpoint>(Pause));
        }

        try
        {
            primary = await TerraBaseTestServer.StartAsync(settings =>
            {
                peers.Apply(settings);
                settings["data-directory"] = primaryDirectory;
            });
            var user = EntityIds.New().ToString();
            var management = primary.Services.GetRequiredService<IManagementService>();
            var organization = (await management.CreateOrganizationAsync(user, "Deletes", token)).Value!;
            var project = (await management.CreateProjectAsync(user, organization.Id, "main", token)).Value!;
            var directory = primary.Services.GetRequiredService<ITenantDirectory>();
            var orders = (await directory.CreateDatabaseAsync(organization.Id, project.Id, "orders", token))!.Value.Database;
            var invoices = (await directory.CreateDatabaseAsync(organization.Id, project.Id, "invoices", token))!.Value.Database;
            var key = await primary.IssueAsync(organization.Id, Scopes);
            string FileOf(TenantContext database)
            {
                return Path.Combine(followerDirectory, organization.Id, project.Id, database.DatabaseId + ".db");
            }


            follower = await StartFollowerAsync();
            await ReadyAsync(follower);
            using (var data = tls.CreateClient(follower.ClientAddress!, key))
            {
                foreach (var name in new[] { "orders", "invoices" })
                {
                    using var collection = await data.PostAsJsonAsync(new Uri($"/v1/projects/{project.Id}/databases/{name}/collections", UriKind.Relative), new { name = "items" }, token);
                    await Assert.That(collection.StatusCode).IsEqualTo(HttpStatusCode.Created);
                }
            }

            await Assert.That(File.Exists(FileOf(orders)) && File.Exists(FileOf(invoices))).IsTrue();

            // Deleted while the follower follows, and while a peer's Sync stream for it passed the registration check but has not
            // opened it: the follower's file goes, the stream's reopened file goes too, and nothing brings it back.
            using (var channel = peers.CreateChannel(follower.PeerAddress!))
            {
                var client = new Features.Replication.V1.Replication.ReplicationClient(channel);
                Volatile.Write(ref paused, orders.DatabaseId);
                var racing = HelloAsync(client, orders, token);
                await reached.WaitAsync(token);
                await Assert.That(await management.DeleteDatabaseAsync(user, organization.Id, project.Id, orders.DatabaseId!, token)).IsEqualTo(ManagementError.None);
                await TerraBaseTestServer.EventuallyAsync(_ => Task.FromResult(!File.Exists(FileOf(orders))));
                release.Release();
                var raced = await racing;
                await Assert.That(raced.Error?.Code).IsEqualTo(StreamErrorCode.StreamErrorUnauthorized);
                await Assert.That(File.Exists(FileOf(orders))).IsFalse();

                var refused = await HelloAsync(client, orders, token);
                await Assert.That(refused.Error?.Code).IsEqualTo(StreamErrorCode.StreamErrorUnauthorized);
                await Assert.That(refused.Error!.Retryable).IsTrue();
                await Assert.That(File.Exists(FileOf(orders))).IsFalse();
                await Assert.That((await HelloAsync(client, invoices, token)).Welcome).IsNotNull();

                // A catalog at a version this build does not read refuses streams, and deletes nothing on its account.
                var engine = primary.Services.GetRequiredService<IStorageEngine>();
                await WriteVersionAsync(engine, "\"99\""u8.ToArray(), token);
                await TerraBaseTestServer.EventuallyAsync(async probe => (await HelloAsync(client, invoices, probe)).Error is not null);
                await Assert.That(File.Exists(FileOf(invoices))).IsTrue();
                await WriteVersionAsync(engine, "\"5\""u8.ToArray(), token);
                await TerraBaseTestServer.EventuallyAsync(async probe => (await HelloAsync(client, invoices, probe)).Welcome is not null);
            }

            using (var data = tls.CreateClient(follower.ClientAddress!, key))
            {
                using var gone = await data.GetAsync(new Uri($"/v1/projects/{project.Id}/databases/orders/collections", UriKind.Relative), token);
                await Assert.That(gone.StatusCode).IsEqualTo(HttpStatusCode.NotFound);
            }

            await Assert.That(File.Exists(FileOf(orders))).IsFalse();

            // Deleted while the follower is down: its file goes once it caught up; a file of no database is kept and counted.
            await follower.DisposeAsync();
            follower = null;
            await Assert.That(await management.DeleteDatabaseAsync(user, organization.Id, project.Id, invoices.DatabaseId!, token)).IsEqualTo(ManagementError.None);
            var unknown = Path.Combine(followerDirectory, organization.Id, project.Id, EntityIds.New() + ".db");
            File.Copy(FileOf(invoices), unknown);
            follower = await StartFollowerAsync();
            await ReadyAsync(follower);
            await Assert.That(File.Exists(FileOf(invoices))).IsFalse();
            await TerraBaseTestServer.EventuallyAsync(async probe => await follower.MetricAsync("terrabase_storage_unknown_files", "", probe) == 1);
            await Assert.That(File.Exists(unknown)).IsTrue();
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
    public async Task A_follower_deletes_files_only_for_valid_cleanup_intents_of_databases_its_catalog_does_not_register()
    {
        using var peers = new PeerTls();
        var origin = RandomNumberGenerator.GetBytes(16);
        var initial = JournalDigest.Initial(origin);
        var (organization, project) = (EntityIds.New().ToString(), EntityIds.New().ToString());
        TenantContext Database()
        {
            return new(organization, PlanTier.Hobby, project, EntityIds.New().ToString());
        }

        var (deleted, tampered, registered) = (Database(), Database(), Database());
        var send = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        // One group: an intent whose id names another database, the intents of a deleted database and of a registered one (a
        // creation that completed).
        DocumentChange Intent(string id, TenantContext database)
        {
            return new("pending_database_cleanup", id, 10, JsonSerializer.SerializeToUtf8Bytes(database, TerraBaseJsonContext.Default.TenantContext));
        }

        DocumentChange[] group =
        [
            Intent(EntityIds.New().ToString(), tampered),
            Intent(deleted.DatabaseId!, deleted with { OrganizationId = "" }),
            Intent(deleted.DatabaseId!.ToUpperInvariant(), deleted with { DatabaseId = deleted.DatabaseId!.ToUpperInvariant() }),
            Intent(deleted.DatabaseId!, deleted),
            Intent(registered.DatabaseId!, registered),
            new($"projects/{project}/databases", registered.DatabaseId!, 11, JsonSerializer.SerializeToUtf8Bytes(
                new ManagedDatabase(registered.DatabaseId!, project, "kept", null, DateTimeOffset.UnixEpoch), TerraBaseJsonContext.Default.ManagedDatabase)),
        ];
        await using var fake = await FakePeer.StartCatalogAsync(peers, async (_, responses, context) =>
        {
            await responses.WriteAsync(new FollowResponse { Welcome = new CatalogWelcome { Protocol = 1, Origin = ByteString.CopyFrom(origin), Head = 0 } });
            await responses.WriteAsync(new FollowResponse { SnapshotBegin = new SnapshotBegin { Sequence = 0, Digest = ByteString.CopyFrom(initial) } });
            await responses.WriteAsync(new FollowResponse { Documents = CatalogProtocol.Page([Version]) });
            await responses.WriteAsync(new FollowResponse { SnapshotEnd = new SnapshotEnd { Sequence = 0, Digest = ByteString.CopyFrom(initial), CommitOffset = 1 } });
            await responses.WriteAsync(new FollowResponse { Heartbeat = new CatalogHeartbeat { Head = 0 } });
            await send.Task.WaitAsync(context.CancellationToken);
            await responses.WriteAsync(new FollowResponse { GroupBegin = new GroupBegin { Sequence = 1, ChangeCount = (uint)group.Length } });
            await responses.WriteAsync(new FollowResponse { Changes = CatalogProtocol.Page(group) });
            await responses.WriteAsync(new FollowResponse { GroupEnd = new GroupEnd { Digest = ByteString.CopyFrom(JournalDigest.Compute(origin, 1, initial, group)) } });
            await responses.WriteAsync(new FollowResponse { Heartbeat = new CatalogHeartbeat { Head = 1 } });
            await Task.Delay(Timeout.Infinite, context.CancellationToken);
        });
        await using var follower = await TerraBaseTestServer.StartAsync(settings =>
        {
            peers.Apply(settings);
            settings["catalog-primary"] = fake.Address.ToString();
        });
        await ReadyAsync(follower);
        var engine = follower.Services.GetRequiredService<IStorageEngine>();
        string FileOf(TenantContext database)
        {
            return Path.Combine(follower.DataDirectory, organization, project, database.DatabaseId + ".db");
        }

        foreach (var database in new[] { deleted, tampered, registered })
        {
            await Assert.That(await engine.CreateDatabaseAsync(database, CancellationToken.None)).IsTrue();
            await Assert.That(File.Exists(FileOf(database))).IsTrue();
        }

        send.SetResult();
        await TerraBaseTestServer.EventuallyAsync(async token => await follower.MetricAsync("terrabase_catalog_applied_sequence", "", token) == 1);
        await Assert.That(File.Exists(FileOf(deleted))).IsFalse();
        await Assert.That(File.Exists(FileOf(tampered))).IsTrue();
        await Assert.That(File.Exists(FileOf(registered))).IsTrue();

        // Invalid intents (another database's id, no organization, ids the engine does not take) are skipped, never retried.
        await Assert.That(await ApplyFailuresAsync(follower)).IsEqualTo(0);
    }

    [Test]
    public async Task A_follower_whose_cleanup_fails_applies_nothing_more_until_it_succeeds()
    {
        using var peers = new PeerTls();
        var origin = RandomNumberGenerator.GetBytes(16);
        var initial = JournalDigest.Initial(origin);
        var deleted = new TenantContext(EntityIds.New().ToString(), PlanTier.Hobby, EntityIds.New().ToString(), EntityIds.New().ToString());
        DocumentChange[] group = [new("pending_database_cleanup", deleted.DatabaseId!, 10, JsonSerializer.SerializeToUtf8Bytes(deleted, TerraBaseJsonContext.Default.TenantContext))];
        var send = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var streams = 0;
        await using var fake = await FakePeer.StartCatalogAsync(peers, async (_, responses, context) =>
        {
            if (Interlocked.Increment(ref streams) == 1)
            {
                // The first stream brings the intent; its cleanup fails, which ends the stream.
                await responses.WriteAsync(new FollowResponse { Welcome = new CatalogWelcome { Protocol = 1, Origin = ByteString.CopyFrom(origin), Head = 0 } });
                await responses.WriteAsync(new FollowResponse { SnapshotBegin = new SnapshotBegin { Sequence = 0, Digest = ByteString.CopyFrom(initial) } });
                await responses.WriteAsync(new FollowResponse { Documents = CatalogProtocol.Page([Version]) });
                await responses.WriteAsync(new FollowResponse { SnapshotEnd = new SnapshotEnd { Sequence = 0, Digest = ByteString.CopyFrom(initial), CommitOffset = 1 } });
                await responses.WriteAsync(new FollowResponse { Heartbeat = new CatalogHeartbeat { Head = 0 } });
                await send.Task.WaitAsync(context.CancellationToken);
                await responses.WriteAsync(new FollowResponse { GroupBegin = new GroupBegin { Sequence = 1, ChangeCount = 1 } });
                await responses.WriteAsync(new FollowResponse { Changes = CatalogProtocol.Page(group) });
                await responses.WriteAsync(new FollowResponse { GroupEnd = new GroupEnd { Digest = ByteString.CopyFrom(JournalDigest.Compute(origin, 1, initial, group)) } });
            }
            else
            {
                // Later streams bring nothing new: the follower acts on the intent its catalog still holds before it follows.
                await responses.WriteAsync(new FollowResponse { Welcome = new CatalogWelcome { Protocol = 1, Origin = ByteString.CopyFrom(origin), Head = 1 } });
                await responses.WriteAsync(new FollowResponse { Heartbeat = new CatalogHeartbeat { Head = 1 } });
            }

            await Task.Delay(Timeout.Infinite, context.CancellationToken);
        });

        // The first cleanup fails, as a disk error would.
        var failures = 0;
        void Fail(string step, TenantContext database)
        {
            if (step == "follower.cleanup" && database.DatabaseId == deleted.DatabaseId && Interlocked.Increment(ref failures) == 1)
            {
                throw new IOException("injected cleanup failure");
            }
        }

        await using var follower = await TerraBaseTestServer.StartAsync(settings =>
        {
            peers.Apply(settings);
            settings["catalog-primary"] = fake.Address.ToString();
        }, services => services.AddSingleton<DatabaseLifecycleCheckpoint>(Fail));
        await ReadyAsync(follower);
        await Assert.That(await follower.Services.GetRequiredService<IStorageEngine>().CreateDatabaseAsync(deleted, CancellationToken.None)).IsTrue();
        var file = Path.Combine(follower.DataDirectory, deleted.OrganizationId, deleted.ProjectId!, deleted.DatabaseId + ".db");

        send.SetResult();
        await TerraBaseTestServer.EventuallyAsync(_ => Task.FromResult(!File.Exists(file)));
        await Assert.That(Volatile.Read(ref failures)).IsEqualTo(2);
        await Assert.That(await follower.MetricAsync("terrabase_catalog_follower_failures_total", "reason=\"apply\"")).IsEqualTo(1);
        // The retry came before a new stream, which the follower opens once it succeeded.
        await TerraBaseTestServer.EventuallyAsync(_ => Task.FromResult(Volatile.Read(ref streams) >= 2));
    }

    [Test]
    public async Task A_restarted_follower_acts_on_the_cleanup_intents_its_catalog_holds_before_it_follows()
    {
        // A follower that stopped right after it applied an intent: the intent is in its catalog, the file still on disk.
        using var peers = new PeerTls();
        var origin = RandomNumberGenerator.GetBytes(16);
        var initial = JournalDigest.Initial(origin);
        var deleted = new TenantContext(EntityIds.New().ToString(), PlanTier.Hobby, EntityIds.New().ToString(), EntityIds.New().ToString());
        var intent = new DocumentChange("pending_database_cleanup", deleted.DatabaseId!, 10, JsonSerializer.SerializeToUtf8Bytes(deleted, TerraBaseJsonContext.Default.TenantContext));
        var streams = 0;
        await using var fake = await FakePeer.StartCatalogAsync(peers, async (_, responses, context) =>
        {
            // Only the first stream gets the catalog: the restarted follower acts on what its catalog holds, with no stream.
            if (Interlocked.Increment(ref streams) == 1)
            {
                await responses.WriteAsync(new FollowResponse { Welcome = new CatalogWelcome { Protocol = 1, Origin = ByteString.CopyFrom(origin), Head = 0 } });
                await responses.WriteAsync(new FollowResponse { SnapshotBegin = new SnapshotBegin { Sequence = 0, Digest = ByteString.CopyFrom(initial) } });
                await responses.WriteAsync(new FollowResponse { Documents = CatalogProtocol.Page([Version, intent]) });
                await responses.WriteAsync(new FollowResponse { SnapshotEnd = new SnapshotEnd { Sequence = 0, Digest = ByteString.CopyFrom(initial), CommitOffset = 10 } });
                await responses.WriteAsync(new FollowResponse { Heartbeat = new CatalogHeartbeat { Head = 0 } });
            }

            await Task.Delay(Timeout.Infinite, context.CancellationToken);
        });
        var directory = Directory.CreateTempSubdirectory("terrabase-delete-restart-").FullName;
        try
        {
            Task<TerraBaseTestServer> StartFollowerAsync()
            {
                return TerraBaseTestServer.StartAsync(settings =>
                {
                    peers.Apply(settings);
                    settings["data-directory"] = directory;
                    settings["catalog-primary"] = fake.Address.ToString();
                });
            }

            var file = Path.Combine(directory, deleted.OrganizationId, deleted.ProjectId!, deleted.DatabaseId + ".db");
            await using (var follower = await StartFollowerAsync())
            {
                await ReadyAsync(follower);
                await Assert.That(await follower.Services.GetRequiredService<IStorageEngine>().CreateDatabaseAsync(deleted, CancellationToken.None)).IsTrue();
            }

            await Assert.That(File.Exists(file)).IsTrue();
            await using var restarted = await StartFollowerAsync();
            await TerraBaseTestServer.EventuallyAsync(_ => Task.FromResult(!File.Exists(file)));
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    private static async Task WriteVersionAsync(IStorageEngine engine, byte[] version, CancellationToken cancellationToken)
    {
        var store = await engine.OpenSystemDatabaseAsync(cancellationToken);
        await using var transaction = await store.BeginTransactionAsync(write: true, cancellationToken);
        await transaction.PutAsync("catalog", "version", version, cancellationToken);
        await transaction.CommitAsync(cancellationToken);
    }

    [Test]
    public async Task Deleting_an_expected_incarnation_never_deletes_one_that_replaced_it()
    {
        await using var server = await TerraBaseTestServer.StartAsync();
        var engine = server.Services.GetRequiredService<IStorageEngine>();
        var database = new TenantContext(EntityIds.New().ToString(), PlanTier.Hobby, EntityIds.New().ToString(), EntityIds.New().ToString());
        var file = Path.Combine(server.DataDirectory, database.OrganizationId, database.ProjectId!, database.DatabaseId + ".db");
        await Assert.That(await engine.CreateDatabaseAsync(database, CancellationToken.None)).IsTrue();
        var stale = (await engine.OpenDatabaseAsync(database, CancellationToken.None))!.Incarnation;
        await Assert.That(await engine.DeleteDatabaseAsync(database, CancellationToken.None)).IsTrue();
        await Assert.That(await engine.CreateDatabaseAsync(database, CancellationToken.None)).IsTrue();
        var replacement = (await engine.OpenDatabaseAsync(database, CancellationToken.None))!.Incarnation;
        await Assert.That(replacement).IsNotEqualTo(stale);

        await Assert.That(await engine.DeleteDatabaseAsync(database, CancellationToken.None, incarnation: stale)).IsFalse();
        await Assert.That(File.Exists(file)).IsTrue();
        await Assert.That(await engine.DeleteDatabaseAsync(database, CancellationToken.None, incarnation: replacement)).IsTrue();
        await Assert.That(File.Exists(file)).IsFalse();
    }

    // Catalog streams that ended because acting on what they applied failed.
    private static async Task<double> ApplyFailuresAsync(TerraBaseTestServer server)
    {
        try
        {
            return await server.MetricAsync("terrabase_catalog_follower_failures_total", "reason=\"apply\"");
        }
        catch (InvalidOperationException)
        {
            // No such stream yet: the counter has no samples.
            return 0;
        }
    }

    // The catalog version document of this build's catalog.
    private static DocumentChange Version { get; } = new("catalog", "version", 1, "\"5\""u8.ToArray());

    private static async Task ReadyAsync(TerraBaseTestServer server) =>
        await TerraBaseTestServer.EventuallyAsync(async token =>
        {
            using var response = await server.Client.GetAsync(new Uri("/readyz", UriKind.Relative), token);
            return response.StatusCode == HttpStatusCode.OK;
        });

    // The receiver's first answer to a Sync stream for database.
    private static async Task<SyncResponse> HelloAsync(Features.Replication.V1.Replication.ReplicationClient client, TenantContext database, CancellationToken cancellationToken)
    {
        using var call = client.Sync(cancellationToken: cancellationToken);
        await call.RequestStream.WriteAsync(new SyncRequest { Hello = ReplicationProtocol.CreateHello(database, RandomNumberGenerator.GetBytes(16)) }, cancellationToken);
        return await call.ResponseStream.MoveNext(cancellationToken) ? call.ResponseStream.Current : throw new InvalidOperationException("no answer");
    }
}
