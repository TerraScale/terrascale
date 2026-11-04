using System.Net;
using System.Text.Json;
using Bogus;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Time.Testing;
using TerraBase.Engine.Revisions;
using TerraBase.Engine.Turso;
using TerraBase.Features.Backup;
using TerraBase.Primitives;
using TerraBase.Tenancy;
using TerraBase.Testing;

namespace TerraBase.Fast.Backup;

public sealed class SqlTableBackupTests
{
    private static readonly Faker Faker = new();

    [Test]
    public async Task Sql_tables_restore_to_a_point_in_time_with_their_rows_schema_constraints_and_triggers(CancellationToken cancellationToken)
    {
        await using var host = await SqlBackupHost.StartAsync(cancellationToken);
        var (first, second, third) = (Faker.Internet.Email(), Faker.Internet.Email(), Faker.Internet.Email());
        await host.OkAsync(
            "CREATE TABLE teams (id TEXT PRIMARY KEY, name TEXT NOT NULL)",
            "CREATE TABLE players (id TEXT PRIMARY KEY, team TEXT NOT NULL REFERENCES teams (id) ON DELETE CASCADE, email TEXT UNIQUE, goals INTEGER NOT NULL CHECK (goals >= 0), profile TEXT)",
            "CREATE INDEX players_team ON players (team)",
            "CREATE VIEW scorers AS SELECT id, goals FROM players WHERE goals > 0",
            "CREATE TABLE audit (id INTEGER PRIMARY KEY, player TEXT NOT NULL)",
            "CREATE TRIGGER players_audit AFTER INSERT ON players BEGIN INSERT INTO audit (player) VALUES (new.id); END");
        await host.OkAsync(
            Stmt("INSERT INTO teams VALUES ('red', ?), ('blue', ?)", Faker.Company.CompanyName(), Faker.Company.CompanyName()),
            Stmt("INSERT INTO players VALUES ('p1', 'red', ?, 0, ?)", first, """{"city":"Recife"}"""),
            Stmt("INSERT INTO players VALUES ('p2', 'blue', ?, 2, ?)", second, """{"city":"Porto"}"""));
        await host.SnapshotAsync(cancellationToken);
        var k = host.SnapshotAt.AddSeconds(2);

        await host.OkAsync(
            Stmt("UPDATE teams SET name = ? WHERE id = 'red'", Faker.Company.CompanyName()),
            Stmt("UPDATE players SET goals = 5, profile = json_set(profile, '$.city', 'Natal') WHERE id = 'p1'"),
            Stmt("INSERT INTO players VALUES ('p3', 'red', ?, 1, NULL)", third));
        await host.StampAsync(k.AddSeconds(-1), cancellationToken);
        await host.OkAsync(Stmt("DELETE FROM teams WHERE id = 'blue'"), Stmt("INSERT INTO players VALUES ('p4', 'red', NULL, 7, NULL)"));
        await host.StampAsync(k, cancellationToken);
        await host.ShipAsync(TimeSpan.FromMilliseconds(2500), cancellationToken);

        var atK = await host.RestoreAsync(k, "at-k", cancellationToken);

        await Assert.That(await host.RowsAsync(atK, "SELECT id, team, goals, profile ->> '$.city' FROM players ORDER BY id"))
            .IsEquivalentTo(["p1|red|5|Natal", "p2|blue|2|Porto", "p3|red|1|"]);
        await Assert.That(await host.RowsAsync(atK, "SELECT player FROM audit ORDER BY id")).IsEquivalentTo(["p1", "p2", "p3"]);
        await Assert.That(await host.RowsAsync(atK, "SELECT type, name FROM sqlite_schema WHERE name IN ('players_team', 'scorers', 'players_audit') ORDER BY name"))
            .IsEquivalentTo(["trigger|players_audit", "index|players_team", "view|scorers"]);

        await Assert.That(await host.RefusedAsync(atK, Stmt("INSERT INTO players VALUES ('p9', 'green', NULL, 0, NULL)"))).IsEqualTo("foreign_key_violation");
        await Assert.That(await host.RefusedAsync(atK, Stmt("INSERT INTO players VALUES ('p9', 'red', ?, 0, NULL)", first))).IsEqualTo("unique_violation");
        await Assert.That(await host.RefusedAsync(atK, Stmt("UPDATE players SET goals = -1 WHERE id = 'p1'"))).IsEqualTo("check_violation");
        await host.OkInAsync(atK, Stmt("INSERT INTO players VALUES ('p5', 'blue', NULL, 0, NULL)"), Stmt("DELETE FROM teams WHERE id = 'red'"));
        await Assert.That(await host.RowsAsync(atK, "SELECT id FROM players ORDER BY id")).IsEquivalentTo(["p2", "p5"]);
        await Assert.That(await host.RowsAsync(atK, "SELECT player FROM audit ORDER BY id")).IsEquivalentTo(["p1", "p2", "p3", "p5"]);
        await Assert.That(await host.RowsAsync(host.Database.Name, "SELECT id FROM players ORDER BY id")).IsEquivalentTo(["p1", "p3", "p4"]);
    }

