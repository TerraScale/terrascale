using System.Net;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using TerraBase.Engine;
using TerraBase.Engine.Turso;
using TerraBase.Hosting;
using TerraBase.Primitives;
using TerraBase.Tenancy;
using TerraBase.Testing;

namespace TerraBase.Fast.Storage;

public sealed class CommitMarkerTests
{
    private static readonly ReadOnlyMemory<byte> Body = Encoding.UTF8.GetBytes("""{"n":1}""");

    [Test]
    public async Task A_marker_names_its_own_commit_and_an_assertion_has_no_marker()
    {
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(60));
        var token = deadline.Token;
        await using var api = await ManagementApi.StartAsync(cancellationToken: token);
        var managed = await api.CreateManagedDatabaseAsync();
        using var data = await api.CreateClientAsync(managed.Organization, "documents:write", "documents:read");
        await ManagementApi.CreateCollectionAsync(data, managed, "items");
        var (store, tenant) = await OpenAsync(api, managed, token);
        var before = await CapturedWrites.HeadAsync(store, token);
        var id = EntityIds.New().ToString();
        var (_, firstCommit) = await PutAsync(store, id, token);
        await Assert.That(firstCommit.Marker).IsNotNull();
        var marker = firstCommit.Marker!.Value;
        await Assert.That(marker.Incarnation).IsEqualTo(store.Incarnation).Because("the marker must name the writer's actual incarnation");
        var read = await CapturedWrites.ReadAsync(store, before, token);
        await Assert.That(marker.ChangeId).IsEqualTo(read.Single(group => group.Ids.Contains(id)).ChangeId);
        await Assert.That(firstCommit.Offset).IsGreaterThan(0);
        await Assert.That(await StatusAsync(data, managed, id, token)).IsEqualTo(HttpStatusCode.OK);
        await AssertValueAsync(data, managed, id, 1, token);

        var later = EntityIds.New().ToString();
        using (var response = await data.PutAsJsonAsync(Document(managed, later), new { n = 2 }, token))
        {
            await Assert.That(response.StatusCode).IsEqualTo(HttpStatusCode.Created);
        }

