using System.Net;
using System.Security.Cryptography;
using System.Text.Json;
using Google.Protobuf;
using Microsoft.Extensions.DependencyInjection;
using TerraScale.TerraBase.Engine;
using TerraScale.TerraBase.Features.ApiKeys;
using TerraScale.TerraBase.Features.Management;
using TerraScale.TerraBase.Features.Replication;
using TerraScale.TerraBase.Features.Replication.V1;
using TerraScale.TerraBase.Hosting;
using TerraScale.TerraBase.Tenancy;
using TerraScale.TerraBase.Testing;

namespace TerraScale.TerraBase.Fast.Replication;

/// <summary>Catalog followers delete what their primary deletes, and replication brings none of it back (docs/decisions/0009).</summary>
public sealed class CatalogDeletionTests
{
    private static readonly string[] Scopes = [ApiKeyScopes.DocumentsRead, ApiKeyScopes.DocumentsWrite];

    [Test]
    public async Task A_database_deleted_on_the_primary_goes_from_its_followers_even_one_that_was_down_and_is_not_received_again()
    {
        using var peers = new PeerTls();
        using var tls = new ClientTls();
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(60));
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
            var user = Guid.NewGuid().ToString();
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
            var unknown = Path.Combine(followerDirectory, organization.Id, project.Id, Guid.NewGuid() + ".db");
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
        var (organization, project) = (Guid.NewGuid().ToString(), Guid.NewGuid().ToString());
        TenantContext Database()
        {
            return new(organization, PlanTier.Hobby, project, Guid.NewGuid().ToString());
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
            Intent(Guid.NewGuid().ToString(), tampered),
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
    }

    [Test]
    public async Task A_follower_retries_a_cleanup_that_failed_with_its_next_intents()
    {
        using var peers = new PeerTls();
        var origin = RandomNumberGenerator.GetBytes(16);
        var initial = JournalDigest.Initial(origin);
        var (organization, project) = (Guid.NewGuid().ToString(), Guid.NewGuid().ToString());
        var (failing, later) = (new TenantContext(organization, PlanTier.Hobby, project, Guid.NewGuid().ToString()),
            new TenantContext(organization, PlanTier.Hobby, project, Guid.NewGuid().ToString()));
        DocumentChange[] first = [new("pending_database_cleanup", failing.DatabaseId!, 10, JsonSerializer.SerializeToUtf8Bytes(failing, TerraBaseJsonContext.Default.TenantContext))];
        DocumentChange[] second = [new("pending_database_cleanup", later.DatabaseId!, 11, JsonSerializer.SerializeToUtf8Bytes(later, TerraBaseJsonContext.Default.TenantContext))];
        var firstDigest = JournalDigest.Compute(origin, 1, initial, first);
        TaskCompletionSource[] send = [new(TaskCreationOptions.RunContinuationsAsynchronously), new(TaskCreationOptions.RunContinuationsAsynchronously)];
        await using var fake = await FakePeer.StartCatalogAsync(peers, async (_, responses, context) =>
        {
            await responses.WriteAsync(new FollowResponse { Welcome = new CatalogWelcome { Protocol = 1, Origin = ByteString.CopyFrom(origin), Head = 0 } });
            await responses.WriteAsync(new FollowResponse { SnapshotBegin = new SnapshotBegin { Sequence = 0, Digest = ByteString.CopyFrom(initial) } });
            await responses.WriteAsync(new FollowResponse { Documents = CatalogProtocol.Page([Version]) });
            await responses.WriteAsync(new FollowResponse { SnapshotEnd = new SnapshotEnd { Sequence = 0, Digest = ByteString.CopyFrom(initial), CommitOffset = 1 } });
            await responses.WriteAsync(new FollowResponse { Heartbeat = new CatalogHeartbeat { Head = 0 } });
            foreach (var (sequence, group, previous) in new[] { (1UL, first, initial), (2UL, second, firstDigest) })
            {
                await send[sequence - 1].Task.WaitAsync(context.CancellationToken);
                await responses.WriteAsync(new FollowResponse { GroupBegin = new GroupBegin { Sequence = sequence, ChangeCount = 1 } });
                await responses.WriteAsync(new FollowResponse { Changes = CatalogProtocol.Page(group) });
                await responses.WriteAsync(new FollowResponse
                {
                    GroupEnd = new GroupEnd { Digest = ByteString.CopyFrom(JournalDigest.Compute(origin, (long)sequence, previous, group)) },
                });
                await responses.WriteAsync(new FollowResponse { Heartbeat = new CatalogHeartbeat { Head = sequence } });
            }

            await Task.Delay(Timeout.Infinite, context.CancellationToken);
        });

        // The first cleanup of the failing database fails, as a disk error would.
        var failures = 0;
        void Fail(string step, TenantContext database)
        {
            if (step == "follower.cleanup" && database.DatabaseId == failing.DatabaseId && Interlocked.Increment(ref failures) == 1)
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
        var engine = follower.Services.GetRequiredService<IStorageEngine>();
        string FileOf(TenantContext database)
        {
            return Path.Combine(follower.DataDirectory, organization, project, database.DatabaseId + ".db");
        }

        foreach (var database in new[] { failing, later })
        {
            await Assert.That(await engine.CreateDatabaseAsync(database, CancellationToken.None)).IsTrue();
        }

        send[0].SetResult();
        await TerraBaseTestServer.EventuallyAsync(async token => await follower.MetricAsync("terrabase_catalog_applied_sequence", "", token) == 1);
        await Assert.That(File.Exists(FileOf(failing))).IsTrue();
        send[1].SetResult();
        await TerraBaseTestServer.EventuallyAsync(async token => await follower.MetricAsync("terrabase_catalog_applied_sequence", "", token) == 2);
        await Assert.That(File.Exists(FileOf(failing))).IsFalse();
        await Assert.That(File.Exists(FileOf(later))).IsFalse();
    }

    [Test]
    public async Task A_follower_acts_at_startup_on_the_cleanup_intents_its_catalog_holds()
    {
        // A follower that stopped right after it applied an intent: the intent is in its catalog, the file still on disk.
        using var peers = new PeerTls();
        var origin = RandomNumberGenerator.GetBytes(16);
        var initial = JournalDigest.Initial(origin);
        var deleted = new TenantContext(Guid.NewGuid().ToString(), PlanTier.Hobby, Guid.NewGuid().ToString(), Guid.NewGuid().ToString());
        var intent = new DocumentChange("pending_database_cleanup", deleted.DatabaseId!, 10, JsonSerializer.SerializeToUtf8Bytes(deleted, TerraBaseJsonContext.Default.TenantContext));
        var streams = 0;
        await using var fake = await FakePeer.StartCatalogAsync(peers, async (_, responses, context) =>
        {
            // Only the first stream gets the catalog: the restarted follower acts before any stream.
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
            await Assert.That(File.Exists(file)).IsFalse();
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