    [Test]
    public async Task A_database_rewinds_in_place_to_a_time_keeping_its_identity_and_backing_up_the_rewind(CancellationToken cancellationToken)
    {
        await using var host = await SqlBackupHost.StartAsync(cancellationToken);
        await host.OkAsync(
            "CREATE TABLE teams (id TEXT PRIMARY KEY, name TEXT NOT NULL)",
            "CREATE TABLE players (id TEXT PRIMARY KEY, team TEXT NOT NULL REFERENCES teams (id) ON DELETE CASCADE)",
            "CREATE TABLE audit (id INTEGER PRIMARY KEY, player TEXT NOT NULL)",
            "CREATE TRIGGER players_audit AFTER INSERT ON players BEGIN INSERT INTO audit (player) VALUES (new.id); END");
        await host.OkAsync(Stmt("INSERT INTO teams VALUES ('red', ?)", Faker.Company.CompanyName()), Stmt("INSERT INTO players VALUES ('p1', 'red')"));
        await host.SnapshotAsync(cancellationToken);
        var k = host.SnapshotAt.AddSeconds(2);
        await host.OkAsync(Stmt("INSERT INTO players VALUES ('p2', 'red')"));
        await host.StampAsync(k.AddSeconds(-1), cancellationToken);
        await host.OkAsync("DROP TABLE players", "CREATE TABLE later (id TEXT PRIMARY KEY)", "INSERT INTO later VALUES ('x')");
        await host.StampAsync(k, cancellationToken);
        await host.ShipAsync(TimeSpan.FromMilliseconds(2500), cancellationToken);

        using var rewound = await host.Api.Client.PostAsJsonAsync(ManagementApi.Uri($"{host.Database.Path}/point-in-time/rewind"), new { pointInTime = k }, cancellationToken);
        var body = await rewound.Content.ReadAsStringAsync(cancellationToken);
        await Assert.That(rewound.StatusCode).IsEqualTo(HttpStatusCode.OK).Because(body);
        await host.OkAsync(Stmt("INSERT INTO players VALUES ('p3', 'red')"));
        using var databases = JsonDocument.Parse(await host.Api.Client.GetStringAsync(ManagementApi.Uri($"{host.Database.ProjectPath}/databases"), cancellationToken));

        await Assert.That(JsonElement.Parse(body).GetProperty("id").GetString()).IsEqualTo(host.Database.Id);
        await Assert.That(await host.RowsAsync(host.Database.Name, "SELECT id FROM players ORDER BY id")).IsEquivalentTo(["p1", "p2", "p3"]);
        await Assert.That(await host.RowsAsync(host.Database.Name, "SELECT player FROM audit ORDER BY id")).IsEquivalentTo(["p1", "p2", "p3"]);
        await Assert.That(await host.RowsAsync(host.Database.Name, "SELECT name FROM sqlite_schema WHERE name = 'later'")).IsEmpty();
        await Assert.That(await host.RefusedAsync(host.Database.Name, Stmt("INSERT INTO players VALUES ('p9', 'green')"))).IsEqualTo("foreign_key_violation");
        await Assert.That(databases.RootElement.GetProperty("items").GetArrayLength()).IsEqualTo(1);
        await host.ShipAsync(TimeSpan.FromSeconds(1), cancellationToken);
        var undone = await host.RestoreAsync(await host.LatestAsync(cancellationToken), "after-rewind", cancellationToken);
        await Assert.That(await host.RowsAsync(undone, "SELECT id FROM players ORDER BY id")).IsEquivalentTo(["p1", "p2", "p3"]);
    }

