using System.Diagnostics.Metrics;
using System.Globalization;
using System.Net;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Time.Testing;
using TerraBase.Engine;
using TerraBase.Features.Documents;
using TerraBase.Features.Replication;
using TerraBase.Features.Replication.V1;
using TerraBase.Primitives;
using TerraBase.Testing;

namespace TerraBase.Fast.Documents;

public sealed class ReplicationShapeTests
{
    [Test]
    [Arguments(false)]
    [Arguments(true)]
    public async Task Imported_winners_below_the_receiver_head_wake_shapes_without_rewriting_merge_versions(bool equalVersion)
    {
        using var issuer = new TestIssuer();
        using var peerTls = new PeerTls();
        using var clientTls = new ClientTls();
        await using var server = await TerraBaseTestServer.StartAsync(settings =>
        {
            peerTls.Apply(settings);
            clientTls.Apply(settings);
            TestIssuer.Configure(settings);
        }, services =>
        {
            issuer.Route(services);
            services.AddKeyedSingleton<TimeProvider>(EngineExtensions.QueryDeadlineClock, new FakeTimeProvider());
        });
        var tenant = await ReplicationTenant.CreateAsync(server, "db");
        using var client = clientTls.CreateClient(server.ClientAddress!, await tenant.KeyAsync(server, "documents:read", "documents:write", "shapes:read"));
        var collections = $"{tenant.Path("db")}/collections";
        var documents = $"{collections}/items/documents";
        var shape = $"{collections}/items/shape";
        var feed = $"/v1/projects/{tenant.Database("db").ProjectId}/databases/db/collections/items/changes";
        var ownerToken = issuer.Mint(tenant.UserId);
        issuer.GrantProject(ownerToken, tenant.OrganizationId, tenant.ProjectId);
        using (var owner = clientTls.CreateClient(server.ClientAddress!, ownerToken))
        using (var bound = await owner.PostAsJsonAsync(ManagementApi.Uri(
            $"/v1/management/organizations/{tenant.OrganizationId}/projects/{tenant.ProjectId}/binding"), new { organization = tenant.OrganizationId }))
        {
            bound.EnsureSuccessStatusCode();
        }
        using var feedClient = clientTls.CreateClient(server.ClientAddress!, issuer.Mint(tenant.UserId, jwt =>
        {
            jwt.Claims["project_id"] = tenant.ProjectId;
            jwt.Claims["scope"] = "documents:read";
        }));
        (await DocumentApi.SendAsync(client, HttpMethod.Post, collections, JsonContent.Create(new { name = "items" }))).Status.ShouldBe(HttpStatusCode.Created);
        var originalId = DocumentApi.NewId();
        var original = await DocumentApi.SendAsync(client, HttpMethod.Put, $"{documents}/by-key?id={originalId}", JsonContent.Create(new { state = "a-original" }));
        original.Status.ShouldBe(HttpStatusCode.Created);
        var mergeVersion = equalVersion ? long.Parse(original.Json.GetProperty("offset").GetString()!, CultureInfo.InvariantCulture) : 1;
        var id = equalVersion ? originalId : DocumentApi.NewId();
        var clockId = DocumentApi.NewId();
        for (var index = 0; index < 3; index++)
        {
            (await DocumentApi.SendAsync(client, HttpMethod.Put, $"{documents}/by-key?id={clockId}", JsonContent.Create(new { index }))).Status.ShouldBe(index == 0 ? HttpStatusCode.Created : HttpStatusCode.OK);
        }

        var feedStart = await DocumentApi.SendAsync(feedClient, HttpMethod.Get, feed);
        await Assert.That(feedStart.Status).IsEqualTo(HttpStatusCode.OK).Because($"GET {feed}: {ResponseBody(feedStart)}");
        var feedToken = feedStart.Json.GetProperty("resume_token").GetString();
        var start = await DocumentApi.SendAsync(client, HttpMethod.Get, shape + "?offset=-1");
        start.Status.ShouldBe(HttpStatusCode.TemporaryRedirect);
        var snapshotUri = start.Location!.OriginalString;
        var snapshot = await DocumentApi.SendAsync(client, HttpMethod.Get, snapshotUri);
        snapshot.Status.ShouldBe(HttpStatusCode.OK);
        var snapshotBody = snapshot.Json.GetRawText();
        var handle = snapshot.Json.GetProperty("handle").GetString();
        var head = long.Parse(snapshot.Json.GetProperty("next_offset").GetString()!, CultureInfo.InvariantCulture);
        await Assert.That(head).IsGreaterThan(mergeVersion);
        var store = (await server.Services.GetRequiredService<IStorageEngine>().OpenDatabaseAsync(tenant.Database("db"), CancellationToken.None))!;
        long journalHead;
        Guid etagScope;
        await using (var read = await store.BeginTransactionAsync(write: false, CancellationToken.None))
        {
            journalHead = await read.GetLastChangeSequenceAsync(CancellationToken.None);
            etagScope = read.Scope;
        }

        // Observe this server's waiter registration before sending the peer change: no timing sleeps.
        var parked = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var scope = server.Services.GetRequiredService<IMeterFactory>();
        using var listener = new MeterListener
        {
            InstrumentPublished = (instrument, observer) =>
            {
                if (instrument.Meter.Scope == scope && instrument.Name == "terrabase.shape.requests.waiting")
                {
                    observer.EnableMeasurementEvents(instrument);
                }
            },
        };
        listener.SetMeasurementEventCallback<long>((_, value, _, _) =>
        {
            if (value > 0)
            {
                parked.TrySetResult();
            }
        });
        listener.Start();
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(TUnit.Core.TestContext.Current!.Execution.CancellationToken);
        var token = deadline.Token;
        using var channel = peerTls.CreateChannel(server.PeerAddress!);
        using var call = new Features.Replication.V1.Replication.ReplicationClient(channel).Sync(cancellationToken: token);
        var origin = EntityIds.New().ToByteArray();
        ReadOnlyMemory<byte> proof = JournalDigest.Initial(origin);
        await call.RequestStream.WriteAsync(new SyncRequest { Hello = ReplicationProtocol.CreateHello(tenant.Database("db"), origin) }, token);
        await Assert.That(await call.ResponseStream.MoveNext(token)).IsTrue();
        await Assert.That(call.ResponseStream.Current.Welcome).IsNotNull();
        var poll = DocumentApi.SendAsync(client, HttpMethod.Get, $"{shape}?handle={handle}&offset={head}&live=true");
        await parked.Task.WaitAsync(token);
        await Assert.That(poll.IsCompleted).IsFalse();
        await ApplyAsync(1, new DocumentChange("items", id, mergeVersion, "{\"state\":\"z-winner\"}"u8.ToArray()));
        var live = await poll.WaitAsync(token);
        live.Status.ShouldBe(HttpStatusCode.OK);
        var change = live.Json.GetProperty("changes").EnumerateArray().Single();
        await Assert.That(change.GetProperty("offset").GetString()).IsEqualTo((head + 1).ToString(CultureInfo.InvariantCulture));
        var op = change.GetProperty("ops").EnumerateArray().Single();
        await Assert.That(op.GetProperty("id").GetString()).IsEqualTo(id);
        await Assert.That(op.GetProperty("op").GetString()).IsEqualTo("upsert");
        await Assert.That(op.GetProperty("document").GetRawText()).IsEqualTo("{\"state\":\"z-winner\"}");
        var feedPage = await DocumentApi.SendAsync(feedClient, HttpMethod.Get, $"{feed}?resume_token={feedToken}");
        await Assert.That(feedPage.Status).IsEqualTo(HttpStatusCode.OK).Because($"GET {feed}: {ResponseBody(feedPage)}");
        var feedEvent = feedPage.Json.GetProperty("changes").EnumerateArray().Single();
        await Assert.That(feedEvent.GetProperty("id").GetString()).IsEqualTo(id);
        await Assert.That(feedEvent.GetProperty("document").GetRawText()).IsEqualTo("{\"state\":\"z-winner\"}");
        await Assert.That(feedEvent.GetProperty("offset").GetString()).IsEqualTo((head + 1).ToString(CultureInfo.InvariantCulture));
        feedToken = feedPage.Json.GetProperty("resume_token").GetString();
        var imported = await DocumentApi.SendAsync(client, HttpMethod.Get, $"{documents}/by-key?id={id}");
        imported.Status.ShouldBe(HttpStatusCode.OK);
        await Assert.That(imported.ETag).IsEqualTo(ETags.Version(etagScope, "items", id, mergeVersion, "{\"state\":\"z-winner\"}"u8.ToArray()));
        await Assert.That(imported.Version).IsEqualTo(imported.ETag);
        await Assert.That(op.GetProperty("version").GetString()).IsEqualTo(imported.Version);
        if (equalVersion)
        {
            await Assert.That(imported.Version).IsNotEqualTo(original.Version);
            var refreshed = await DocumentApi.SendAsync(client, HttpMethod.Get, $"{documents}/by-key?id={id}", configure: DocumentApi.IfNoneMatch(original.Version));
            refreshed.Status.ShouldBe(HttpStatusCode.OK);
            await Assert.That(refreshed.Json.GetProperty("document").GetRawText()).IsEqualTo("{\"state\":\"z-winner\"}");
            await Assert.That(refreshed.ETag).IsEqualTo(imported.ETag);
            var staleRead = await DocumentApi.SendAsync(client, HttpMethod.Get, $"{documents}/by-key?id={id}", configure: DocumentApi.IfMatch(original.Version));
            staleRead.Status.ShouldBe(HttpStatusCode.PreconditionFailed);
            var stalePut = await DocumentApi.SendAsync(client, HttpMethod.Put, $"{documents}/by-key?id={id}",
                JsonContent.Create(new { state = "stale-put" }), DocumentApi.IfMatch(original.Version));
            stalePut.Status.ShouldBe(HttpStatusCode.PreconditionFailed);
            var stalePatch = await DocumentApi.SendAsync(client, HttpMethod.Patch, $"{documents}/by-key?id={id}",
                new StringContent("{\"state\":\"stale-patch\"}", System.Text.Encoding.UTF8, "application/merge-patch+json"), DocumentApi.IfMatch(original.Version));
            stalePatch.Status.ShouldBe(HttpStatusCode.PreconditionFailed);
            var staleDelete = await DocumentApi.SendAsync(client, HttpMethod.Delete, $"{documents}/by-key?id={id}", configure: DocumentApi.IfMatch(original.Version));
            staleDelete.Status.ShouldBe(HttpStatusCode.PreconditionFailed);
            object[] staleOps = [new { op = "put", collection = "items", id, document = new { state = "stale-transaction" }, if_match = original.Version }];
            var staleTransaction = await DocumentApi.SendAsync(client, HttpMethod.Post, $"{tenant.Path("db")}/transactions", JsonContent.Create(new { ops = staleOps }));
            staleTransaction.Status.ShouldBe(HttpStatusCode.PreconditionFailed);
            await Assert.That(staleTransaction.Op).IsEqualTo(0);
        }

        var unchanged = await DocumentApi.SendAsync(client, HttpMethod.Get, $"{documents}/by-key?id={id}", configure: DocumentApi.IfNoneMatch(imported.Version));
        unchanged.Status.ShouldBe(HttpStatusCode.NotModified);

        var logUri = $"{shape}?handle={handle}&offset={head}";
        var log = await DocumentApi.SendAsync(client, HttpMethod.Get, logUri);
        await Assert.That(log.Json.GetRawText()).IsEqualTo(live.Json.GetRawText());
        var validated = await DocumentApi.SendAsync(client, HttpMethod.Get, logUri, configure: DocumentApi.IfNoneMatch(log.ETag!));
        await Assert.That(validated.Status).IsEqualTo(HttpStatusCode.NotModified);
        await Assert.That(validated.ETag).IsEqualTo(log.ETag);
        var fresh = await CurrentSnapshotAsync();
        var freshOp = fresh.Json.GetProperty("changes")[0].GetProperty("ops").EnumerateArray().Single(item => item.GetProperty("id").GetString() == id);
        await Assert.That(freshOp.GetProperty("document").GetRawText()).IsEqualTo(op.GetProperty("document").GetRawText());
        await Assert.That(freshOp.GetProperty("version").GetString()).IsEqualTo(imported.Version);
        await Assert.That(fresh.Json.GetProperty("next_offset").GetString()).IsEqualTo((head + 1).ToString(CultureInfo.InvariantCulture));
        await AssertOldSnapshotAsync();
        await using (var read = await store.BeginTransactionAsync(write: false, token))
        {
            await Assert.That(await read.GetVersionAsync("items", id, token)).IsEqualTo(new DocumentVersion(mergeVersion, Deleted: false));
            await Assert.That(await read.GetOffsetAsync(token)).IsEqualTo(head + 1);
            await Assert.That(await read.GetLastChangeSequenceAsync(token)).IsEqualTo(journalHead);
        }

        parked = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var deletionPoll = DocumentApi.SendAsync(client, HttpMethod.Get, $"{shape}?handle={handle}&offset={head + 1}&live=true");
        await parked.Task.WaitAsync(token);
        await Assert.That(deletionPoll.IsCompleted).IsFalse();
        // An equal-version tombstone wins over the imported live body without changing the merge clock.
        await ApplyAsync(2, new DocumentChange("items", id, mergeVersion, null));
        var deleted = await deletionPoll.WaitAsync(token);
        var feedDeletion = await DocumentApi.SendAsync(feedClient, HttpMethod.Get, $"{feed}?resume_token={feedToken}");
        await Assert.That(feedDeletion.Status).IsEqualTo(HttpStatusCode.OK).Because($"GET {feed}: {ResponseBody(feedDeletion)}");
        var feedDelete = feedDeletion.Json.GetProperty("changes").EnumerateArray().Single();
        await Assert.That(feedDelete.GetProperty("operation").GetString()).IsEqualTo("delete");
        await Assert.That(feedDelete.GetProperty("id").GetString()).IsEqualTo(id);
        await Assert.That(feedDelete.GetProperty("offset").GetString()).IsEqualTo((head + 2).ToString(CultureInfo.InvariantCulture));
        deleted.Status.ShouldBe(HttpStatusCode.OK);
        var deletion = deleted.Json.GetProperty("changes").EnumerateArray().Single();
        await Assert.That(deletion.GetProperty("offset").GetString()).IsEqualTo((head + 2).ToString(CultureInfo.InvariantCulture));
        var tombstone = await DocumentApi.SendAsync(client, HttpMethod.Get, $"{documents}/by-key?id={id}");
        await Assert.That(tombstone.Status).IsEqualTo(HttpStatusCode.NotFound);
        var deletionOp = deletion.GetProperty("ops").EnumerateArray().Single();
        await Assert.That(deletionOp.GetProperty("op").GetString()).IsEqualTo("delete");
        await Assert.That(deletionOp.GetProperty("id").GetString()).IsEqualTo(id);
        await Assert.That(deletionOp.TryGetProperty("version", out _)).IsFalse();
        await Assert.That(tombstone.ETag).IsNotEqualTo(imported.Version);
        await Assert.That(tombstone.ETag).IsEqualTo(ETags.Version(etagScope, "items", id, mergeVersion, ReadOnlyMemory<byte>.Empty, deleted: true));
        var staleRecreation = await DocumentApi.SendAsync(client, HttpMethod.Put, $"{documents}/by-key?id={id}",
            JsonContent.Create(new { state = "stale-recreation" }), DocumentApi.IfMatch(imported.Version));
        staleRecreation.Status.ShouldBe(HttpStatusCode.PreconditionFailed);
        var afterDeletion = await CurrentSnapshotAsync();
        await Assert.That(afterDeletion.Json.GetProperty("changes")[0].GetProperty("ops").EnumerateArray().Any(item => item.GetProperty("id").GetString() == id)).IsFalse();
        await Assert.That(afterDeletion.Json.GetProperty("next_offset").GetString()).IsEqualTo((head + 2).ToString(CultureInfo.InvariantCulture));
        await AssertOldSnapshotAsync();
        await using (var read = await store.BeginTransactionAsync(write: false, token))
        {
            await Assert.That(await read.GetVersionAsync("items", id, token)).IsEqualTo(new DocumentVersion(mergeVersion, Deleted: true));
            await Assert.That(await read.GetOffsetAsync(token)).IsEqualTo(head + 2);
            await Assert.That(await read.GetAppliedSequenceAsync(origin, token)).IsEqualTo(2);
            await Assert.That(await read.GetLastChangeSequenceAsync(token)).IsEqualTo(journalHead);
        }

        var recreated = await DocumentApi.SendAsync(client, HttpMethod.Put, $"{documents}/by-key?id={id}",
            JsonContent.Create(new { state = "recreated" }), DocumentApi.IfMatch(tombstone.ETag!));
        recreated.Status.ShouldBe(HttpStatusCode.Created);
        var recreatedRead = await DocumentApi.SendAsync(client, HttpMethod.Get, $"{documents}/by-key?id={id}");
        recreatedRead.Status.ShouldBe(HttpStatusCode.OK);
        await Assert.That(recreatedRead.Version).IsEqualTo(recreated.Version);
        await Assert.That(recreatedRead.Json.GetProperty("document").GetProperty("state").GetString()).IsEqualTo("recreated");

        async Task ApplyAsync(long sequence, DocumentChange incoming)
        {
            await call.RequestStream.WriteAsync(new SyncRequest { CommittedGroup = ReplicationProtocol.CreateBatch(tenant.Database("db"), origin, sequence, [incoming], proof) }, token);
            await Assert.That(await call.ResponseStream.MoveNext(token)).IsTrue();
            await Assert.That(call.ResponseStream.Current.Durable.DurablePrefix).IsEqualTo((ulong)sequence);
            proof = call.ResponseStream.Current.Durable.JournalDigest.Memory;
            await Assert.That(await call.ResponseStream.MoveNext(token)).IsTrue();
            await Assert.That(call.ResponseStream.Current.Applied.AppliedPrefix).IsEqualTo((ulong)sequence);
        }

        async Task AssertOldSnapshotAsync()
        {
            var replay = await DocumentApi.SendAsync(client, HttpMethod.Get, snapshotUri);
            replay.Status.ShouldBe(HttpStatusCode.OK);
            await Assert.That(replay.Json.GetRawText()).IsEqualTo(snapshotBody);
            await Assert.That(replay.ETag).IsEqualTo(snapshot.ETag);
            var cached = await DocumentApi.SendAsync(client, HttpMethod.Get, snapshotUri, configure: DocumentApi.IfNoneMatch(snapshot.ETag!));
            await Assert.That(cached.Status).IsEqualTo(HttpStatusCode.NotModified);
        }

        async Task<ApiResponse> CurrentSnapshotAsync()
        {
            var redirect = await DocumentApi.SendAsync(client, HttpMethod.Get, shape + "?offset=-1");
            redirect.Status.ShouldBe(HttpStatusCode.TemporaryRedirect);
            var page = await DocumentApi.SendAsync(client, HttpMethod.Get, redirect.Location!.OriginalString);
            page.Status.ShouldBe(HttpStatusCode.OK);
            return page;
        }
    }

    private static string ResponseBody(ApiResponse response)
    {
        var text = response.Json.ToString();
        return text[..Math.Min(text.Length, 4096)];
    }
}