        await Assert.That(await CapturedWrites.HeadAsync(store, token)).IsGreaterThan(marker.ChangeId);
        await AssertValueAsync(data, managed, later, 2, token);
        var (asserted, assertionCommit) = await store.WriteAsync((transaction, inner) => transaction.GetAsync("items", id, inner), document => document is not null, token);
        await Assert.That(asserted).IsNotNull();
        await Assert.That(assertionCommit.Marker).IsNull();
    }

    [Test]
    public async Task A_competing_writer_cannot_replace_the_first_writes_own_marker()
    {
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(60));
        var token = deadline.Token;
        using var releaseMarker = new ManualResetEventSlim();
        var markerEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var armed = 0;
        string? target = null;
        await using var api = await ManagementApi.StartAsync(cancellationToken: token,
            services: services => services.AddSingleton<DatabaseLifecycleCheckpoint>((step, database) =>
            {
                if (step == "writer.marker" && database.DatabaseId == Volatile.Read(ref target) && Interlocked.Exchange(ref armed, 0) == 1)
                {
                    markerEntered.TrySetResult();
                    releaseMarker.Wait(token);
                }
            }));
        var managed = await api.CreateManagedDatabaseAsync();
        using var data = await api.CreateClientAsync(managed.Organization, "documents:write", "documents:read");
        await ManagementApi.CreateCollectionAsync(data, managed, "items");
        var (store, tenant) = await OpenAsync(api, managed, token);
        var before = await CapturedWrites.HeadAsync(store, token);
        var firstId = EntityIds.New().ToString();
        var laterId = EntityIds.New().ToString();
        await using var transaction = await store.BeginTransactionAsync(write: true, token);
        _ = await transaction.PutAsync("items", firstId, Body, token);
        Volatile.Write(ref target, tenant.DatabaseId);
        Volatile.Write(ref armed, 1);
        var firstCommit = transaction.CommitAsync(token).AsTask();
        Task<(long Result, CommitResult Commit)> later;
        try
        {
            await markerEntered.Task.WaitAsync(token);
            later = PutAsync(store, laterId, token).AsTask();
            await UntilAsync(() => store.BatchedWrites == 1, token);
            await Assert.That(firstCommit.IsCompleted).IsFalse();
            await Assert.That(later.IsCompleted).IsFalse();
        }
        finally
        {
            releaseMarker.Set();
        }

        await transaction.DisposeAsync();
        var (_, laterCommit) = await later;
        var original = await firstCommit;
        await Assert.That(original.Marker).IsNotNull();
        await Assert.That(laterCommit.Marker).IsNotNull();
        await Assert.That(original.Marker!.Value.Incarnation).IsEqualTo(store.Incarnation);
        await Assert.That(laterCommit.Marker!.Value.Incarnation).IsEqualTo(original.Marker.Value.Incarnation);
        await Assert.That(laterCommit.Marker.Value.ChangeId).IsGreaterThan(original.Marker.Value.ChangeId);
        var groups = await CapturedWrites.ReadAsync(store, before, token);
        await Assert.That(original.Marker.Value.ChangeId).IsEqualTo(groups.Single(group => group.Ids.Contains(firstId)).ChangeId);
        await Assert.That(laterCommit.Marker.Value.ChangeId).IsEqualTo(groups.Single(group => group.Ids.Contains(laterId)).ChangeId);
        await AssertValueAsync(data, managed, firstId, 1, token);
        await AssertValueAsync(data, managed, laterId, 1, token);
    }

    [Test]
    public async Task A_shared_batch_gives_markers_only_to_its_kept_mutations()
    {
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(60));
        var token = deadline.Token;
        await using var api = await ManagementApi.StartAsync(cancellationToken: token);
        var managed = await api.CreateManagedDatabaseAsync();
        using var data = await api.CreateClientAsync(managed.Organization, "documents:write", "documents:read");
        await ManagementApi.CreateCollectionAsync(data, managed, "items");
        var (store, tenant) = await OpenAsync(api, managed, token);
        var seed = EntityIds.New().ToString();
        _ = await PutAsync(store, seed, token);
        var before = await CapturedWrites.HeadAsync(store, token);
        var kept = EntityIds.New().ToString();
        var alsoKept = EntityIds.New().ToString();
        var refused = EntityIds.New().ToString();
        Task<(long Result, CommitResult Commit)>? lead = null;
        Task<(long Result, CommitResult Commit)>? mutation = null;
        Task<(long Result, CommitResult Commit)>? sibling = null;
        Task<(StoredDocument? Result, CommitResult Commit)>? assertion = null;
        Task<(long Result, CommitResult Commit)>? rejected = null;

        await using (var blocker = await store.BeginDurableTransactionAsync(token))
        {
            lead = PutAsync(store, EntityIds.New().ToString(), token).AsTask();
            await UntilAsync(() => store.BatchedWrites == 1, token);
            mutation = PutAsync(store, kept, token).AsTask();
            sibling = PutAsync(store, alsoKept, token).AsTask();
            assertion = store.WriteAsync((transaction, inner) => transaction.GetAsync("items", seed, inner), document => document is not null, token).AsTask();
            rejected = store.WriteAsync((transaction, inner) => transaction.PutAsync("items", refused, Body, inner), _ => false, token).AsTask();
            await UntilAsync(() => store.QueuedWrites == 4, token);
        }

        _ = await lead;
        var (_, writtenCommit) = await mutation;
        var (_, otherCommit) = await sibling;
        var (checkedOnly, assertionCommit) = await assertion;
        var (_, rejectedCommit) = await rejected;
        await Assert.That(writtenCommit.Marker).IsNotNull();
        await Assert.That(writtenCommit.Marker!.Value.Incarnation).IsEqualTo(store.Incarnation);
        await Assert.That(otherCommit).IsEqualTo(writtenCommit).Because("both mutations share their batch's own CDC commit row");
        await Assert.That(checkedOnly).IsNotNull();
        await Assert.That(assertionCommit.Offset).IsEqualTo(writtenCommit.Offset);
        await Assert.That(assertionCommit.Marker).IsNull().Because("assertions never inherit a sibling mutation's marker");
        await Assert.That(rejectedCommit).IsEqualTo(default);
        await Assert.That(await StatusAsync(data, managed, kept, token)).IsEqualTo(HttpStatusCode.OK);
        await Assert.That(await StatusAsync(data, managed, alsoKept, token)).IsEqualTo(HttpStatusCode.OK);
        await AssertValueAsync(data, managed, kept, 1, token);
        await AssertValueAsync(data, managed, alsoKept, 1, token);
        await Assert.That(await StatusAsync(data, managed, refused, token)).IsEqualTo(HttpStatusCode.NotFound);
        var groups = await CapturedWrites.ReadAsync(store, before, token);
        await Assert.That(groups.Single(group => group.Ids.Contains(kept)).ChangeId).IsEqualTo(writtenCommit.Marker!.Value.ChangeId);
        await Assert.That(groups.Single(group => group.Ids.Contains(alsoKept)).ChangeId).IsEqualTo(writtenCommit.Marker!.Value.ChangeId);
        await Assert.That(groups.SelectMany(group => group.Ids).Contains(refused)).IsFalse();
    }

    [Test]
    [Arguments("writer.marker")]
    [Arguments("writer.reset")]
    public async Task A_post_commit_failure_never_replays_the_write_or_loses_its_commit_signal(string failingStep)
    {
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(60));
        var token = deadline.Token;
        var armed = 0;
        string? target = null;
        TursoDatabaseStore? committing = null;
        TursoSession? markedSession = null;
        await using var api = await ManagementApi.StartAsync(cancellationToken: token,
            services: services => services.AddSingleton<DatabaseLifecycleCheckpoint>((step, database) =>
            {
                if (database.DatabaseId != Volatile.Read(ref target) || Volatile.Read(ref armed) == 0)
                {
                    return;
                }

                if (step == "writer.marker")
                {
                    Volatile.Write(ref markedSession, committing!.CurrentWriter());
                }

                if (step == failingStep && Volatile.Read(ref markedSession) is { } marked
                    && ReferenceEquals(committing!.CurrentWriter(), marked) && Interlocked.Exchange(ref armed, 0) == 1)
                {
                    throw new IOException("a test makes the post-commit checkpoint fail");
                }
            }));
        var managed = await api.CreateManagedDatabaseAsync();
        using var data = await api.CreateClientAsync(managed.Organization, "documents:write", "documents:read");
        await ManagementApi.CreateCollectionAsync(data, managed, "items");
        var (store, tenant) = await OpenAsync(api, managed, token);
        committing = store;
        _ = await PutAsync(store, EntityIds.New().ToString(), token);
        var before = await CapturedWrites.HeadAsync(store, token);
        var incarnation = store.Incarnation;
        var sequence = store.CommitSequence;
        var session = store.CurrentWriter();
        var id = EntityIds.New().ToString();
        Volatile.Write(ref target, tenant.DatabaseId);
        Volatile.Write(ref armed, 1);
        await using (var uncaptured = await store.BeginTransactionAsync(write: true, token))
        {
            await Assert.That((await uncaptured.CommitAsync(token)).Marker).IsNull();
        }

        await Assert.That(Volatile.Read(ref armed)).IsEqualTo(1).Because("uncaptured writer cleanup cannot consume this mutation's fault");
        if (failingStep == "writer.marker")
        {
            await Assert.ThrowsAsync<IOException>(() => PutAsync(store, id, token).AsTask());
        }
        else
        {
            var (_, commit) = await PutAsync(store, id, token);
            await Assert.That(commit.Marker).IsNotNull();
            await Assert.That(commit.Marker!.Value.Incarnation).IsEqualTo(incarnation);
            await Assert.That(store.CurrentWriter()).IsNotSameReferenceAs(session);
            await Assert.That(session!.Connection.State).IsEqualTo(System.Data.ConnectionState.Closed);
        }

        await Assert.That(Volatile.Read(ref armed)).IsEqualTo(0).Because("the intended mutation's checkpoint actually failed");
        await Assert.That(Volatile.Read(ref markedSession)).IsSameReferenceAs(session);
        await Assert.That(store.CommitSequence).IsGreaterThan(sequence).Because("the successful COMMIT must notify waiters even when its marker read fails");
        await Assert.That(store.Incarnation).IsEqualTo(incarnation);
        if (failingStep == "writer.marker")
        {
            await AssertFaultedCommitAsync(api, store, before, id, token);
            return;
        }
        await Assert.That(await StatusAsync(data, managed, id, token)).IsEqualTo(HttpStatusCode.OK);
        await AssertValueAsync(data, managed, id, 1, token);
        await Assert.That((await CapturedWrites.ReadAsync(store, before, token))
            .Count(group => group.Ids.Contains(id))).IsEqualTo(1);
        await Assert.That((await PutAsync(store, EntityIds.New().ToString(), token)).Commit.Marker).IsNotNull();
    }

    [Test]
    [Arguments("writer.marker", HttpStatusCode.ServiceUnavailable)]
    [Arguments("writer.reset", HttpStatusCode.Created)]
    public async Task Client_success_requires_its_marker_while_a_failed_session_reset_preserves_the_committed_outcome(
        string failingStep, HttpStatusCode expected)
    {
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(60));
        var token = deadline.Token;
        var armed = 0;
        string? target = null;
        TursoDatabaseStore? committing = null;
        TursoSession? markedSession = null;
        await using var api = await ManagementApi.StartAsync(cancellationToken: token,
            services: services => services.AddSingleton<DatabaseLifecycleCheckpoint>((step, database) =>
            {
                if (database.DatabaseId != Volatile.Read(ref target) || Volatile.Read(ref armed) == 0)
                {
                    return;
                }

                if (step == "writer.marker")
                {
                    Volatile.Write(ref markedSession, committing!.CurrentWriter());
                }

                if (step == failingStep && Volatile.Read(ref markedSession) is { } marked
                    && ReferenceEquals(committing!.CurrentWriter(), marked) && Interlocked.Exchange(ref armed, 0) == 1)
                {
                    throw new IOException("a test fails this client's post-commit checkpoint");
                }
            }));
        var managed = await api.CreateManagedDatabaseAsync();
        using var data = await api.CreateClientAsync(managed.Organization, "documents:write", "documents:read");
        await ManagementApi.CreateCollectionAsync(data, managed, "items");
        var (store, tenant) = await OpenAsync(api, managed, token);
        committing = store;
        _ = await PutAsync(store, EntityIds.New().ToString(), token);
        var before = await CapturedWrites.HeadAsync(store, token);
        var incarnation = store.Incarnation;
        var id = EntityIds.New().ToString();
        Volatile.Write(ref target, tenant.DatabaseId);
        Volatile.Write(ref armed, 1);
        await using (var uncaptured = await store.BeginTransactionAsync(write: true, token))
        {
            await Assert.That((await uncaptured.CommitAsync(token)).Marker).IsNull();
        }

        await Assert.That(Volatile.Read(ref armed)).IsEqualTo(1).Because("uncaptured writer cleanup cannot consume this client's fault");
        using (var response = await data.PutAsJsonAsync(Document(managed, id), new { n = 1 }, token))
        {
            await Assert.That(response.StatusCode).IsEqualTo(expected);
            if (failingStep == "writer.marker")
            {
                var body = await response.Content.ReadFromJsonAsync<System.Text.Json.JsonElement>(token);
                await Assert.That(body.GetProperty("error").GetProperty("code").GetString()).IsEqualTo("write_outcome_unknown");
            }
        }

        await Assert.That(Volatile.Read(ref armed)).IsEqualTo(0).Because("the requested post-commit failure actually executed");
        if (failingStep == "writer.reset")
        {
            await Assert.That(Volatile.Read(ref markedSession)!.Connection.State).IsEqualTo(System.Data.ConnectionState.Closed);
            await Assert.That(store.CurrentWriter()).IsNotSameReferenceAs(Volatile.Read(ref markedSession));
        }
        await Assert.That(store.Incarnation).IsEqualTo(incarnation);
        if (failingStep == "writer.marker")
        {
            await AssertFaultedCommitAsync(api, store, before, id, token);
            return;
        }
        await Assert.That(await StatusAsync(data, managed, id, token)).IsEqualTo(HttpStatusCode.OK);
        await AssertValueAsync(data, managed, id, 1, token);
        await Assert.That((await CapturedWrites.ReadAsync(store, before, token))
            .Count(group => group.Ids.Contains(id))).IsEqualTo(1).Because("a failed response never replays the ambiguous mutation");

        var nextId = EntityIds.New().ToString();
        using var next = await data.PutAsJsonAsync(Document(managed, nextId), new { n = 2 }, token);
        await Assert.That(next.StatusCode).IsEqualTo(HttpStatusCode.Created);
        await AssertValueAsync(data, managed, nextId, 2, token);
    }

    [Test]
    public async Task A_cancelled_commit_has_no_result_and_rolls_back_its_document_and_capture()
    {
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(60));
        var token = deadline.Token;
        await using var api = await ManagementApi.StartAsync(cancellationToken: token);
        var managed = await api.CreateManagedDatabaseAsync();
        using var data = await api.CreateClientAsync(managed.Organization, "documents:write", "documents:read");
        await ManagementApi.CreateCollectionAsync(data, managed, "items");
        var (store, tenant) = await OpenAsync(api, managed, token);
        var before = await CapturedWrites.HeadAsync(store, token);
        var incarnation = store.Incarnation;
        var sequence = store.CommitSequence;
        var id = EntityIds.New().ToString();
        CommitResult? result = null;
        await using (var transaction = await store.BeginTransactionAsync(write: true, token))
        {
            _ = await transaction.PutAsync("items", id, Body, token);
            await Assert.ThrowsAsync<OperationCanceledException>(async () =>
            {
                result = await transaction.CommitAsync(new CancellationToken(canceled: true));
            });
        }

        await Assert.That(result).IsNull().Because("a rolled-back attempt has no committed outcome or marker");
        await Assert.That(store.CommitSequence).IsEqualTo(sequence);
        await Assert.That(store.Incarnation).IsEqualTo(incarnation);
        await Assert.That(await StatusAsync(data, managed, id, token)).IsEqualTo(HttpStatusCode.NotFound);
        await Assert.That((await CapturedWrites.ReadAsync(store, before, token))
            .Any(group => group.Ids.Contains(id))).IsFalse();
    }

    private static async Task AssertFaultedCommitAsync(
        ManagementApi api, TursoDatabaseStore store, long before, string id, CancellationToken token)
    {
        var engine = api.Server.Services.GetRequiredService<TursoStorageEngine>();
        await Assert.That(engine.Ledger.Refusal(store)).IsNotNull();
        await using (var read = await store.BeginTransactionAsync(write: false, token))
        {
            var document = await read.GetAsync("items", id, token);
            await Assert.That(document).IsNotNull();
            using var body = JsonDocument.Parse(document!.Json);
            await Assert.That(body.RootElement.GetProperty("n").GetInt32()).IsEqualTo(1);
        }
        await Assert.That((await CapturedWrites.ReadAsync(store, before, token)).Count(group => group.Ids.Contains(id))).IsEqualTo(1);
        await Assert.ThrowsAsync<StorageCopyFaultedException>(() => PutAsync(store, EntityIds.New().ToString(), token).AsTask());
    }

    private static ValueTask<(long Result, CommitResult Commit)> PutAsync(TursoDatabaseStore store, string id, CancellationToken token) =>
        store.WriteAsync((transaction, inner) => transaction.PutAsync("items", id, Body, inner), _ => true, token);

    private static async Task<(TursoDatabaseStore Store, TenantContext Tenant)> OpenAsync(
        ManagementApi api, ManagedDatabaseRef managed, CancellationToken token)
    {
        var tenant = await api.Server.Services.GetRequiredService<ITenantDirectory>().FindDatabaseAsync(managed.Organization, managed.Project, managed.Name, token)
            is TenantContext found ? found : throw new InvalidOperationException("the database is not registered");
        var store = (TursoDatabaseStore)(await api.Server.Services.GetRequiredService<TursoStorageEngine>().OpenDatabaseAsync(tenant, token))!;
        return (store, tenant);
    }

    private static Uri Document(ManagedDatabaseRef managed, string id) => ManagementApi.Uri($"{managed.Data}/collections/items/documents/by-key?id={id}");

    private static async Task<HttpStatusCode> StatusAsync(HttpClient data, ManagedDatabaseRef managed, string id, CancellationToken token)
    {
        using var response = await data.GetAsync(Document(managed, id), token);
        return response.StatusCode;
    }

    private static async Task AssertValueAsync(HttpClient data, ManagedDatabaseRef managed, string id, int expected, CancellationToken token)
    {
        using var response = await data.GetAsync(Document(managed, id), token);
        await Assert.That(response.StatusCode).IsEqualTo(HttpStatusCode.OK);
        var body = JsonElement.Parse(await response.Content.ReadAsStringAsync(token));
        await Assert.That(body.GetProperty("document").GetProperty("n").GetInt32()).IsEqualTo(expected);
    }

    private static async Task UntilAsync(Func<bool> condition, CancellationToken token)
    {
        while (!condition())
        {
            token.ThrowIfCancellationRequested();
            await Task.Yield();
        }
    }
}