    [Test]
    public async Task A_manual_backup_restores_sql_tables_as_they_were_when_it_was_taken(CancellationToken cancellationToken)
    {
        await using var host = await SqlBackupHost.StartAsync(cancellationToken);
        await host.OkAsync("CREATE TABLE items (id TEXT PRIMARY KEY, v TEXT NOT NULL)", "CREATE UNIQUE INDEX items_v ON items (v)");
        await host.OkAsync(Stmt("INSERT INTO items VALUES ('kept', ?)", Faker.Lorem.Word()));

        using var created = await host.Api.Client.PostAsync(ManagementApi.Uri($"{host.Database.Path}/backups"), content: null, cancellationToken);
        var backupId = (await created.Content.ReadFromJsonAsync<JsonElement>(cancellationToken)).GetProperty("id").GetString();
        await host.OkAsync(Stmt("INSERT INTO items VALUES ('later', ?)", Faker.Lorem.Sentence()), Stmt("DELETE FROM items WHERE id = 'kept'"));
        using var restored = await host.Api.Client.PostAsJsonAsync(ManagementApi.Uri($"{host.Database.Path}/backups/{backupId}/restore"), new { name = "manual" }, cancellationToken);

        await Assert.That(created.StatusCode).IsEqualTo(HttpStatusCode.Created);
        await Assert.That(restored.StatusCode).IsEqualTo(HttpStatusCode.Created);
        await Assert.That(await host.RowsAsync("manual", "SELECT id FROM items")).IsEquivalentTo(["kept"]);
        await Assert.That(await host.RowsAsync("manual", "SELECT name FROM sqlite_schema WHERE type = 'index' AND name = 'items_v'")).IsEquivalentTo(["items_v"]);
        await Assert.That(await host.RowsAsync(host.Database.Name, "SELECT id FROM items")).IsEquivalentTo(["later"]);
    }

    [Test]
    public async Task A_replica_rebootstraps_after_a_rewind_and_matches_the_rewound_database(CancellationToken cancellationToken)
    {
        await using var host = await SqlBackupHost.StartAsync(cancellationToken);
        await host.OkAsync("CREATE TABLE items (id TEXT PRIMARY KEY, v TEXT NOT NULL)");
        var rows = Enumerable.Range(0, 40).Select(_ => (Id: EntityIds.New().ToString(), Value: Faker.Lorem.Sentence(20))).ToList();
        await host.OkAsync([.. rows.Select(row => Stmt("INSERT INTO items VALUES (?, ?)", row.Id, row.Value))]);
        await host.SnapshotAsync(cancellationToken);
        var k = host.SnapshotAt.AddSeconds(2);
        await host.OkAsync(Stmt("DELETE FROM items WHERE id = ?", rows[0].Id));
        await host.StampAsync(k.AddSeconds(-1), cancellationToken);
        await host.OkAsync("DELETE FROM items");
        await host.StampAsync(k, cancellationToken);
        await host.ShipAsync(TimeSpan.FromMilliseconds(2500), cancellationToken);
        var replica = new RevisionReplica();
        var before = await replica.PullAsync(host.Api.Server, host.Tenant);

        using var rewound = await host.Api.Client.PostAsJsonAsync(ManagementApi.Uri($"{host.Database.Path}/point-in-time/rewind"), new { pointInTime = k }, cancellationToken);
        var after = await replica.PullAsync(host.Api.Server, host.Tenant);
        var steady = await replica.PullAsync(host.Api.Server, host.Tenant);

        await Assert.That(rewound.StatusCode).IsEqualTo(HttpStatusCode.OK).Because(await rewound.Content.ReadAsStringAsync(cancellationToken));
        await Assert.That(after.Full).IsEqualTo(FullReason.OtherIncarnation);
        await Assert.That(after.Revision.Incarnation).IsNotEqualTo(before.Revision.Incarnation);
        await Assert.That(steady.Full).IsNull();
        await Assert.That(replica.Rows()).IsEquivalentTo(rows.Skip(1).ToDictionary(row => row.Id, row => row.Value));
    }

