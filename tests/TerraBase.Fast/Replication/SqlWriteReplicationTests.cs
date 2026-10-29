using System.Text.Json;
using Bogus;
using Microsoft.Extensions.DependencyInjection;
using TerraBase.Engine.Cluster;
using TerraBase.Features.Replication;
using TerraBase.Primitives;
using TerraBase.Tenancy;
using TerraBase.Testing;

namespace TerraBase.Fast.Replication;

public sealed class SqlWriteReplicationTests
{
    private const string Select = "SELECT id, name, age FROM people ORDER BY id";

    [Test]
    public async Task An_atomic_create_insert_drop_frees_the_name_in_every_region()
    {
        await using var cell = await Cell.StartAsync();
        await OkAsync(cell.Gru,
            Statement("CREATE TABLE people (id INTEGER PRIMARY KEY, name TEXT, age INTEGER)"),
            Statement("INSERT INTO people VALUES (1, 'old', 1)"),
            Statement("DROP TABLE people"));
        await OkAsync(cell.Gru,
            Statement("CREATE TABLE barrier (id INTEGER PRIMARY KEY)"),
            Statement("INSERT INTO barrier VALUES (1)"));
        // Each origin's change groups arrive in order; observing this later table fences the preceding drop.
        await RowsEverywhereAsync(cell, "[[1]]", "SELECT id FROM barrier");
        await RowsEverywhereAsync(cell, "[]", "SELECT name FROM sqlite_master WHERE name = 'people'");
        await OkAsync(cell.Sjc, Statement("CREATE TABLE people (id INTEGER PRIMARY KEY, fresh TEXT)"));
        await RowsEverywhereAsync(cell, "[]", "SELECT id, fresh FROM people");
        await OkAsync(cell.Fra, Statement("INSERT INTO people VALUES (2, 'new')"));
        await RowsEverywhereAsync(cell, """[[2,"new"]]""", "SELECT id, fresh FROM people");
        // Reusing the name on the other regions must also succeed, after each replicated drop.
        foreach (var machine in new[] { cell.Fra, cell.Gru })
        {
            await OkAsync(machine, Statement("DROP TABLE people"));
            await RowsEverywhereAsync(cell, "[]", "SELECT name FROM sqlite_master WHERE name = 'people'");
            await OkAsync(machine, Statement("CREATE TABLE people (id INTEGER PRIMARY KEY, fresh TEXT)"));
            await RowsEverywhereAsync(cell, "[]", "SELECT id, fresh FROM people");
        }
    }

    [Test]
    [Arguments(false)]
    [Arguments(true)]
    public async Task An_atomic_recreation_ships_its_drop_barrier_with_the_new_definition(bool alreadyReplicated)
    {
        await using var cell = await Cell.StartAsync();
        var create = Statement("CREATE TABLE people (id INTEGER PRIMARY KEY, name TEXT, age INTEGER)");
        var insert = Statement("INSERT INTO people VALUES (1, 'old', 1)");
        if (alreadyReplicated)
        {
            await OkAsync(cell.Gru, create, insert);
            await RowsEverywhereAsync(cell, """[[1,"old",1]]""");
        }

        object[] recreate =
        [
            Statement("DROP TABLE people"),
            Statement("CREATE TABLE people (id INTEGER PRIMARY KEY, fresh TEXT)"),
            Statement("INSERT INTO people VALUES (2, 'new')"),
        ];
        await OkAsync(cell.Gru, alreadyReplicated ? recreate : [create, insert, .. recreate]);
        await RowsEverywhereAsync(cell, """[[2,"new"]]""", "SELECT id, fresh FROM people ORDER BY id");
    }

