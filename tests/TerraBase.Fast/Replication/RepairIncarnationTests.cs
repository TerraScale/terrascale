using System.Collections;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Time.Testing;
using TerraBase.Engine;
using TerraBase.Engine.Lattices;
using TerraBase.Engine.Sqlite;
using TerraBase.Features.Replication;
using TerraBase.Tenancy;
using TerraBase.Testing;
using Turso.Data.Sqlite;

namespace TerraBase.Fast.Replication;

public sealed class RepairIncarnationTests
{
    [Test]
    [Arguments(-1)]
    [Arguments(0)]
    [Arguments(IStorageTransaction.MaxChangeGroupChanges)]
    public async Task Repair_refuses_a_replacement_before_joining_each_chunk(int replaceAfter)
    {
        var token = TestContext.Current!.Execution.CancellationToken;
        await using var api = await ManagementApi.StartAsync(time: new FakeTimeProvider());
        var managed = await api.CreateManagedDatabaseAsync();
        var database = new TenantContext(managed.Organization, PlanTier.Hobby, managed.Project, managed.Id);
        var store = (SqliteDatabaseStore)(await api.Server.Services.GetRequiredService<SqliteStorageEngine>().OpenDatabaseAsync(database, token))!;
        var incarnation = store.Incarnation;
        var replacement = Ulid.NewUlid();
        using var body = JsonDocument.Parse("""{"value":"repair"}""");
        var state = DocumentState.Bottom.Put(new(new Hlc(1000), ReplicaId.Of(99, 1, 0)), body.RootElement);
        var rows = Enumerable.Range(0, IStorageTransaction.MaxChangeGroupChanges + 1)
            .Select(_ => new RepairState("items", Ulid.NewUlid().ToString(), state)).ToArray();
        var replacements = 0;
        var states = new ReplacingStates(rows, replaceAfter, () =>
        {
            // A file replacement changes persisted identity while the open store still caches the original one.
            using var connection = new SqliteConnection($"Data Source={store.FilePath}");
            connection.Open();
            using var command = connection.CreateCommand();
            command.CommandText = "UPDATE commit_offset SET incarnation = $incarnation WHERE id = 1;";
            command.Parameters.AddWithValue("$incarnation", replacement.ToString());
            replacements += command.ExecuteNonQuery();
        });

        var result = await api.Server.Services.GetRequiredService<IChangeGroupSink>()
            .RepairAsync(database, replaceAfter < 0 ? replacement : incarnation, states, token);

        await Assert.That(result).IsNull();
        await Assert.That(replacements).IsEqualTo(replaceAfter < 0 ? 0 : 1);
        await Assert.That(store.Incarnation).IsEqualTo(incarnation);
        await using var read = await store.BeginTransactionAsync(write: false, token);
        await Assert.That(await read.GetIncarnationAsync(token)).IsEqualTo(replaceAfter < 0 ? incarnation : replacement);
        await Assert.That(await read.GetDocumentBytesAsync("items", rows[^1].Id, token)).IsEqualTo(0);
        await Assert.That(await read.GetDocumentBytesAsync("items", rows[0].Id, token) > 0).IsEqualTo(replaceAfter > 0);
    }

    private sealed class ReplacingStates(RepairState[] rows, int replaceAfter, Action replace) : IReadOnlyList<RepairState>
    {
        public int Count => rows.Length;

        public RepairState this[int index] => rows[index];

        public IEnumerator<RepairState> GetEnumerator()
        {
            for (var index = 0; index < rows.Length; index++)
            {
                if (index == replaceAfter)
                {
                    replace();
                }

                yield return rows[index];
            }
        }

        IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();
    }
}
