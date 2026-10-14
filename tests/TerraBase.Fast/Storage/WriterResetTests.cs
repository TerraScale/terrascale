using System.Net;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using TerraBase.Engine.Sqlite;
using TerraBase.Features.Replication;
using TerraBase.Hosting;
using TerraBase.Primitives;
using TerraBase.Tenancy;
using TerraBase.Testing;

namespace TerraBase.Fast.Storage;

/// <summary>
/// Resetting a write session after its transaction (back to <c>synchronous = NORMAL</c>, capture on) can fail. A
/// transaction that committed and synced keeps its outcome: the session is discarded, never used again, and the write is
/// answered as written, with its commit signal, change feed and incarnation as for any commit. A transaction that did not
/// commit still fails. The fault comes from the host's lifecycle checkpoint (<c>writer.reset</c>), a no-op in production.
/// </summary>
public sealed class WriterResetTests
{
    [Test]
    public async Task A_failed_writer_reset_after_a_commit_keeps_the_write_and_discards_the_session()
    {
        var failing = 0;
        string? target = null;
        await using var api = await ManagementApi.StartAsync(services: services => services.AddSingleton<DatabaseLifecycleCheckpoint>((step, database) =>
        {
            if (step == "writer.reset" && database.DatabaseId == Volatile.Read(ref target) && Volatile.Read(ref failing) == 1)
            {
                throw new IOException("a test makes the writer's reset fail");
            }
        }));
        var managed = await api.CreateManagedDatabaseAsync();
        using var data = await api.CreateClientAsync(managed.Organization, "documents:write", "documents:read");
        await ManagementApi.CreateCollectionAsync(data, managed, "items");
        var tenant = await api.Server.Services.GetRequiredService<ITenantDirectory>().FindDatabaseAsync(managed.Organization, managed.Project, managed.Name, CancellationToken.None)
            is TenantContext found ? found : throw new InvalidOperationException("the database is not registered");
        var engine = api.Server.Services.GetRequiredService<SqliteStorageEngine>();
        var store = (SqliteDatabaseStore)(await engine.OpenDatabaseAsync(tenant, CancellationToken.None))!;
        var source = api.Server.Services.GetRequiredService<IChangeGroupSource>();
        Volatile.Write(ref target, tenant.DatabaseId);

        // A first write opens the writer session.
        await Assert.That(await PutAsync(data, managed, EntityIds.New().ToString(), 0)).IsEqualTo(HttpStatusCode.Created);
        var session = store.CurrentWriter() ?? throw new InvalidOperationException("no writer session after a write");
        var incarnation = store.Incarnation;
        var head = await source.HeadAsync(tenant, incarnation, CancellationToken.None) ?? throw new InvalidOperationException("the copy refused its own incarnation");

        // Every reset of this database's writer fails while armed: the write after it committed is still answered as written.
        var written = EntityIds.New().ToString();
        Volatile.Write(ref failing, 1);
        await Assert.That(await PutAsync(data, managed, written, 1)).IsEqualTo(HttpStatusCode.Created);
        Volatile.Write(ref failing, 0);

        // The session is discarded (closed, and not the writer any more); the store keeps its incarnation.
        await Assert.That(session.Connection.State).IsEqualTo(System.Data.ConnectionState.Closed);
        await Assert.That(store.CurrentWriter()).IsNotSameReferenceAs(session);
        await Assert.That(store.Incarnation).IsEqualTo(incarnation);

        // The commit is visible to reads and reached the change capture the peer streams ship from.
        await Assert.That(await GetAsync(data, managed, written)).IsEqualTo("""{"n":1}""");
        var after = await source.HeadAsync(tenant, incarnation, CancellationToken.None) ?? throw new InvalidOperationException("the copy refused its own incarnation");
        await Assert.That(after).IsGreaterThan(head);
        await Assert.That(await ShippedAsync(source, tenant, incarnation, head, written)).IsTrue();

        // The next write opens a fresh session, with capture on.
        var next = EntityIds.New().ToString();
        await Assert.That(await PutAsync(data, managed, next, 2)).IsEqualTo(HttpStatusCode.Created);
        await Assert.That(store.CurrentWriter()).IsNotNull();
        await Assert.That(store.CurrentWriter()).IsNotSameReferenceAs(session);
        await Assert.That(await ShippedAsync(source, tenant, incarnation, after, next)).IsTrue();

        // A transaction that did not commit still fails when its reset fails, and leaves nothing.
        var rolledBack = EntityIds.New().ToString();
        var transaction = await store.BeginTransactionAsync(write: true, CancellationToken.None);
        _ = await transaction.PutAsync("items", rolledBack, Encoding.UTF8.GetBytes("""{"n":3}"""), CancellationToken.None);
        var discarded = store.CurrentWriter();
        Volatile.Write(ref failing, 1);
        await Assert.That(async () => await transaction.DisposeAsync()).ThrowsException();
        Volatile.Write(ref failing, 0);
        await Assert.That(discarded!.Connection.State).IsEqualTo(System.Data.ConnectionState.Closed);
        await Assert.That(await GetAsync(data, managed, rolledBack)).IsNull();
    }

    private static Uri Document(ManagedDatabaseRef database, string id) => ManagementApi.Uri($"{database.Data}/collections/items/documents/by-key?id={id}");

    private static async Task<HttpStatusCode> PutAsync(HttpClient data, ManagedDatabaseRef database, string id, int n)
    {
        using var response = await data.PutAsJsonAsync(Document(database, id), new { n });
        return response.StatusCode;
    }

    private static async Task<string?> GetAsync(HttpClient data, ManagedDatabaseRef database, string id)
    {
        using var response = await data.GetAsync(Document(database, id));
        return response.StatusCode == HttpStatusCode.OK
            ? JsonElement.Parse(await response.Content.ReadAsStringAsync()).GetProperty("document").GetRawText()
            : null;
    }

    // Whether the document's write is in this machine's change groups after `after`: what the peer streams send.
    private static async Task<bool> ShippedAsync(IChangeGroupSource source, TenantContext database, Ulid incarnation, long after, string id) =>
        (await source.ReadAsync(database, incarnation, after, ChangeGroups.MaxGroups, ChangeGroups.MaxBatchBytes, CancellationToken.None)
            ?? throw new InvalidOperationException("the copy refused its own incarnation")).Groups
            .SelectMany(group => group.Deltas).Any(delta => delta.Id == id);
}