    [Test]
    public async Task Sql_inserts_updates_and_deletes_in_gru_and_sjc_are_read_with_sql_in_every_region()
    {
        await using var cell = await Cell.StartAsync();
        // Exercise JSON escaping deterministically as well as Bogus names such as D'angelo.
        var names = new Faker().Make(4, index => index == 0 ? "D'angelo \"quoted\" \\ é <&>" : new Faker().Name.FirstName());
        await OkAsync(cell.Gru, Statement("CREATE TABLE people (id INTEGER PRIMARY KEY, name TEXT NOT NULL, age INTEGER)"));
        await OkAsync(cell.Gru, [.. names.Take(3).Select((name, i) => Statement("INSERT INTO people (id, name, age) VALUES (?, ?, ?)", i + 1, name, 20 + i * 10))]);
        await RowsEverywhereAsync(cell, JsonSerializer.Serialize(new object[][]
        {
            [1, names[0], 20], [2, names[1], 30], [3, names[2], 40],
        }));

        await OkAsync(cell.Gru, Statement("UPDATE people SET age = age + 1 WHERE id = ?", 1), Statement("DELETE FROM people WHERE id = ?", 2));
        await OkAsync(cell.Sjc, Statement("INSERT INTO people (id, name, age) VALUES (?, ?, 50)", 4, names[3]));
        await RowsEverywhereAsync(cell, JsonSerializer.Serialize(new object[][]
        {
            [1, names[0], 21], [3, names[2], 40], [4, names[3], 50],
        }));
    }

    [Test]
    public async Task Concurrent_sql_updates_of_one_row_in_gru_and_sjc_converge_to_one_of_them_everywhere()
    {
        await using var cell = await Cell.StartAsync();
        await OkAsync(cell.Gru, Statement("CREATE TABLE people (id INTEGER PRIMARY KEY, name TEXT NOT NULL, age INTEGER)"), Statement("INSERT INTO people VALUES (1, 'base', 0)"));
        await RowsEverywhereAsync(cell, """[[1,"base",0]]""");
        for (var round = 0; round < 5; round++)
        {
            await Task.WhenAll(
                OkAsync(cell.Gru, Statement("UPDATE people SET name = ?, age = ? WHERE id = 1", $"gru-{round}", round)),
                OkAsync(cell.Sjc, Statement("UPDATE people SET name = ?, age = ? WHERE id = 1", $"sjc-{round}", round)));

            // Every region ends with the same row, and its name is one of the two writes.
            string?[] read = [];
            await TerraBaseTestServer.EventuallyAsync(async _ =>
            {
                read = await Task.WhenAll(cell.Machines.Select(machine => RowsAsync(machine, Select)));
                return read[0] is not null && read.Distinct().Count() == 1;
            }, timeout: TimeSpan.FromSeconds(30));
            await Assert.That(read[0]).IsIn([$"""[[1,"gru-{round}",{round}]]""", $"""[[1,"sjc-{round}",{round}]]"""]).Because($"round {round}: {string.Join(" | ", read)}");
        }
    }

    private static object Statement(string sql, params object[] args) => new { sql, args };

    private static async Task OkAsync(Machine machine, params object[] statements)
    {
        using var response = await machine.Data.PostAsJsonAsync(ManagementApi.Uri($"{machine.Cell.Managed.Data}/sql/execute"), new { statements });
        await Assert.That(response.IsSuccessStatusCode).IsTrue().Because($"{machine.Region}: {await response.Content.ReadAsStringAsync()}");
    }

    // The people rows as JSON on `machine`, or null while it cannot read the table yet.
    private static async Task<string?> RowsAsync(Machine machine, string select)
    {
        using var response = await machine.Data.PostAsJsonAsync(ManagementApi.Uri($"{machine.Cell.Managed.Data}/sql/execute"), new { statements = new[] { new { sql = select } } });
        return response.IsSuccessStatusCode
            ? JsonElement.Parse(await response.Content.ReadAsStringAsync()).GetProperty("results")[0].GetProperty("rows").GetRawText()
            : null;
    }

    private static async Task RowsEverywhereAsync(Cell cell, string expected, string select = Select)
    {
        string?[] read = [];
        await TerraBaseTestServer.EventuallyAsync(async _ =>
        {
            read = await Task.WhenAll(cell.Machines.Select(machine => RowsAsync(machine, select)));
            return read.All(rows => rows == expected);
        }, timeout: TimeSpan.FromSeconds(30));
        await Assert.That(read.All(rows => rows == expected)).IsTrue().Because(string.Join(" | ", read));
    }

    // gru, sjc and fra replicating one database through their CDC tailers, each serving the public APIs.
    internal sealed class Cell : IAsyncDisposable
    {
        public FakeMemberStore Members { get; } = new();

        public string User { get; } = EntityIds.New().ToString();

        public TenantContext Database { get; } = new(EntityIds.New().ToString(), PlanTier.Hobby, EntityIds.New().ToString(), EntityIds.New().ToString());

