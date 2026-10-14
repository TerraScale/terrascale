using System.Net;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using TerraBase.Engine.Sqlite;
using TerraBase.Tenancy;
using TerraBase.Testing;

namespace TerraBase.Slow.Storage;

// Turso.Data.Sqlite 0.8.2 (SqliteCommand.Execute) leaves the native statement of a failed write to the garbage collector,
// whose finalizer then finalizes it on the finalizer thread: a second user of the connection, which Turso refuses with
// "concurrent use forbidden" when it meets the connection's owner stepping another statement.
public sealed class AbandonedStatementTests
{
    [Test]
    public async Task Failed_writes_never_leave_their_connection_to_another_thread()
    {
        await using var api = await ManagementApi.StartAsync();
        var database = await api.CreateManagedDatabaseAsync();
        await api.SetPlanAsync(api.User, database.Organization, "enterprise");
        using var data = await api.CreateClientAsync(database.Organization, "documents:read", "documents:write");
        await ManagementApi.CreateCollectionAsync(data, database, "items");
        var engine = api.Server.Services.GetRequiredService<SqliteStorageEngine>();
        var store = (SqliteDatabaseStore)(await engine.OpenDatabaseAsync(
            new TenantContext(database.Organization, PlanTier.Enterprise, database.Project, database.Id), CancellationToken.None))!;
        // A write of a document marked "fail" fails inside its INSERT, as a stopped read or a constraint fails a statement.
        // Created on a connection of its own while no write runs: the provider cannot run a trigger body.
        await store.WithWritersHeldAsync(() =>
        {
            using var connection = new Turso.TursoConnection($"Data Source={store.FilePath}");
            connection.Open();
            connection.ExecuteNonQuery(
                "CREATE TRIGGER fail_marked BEFORE INSERT ON \"items\" WHEN json_extract(NEW.doc, '$.fail') = 1 BEGIN SELECT RAISE(ROLLBACK, 'the test fails this write'); END;");
            return Task.FromResult(true);
        }, CancellationToken.None, "a test");
        var existing = Document(database, "existing");
        using (var created = await data.PutAsJsonAsync(existing, new { n = 0 }))
        {
            created.EnsureSuccessStatusCode();
        }

        // Young collections all the while: an abandoned statement is finalized as soon as it is garbage, often while the
        // writer steps another statement.
        using var stop = new CancellationTokenSource();
        var collector = Task.Run(() =>
        {
            while (!stop.IsCancellationRequested)
            {
                GC.Collect(0);
                GC.WaitForPendingFinalizers();
            }
        });
        var unexpected = new List<string>();
        try
        {
            for (var round = 0; round < 60; round++)
            {
                var failing = Enumerable.Range(0, 2).Select(i => data.PutAsJsonAsync(Document(database, $"f{round}-{i}"), new { fail = 1 })).ToList();
                var written = Enumerable.Range(0, 4).Select(i => (Id: $"w{round}-{i}", Answer: data.PutAsJsonAsync(Document(database, $"w{round}-{i}"), new { n = round })))
                    .ToList();
                var reads = Enumerable.Range(0, 2).Select(_ => data.GetAsync(existing)).ToList();
                foreach (var answer in await Task.WhenAll(failing))
                {
                    using (answer)
                    {
                        // A native failure during the commit path is outcome unknown, never a safe-to-retry refusal.
                        var body = await answer.Content.ReadFromJsonAsync<JsonElement>();
                        var code = body.GetProperty("error").GetProperty("code").GetString();
                        if (answer.StatusCode != HttpStatusCode.ServiceUnavailable || code != "write_outcome_unknown"
                            || answer.Headers.RetryAfter is not null)
                        {
                            unexpected.Add($"round {round}: a failing write answered {answer.StatusCode} {code}, Retry-After: {answer.Headers.RetryAfter}");
                        }
                    }
                }

                foreach (var (id, answer) in written)
                {
                    using var response = await answer;
                    if (!response.IsSuccessStatusCode)
                    {
                        unexpected.Add($"round {round}: write {id} answered {response.StatusCode}");
                    }
                }

                foreach (var answer in await Task.WhenAll(reads))
                {
                    using (answer)
                    {
                        if (answer.StatusCode != HttpStatusCode.OK)
                        {
                            unexpected.Add($"round {round}: a read answered {answer.StatusCode}");
                        }
                    }
                }
            }
        }
        finally
        {
            await stop.CancelAsync();
            await collector;
        }

        await Assert.That(unexpected).IsEmpty();
    }

    private static Uri Document(ManagedDatabaseRef database, string name) =>
        ManagementApi.Uri(ManagementApi.DocumentPath(database, "items", name));
}