    private static object Stmt(string sql, params object?[] args) => new { sql, args };

    private sealed class SqlBackupHost(ManagementApi api, FakeTimeProvider time, ManagedDatabaseRef database, HttpClient data) : IAsyncDisposable
    {
        private long _stamped;

        public ManagementApi Api { get; } = api;

        public ManagedDatabaseRef Database { get; } = database;

        public DateTimeOffset SnapshotAt { get; private set; }

        public TenantContext Tenant => new(Database.Organization, PlanTier.Pro, Database.Project, Database.Id);

        private IContinuousBackup Backup => Api.Server.Services.GetRequiredService<IContinuousBackup>();

        public static async Task<SqlBackupHost> StartAsync(CancellationToken cancellationToken)
        {
            var s3 = new FakeS3();
            var time = new FakeTimeProvider(new DateTimeOffset(2026, 10, 7, 12, 0, 0, TimeSpan.Zero));
            var api = await ManagementApi.StartAsync(s3.Configure, services =>
            {
                s3.Route(services);
                ManualBackupHosting.Configure(services);
            }, time);
            try
            {
                Directory.CreateDirectory(Path.Combine(api.Server.DataDirectory, ".backup"));
                var database = await api.CreateManagedDatabaseAsync("main");
                await api.SetPlanAsync(api.User, database.Organization, "pro", cancellationToken);
                var key = await api.Server.IssueNarrowedAsync(database.Organization, database.Project, null, "sql:read", "sql:write");
                return new SqlBackupHost(api, time, database, api.DataClient(key));
            }
            catch
            {
                await api.DisposeAsync();
                throw;
            }
        }

        public async Task SnapshotAsync(CancellationToken cancellationToken)
        {
            await ShipAsync(TimeSpan.FromSeconds(10), cancellationToken);
            SnapshotAt = time.GetUtcNow();
            await StampAsync(null, cancellationToken);
        }

        public async Task ShipAsync(TimeSpan advance, CancellationToken cancellationToken)
        {
            time.Advance(advance);
            await Backup.RunOnceAsync(cancellationToken);
        }

