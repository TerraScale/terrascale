using System.Net;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using TerraBase.Engine.Turso;
using TerraBase.Hosting;
using TerraBase.Primitives;
using TerraBase.Tenancy;
using TerraBase.Testing;

namespace TerraBase.Slow.Storage;

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
        var engine = api.Server.Services.GetRequiredService<TursoStorageEngine>();
        var store = (TursoDatabaseStore)(await engine.OpenDatabaseAsync(tenant, CancellationToken.None))!;
        Volatile.Write(ref target, tenant.DatabaseId);
        await Assert.That(await PutAsync(data, managed, EntityIds.New().ToString(), 0)).IsEqualTo(HttpStatusCode.Created);
        var session = store.CurrentWriter() ?? throw new InvalidOperationException("no writer session after a write");
        var incarnation = store.Incarnation;
        var head = await CapturedWrites.HeadAsync(store, CancellationToken.None);
        var written = EntityIds.New().ToString();
        Volatile.Write(ref failing, 1);
        await Assert.That(await PutAsync(data, managed, written, 1)).IsEqualTo(HttpStatusCode.Created);
        Volatile.Write(ref failing, 0);
        await Assert.That(session.Connection.State).IsEqualTo(System.Data.ConnectionState.Closed);
        await Assert.That(store.CurrentWriter()).IsNotSameReferenceAs(session);
        await Assert.That(store.Incarnation).IsEqualTo(incarnation);
        await Assert.That(await GetAsync(data, managed, written)).IsEqualTo("""{"n":1}""");
        var after = await CapturedWrites.HeadAsync(store, CancellationToken.None);
        await Assert.That(after).IsGreaterThan(head);
        await Assert.That(await CapturedAsync(store, head, written)).IsTrue();
        var next = EntityIds.New().ToString();
        await Assert.That(await PutAsync(data, managed, next, 2)).IsEqualTo(HttpStatusCode.Created);
        await Assert.That(store.CurrentWriter()).IsNotNull();
        await Assert.That(store.CurrentWriter()).IsNotSameReferenceAs(session);
        await Assert.That(await CapturedAsync(store, after, next)).IsTrue();
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

    private static async Task<bool> CapturedAsync(TursoDatabaseStore store, long after, string id) =>
        (await CapturedWrites.ReadAsync(store, after, CancellationToken.None)).Any(commit => commit.Ids.Contains(id));
}