        public ManagedDatabaseRef Managed => new(Database.OrganizationId, Database.ProjectId!, Database.DatabaseId!, "main");

        public Machine Gru { get; private set; } = null!;

        public Machine Sjc { get; private set; } = null!;

        public Machine Fra { get; private set; } = null!;

        public Machine[] Machines => [Gru, Sjc, Fra];

        public static async Task<Cell> StartAsync()
        {
            var cell = new Cell();
            try
            {
                cell.Gru = new Machine(cell, "gru", ["sjc", "fra"]);
                cell.Sjc = new Machine(cell, "sjc", ["gru", "fra"]);
                cell.Fra = new Machine(cell, "fra", ["gru", "sjc"]);
                foreach (var machine in cell.Machines)
                {
                    await machine.StartAsync();
                }

                foreach (var machine in cell.Machines)
                {
                    await machine.ConnectedAsync();
                }

                return cell;
            }
            catch
            {
                await cell.DisposeAsync();
                throw;
            }
        }

        public async ValueTask DisposeAsync()
        {
            foreach (var machine in new[] { Fra, Sjc, Gru })
            {
                if (machine is not null)
                {
                    await machine.DisposeAsync();
                }
            }
        }
    }

    internal sealed class Machine(Cell cell, string region, string[] replicas) : IAsyncDisposable
    {
        private readonly string _directory = Directory.CreateTempSubdirectory("terrabase-sql-replication-").FullName;
        private readonly FakeS3 _s3 = new();
        private ManagementApi? _api;
        private HttpClient? _data;

        public Cell Cell => cell;

        public string Region => region;

        public HttpClient Data => _data ?? throw new InvalidOperationException($"{region} is stopped");

        internal ManagementApi Api => _api ?? throw new InvalidOperationException($"{region} is stopped");

        public async Task StartAsync()
        {
            _api = await ManagementApi.StartAsync(settings =>
            {
                _s3.Configure(settings);
                settings["region"] = region;
                settings["node-name"] = Convert.ToHexStringLower(Guid.NewGuid().ToByteArray())[..14];
                settings["data-directory"] = _directory;
            }, services =>
            {
                _s3.Route(services);
                ManualBackupHosting.Configure(services);
                services.AddSingleton<IMemberDirectory>(cell.Members);
            }, userId: cell.User);
            await RegisterAsync(_api);
            var gate = _api.Server.Services.GetRequiredService<ICatalogGate>();
            await TerraBaseTestServer.EventuallyAsync(_ => Task.FromResult(gate.IsOpen), timeout: TimeSpan.FromSeconds(30));
            _data = await _api.CreateClientAsync(cell.Database.OrganizationId, "documents:read", "documents:write");
        }

        // A write is admitted only while a stream to another placed region is connected: wait for every one.
        public Task ConnectedAsync() =>
            TerraBaseTestServer.EventuallyAsync(_ =>
            {
                var peers = Api.Server.Services.GetRequiredService<IReplicationStatus>().Snapshot().Peers.Select(peer => peer.Region).ToHashSet(StringComparer.Ordinal);
                return Task.FromResult(replicas.All(peers.Contains));
            }, timeout: TimeSpan.FromSeconds(30));

        public async ValueTask DisposeAsync()
        {
            try
            {
                _data?.Dispose();
                if (_api is not null)
                {
                    await _api.DisposeAsync();
                }
            }
            finally
            {
                Directory.Delete(_directory, recursive: true);
            }
        }

        private async Task RegisterAsync(ManagementApi api)
        {
            var database = cell.Database;
            var commit = JsonSerializer.Serialize(new
            {
                id = EntityIds.New().ToString(),
                ops = new object[]
                {
                    new { op = "create_organization", id = database.OrganizationId, name = $"sql-replication-{database.OrganizationId}" },
                    new { op = "create_project", organization = database.OrganizationId, id = database.ProjectId, name = "sql-replication" },
                    new { op = "create_database", organization = database.OrganizationId, project = database.ProjectId, name = "main", id = database.DatabaseId, replica_regions = replicas },
                },
            });
            using var service = api.Service(api.User);
            using var response = await service.PostAsync(new Uri("/v1/management/commits", UriKind.Relative), new StringContent(commit, Encoding.UTF8, "application/json"));
            await Assert.That(response.IsSuccessStatusCode).IsTrue().Because(await response.Content.ReadAsStringAsync());
        }
    }
}