        public async Task StampAsync(DateTimeOffset? commit, CancellationToken cancellationToken)
        {
            var store = (TursoDatabaseStore)(await Api.Server.Services.GetRequiredService<TursoStorageEngine>().OpenDatabaseAsync(Tenant, cancellationToken))!;
            _stamped = await store.WithWriterAsync(writer =>
            {
                List<long> rows = [];
                using (var select = writer.Connection.CreateCommand())
                {
                    select.CommandText = "SELECT change_id FROM turso_cdc WHERE change_id > $after ORDER BY change_id;";
                    select.Parameters.AddWithValue("$after", _stamped);
                    using var reader = select.ExecuteReader();
                    while (reader.Read())
                    {
                        rows.Add(reader.GetInt64(0));
                    }
                }

                if (commit is { } at)
                {
                    Execute(writer, TursoSession.CaptureOff);
                    try
                    {
                        using var update = writer.Connection.CreateCommand();
                        update.CommandText = "UPDATE turso_cdc SET change_time = $time WHERE change_id > $after;";
                        update.Parameters.AddWithValue("$time", at.ToUnixTimeSeconds());
                        update.Parameters.AddWithValue("$after", _stamped);
                        update.ExecuteNonQuery();
                    }
                    finally
                    {
                        Execute(writer, TursoSession.CaptureOn);
                    }
                }

                return rows.Count > 0 ? rows[^1] : _stamped;
            }, "a test", cancellationToken);
        }

        public async Task<DateTimeOffset> LatestAsync(CancellationToken cancellationToken)
        {
            using var window = await Api.Client.GetAsync(ManagementApi.Uri($"{Database.Path}/point-in-time"), cancellationToken);
            var body = await window.Content.ReadFromJsonAsync<JsonElement>(cancellationToken);
            await Assert.That(window.StatusCode).IsEqualTo(HttpStatusCode.OK).Because(body.GetRawText());
            return body.GetProperty("latest").GetDateTimeOffset();
        }

        public async Task<string> RestoreAsync(DateTimeOffset at, string name, CancellationToken cancellationToken)
        {
            using var response = await Api.Client.PostAsJsonAsync(ManagementApi.Uri($"{Database.Path}/point-in-time/restore"), new { pointInTime = at, name }, cancellationToken);
            var body = await response.Content.ReadAsStringAsync(cancellationToken);
            await Assert.That(response.StatusCode).IsEqualTo(HttpStatusCode.Created).Because(body);
            return name;
        }

        public Task<JsonElement[]> OkAsync(params string[] statements) => OkInAsync(Database.Name, [.. statements.Select(sql => Stmt(sql))]);

        public Task<JsonElement[]> OkAsync(params object[] statements) => OkInAsync(Database.Name, statements);

        public async Task<JsonElement[]> OkInAsync(string name, params object[] statements)
        {
            var (status, body) = await PostAsync(name, statements);
            await Assert.That(status).IsEqualTo(HttpStatusCode.OK).Because(body.GetRawText());
            return [.. body.GetProperty("results").EnumerateArray()];
        }

        public async Task<string?> RefusedAsync(string name, params object[] statements)
        {
            var (status, body) = await PostAsync(name, statements);
            await Assert.That(status).IsNotEqualTo(HttpStatusCode.OK).Because(body.GetRawText());
            return body.GetProperty("error").TryGetProperty("code", out var code) ? code.GetString() : null;
        }

        public async Task<List<string>> RowsAsync(string name, string sql)
        {
            var results = await OkInAsync(name, Stmt(sql));
            return [.. results[0].GetProperty("rows").EnumerateArray().Select(row => string.Join('|', row.EnumerateArray().Select(Text)))];
        }

        public async ValueTask DisposeAsync()
        {
            data.Dispose();
            await Api.DisposeAsync();
        }

        private static void Execute(TursoSession writer, string sql)
        {
            using var command = writer.Connection.CreateCommand();
            command.CommandText = sql;
            command.ExecuteNonQuery();
        }

        private static string Text(JsonElement value) => value.ValueKind switch
        {
            JsonValueKind.Null => string.Empty,
            JsonValueKind.String => value.GetString()!,
            _ => value.GetRawText(),
        };

        private async Task<(HttpStatusCode Status, JsonElement Body)> PostAsync(string name, object[] statements)
        {
            using var response = await data.PostAsJsonAsync(ManagementApi.Uri($"/v1/projects/{Database.Project}/databases/{name}/sql/execute"), new { statements });
            return (response.StatusCode, JsonElement.Parse(await response.Content.ReadAsStringAsync()));
        }
    }
}
