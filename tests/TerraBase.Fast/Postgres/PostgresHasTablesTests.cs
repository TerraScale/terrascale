using Npgsql;
using TerraBase.Testing;
using static TerraBase.Testing.PostgresSql;

namespace TerraBase.Fast.Postgres;

public sealed class PostgresHasTablesTests
{
    // Npgsql.EntityFrameworkCore.PostgreSQL 10.0.3: NpgsqlDatabaseCreator.CreateHasTablesCommand.
    private const string HasTables = """
        SELECT CASE WHEN COUNT(*) = 0 THEN FALSE ELSE TRUE END
        FROM pg_class AS cls
        JOIN pg_namespace AS ns ON ns.oid = cls.relnamespace
        WHERE
            cls.relkind IN ('r', 'v', 'm', 'f', 'p') AND
            ns.nspname NOT IN ('pg_catalog', 'information_schema') AND
            NOT EXISTS (
                SELECT 1 FROM pg_depend WHERE
                    classid=(
                        SELECT cls.oid
                        FROM pg_class AS cls
                        JOIN pg_namespace AS ns ON ns.oid = cls.relnamespace
                        WHERE relname='pg_class' AND ns.nspname='pg_catalog'
                    ) AND
                    objid=cls.oid AND
                    deptype IN ('e', 'x')
            )
        """;

    [Test]
    public async Task Has_tables_returns_a_boolean_and_observes_uncommitted_create_drop_and_rollback()
    {
        await using var api = await ManagementApi.StartAsync();
        await using var source = await SourceAsync(api);
        await using var connection = await source.OpenConnectionAsync();
        await Assert.That(await AnyAsync(connection)).IsFalse();

        await using (var create = await connection.BeginTransactionAsync())
        {
            await ExecuteAsync(connection, "CREATE TABLE items (id INTEGER PRIMARY KEY)");
            await Assert.That(await AnyAsync(connection)).IsTrue();
            await create.RollbackAsync();
        }

        await Assert.That(await AnyAsync(connection)).IsFalse();
        await ExecuteAsync(connection, "CREATE TABLE items (id INTEGER PRIMARY KEY)");
        await using (var drop = await connection.BeginTransactionAsync())
        {
            await ExecuteAsync(connection, "DROP TABLE items");
            await Assert.That(await AnyAsync(connection)).IsFalse();
            await drop.RollbackAsync();
        }

        await Assert.That(await AnyAsync(connection)).IsTrue();
    }

    [Test]
    public async Task Prepared_has_tables_reads_at_execute_and_savepoint_rollback_restores_visibility()
    {
        await using var api = await ManagementApi.StartAsync();
        await using var source = await SourceAsync(api);
        await using var connection = await source.OpenConnectionAsync();
        await using var command = new NpgsqlCommand(HasTables, connection);
        await command.PrepareAsync();
        await using var transaction = await connection.BeginTransactionAsync();
        await transaction.SaveAsync("before_create");
        await ExecuteAsync(connection, "CREATE TABLE items (id INTEGER PRIMARY KEY)");
        await Assert.That((bool)(await command.ExecuteScalarAsync())!).IsTrue();
        await transaction.RollbackAsync("before_create");
        await Assert.That((bool)(await command.ExecuteScalarAsync())!).IsFalse();
        await transaction.CommitAsync();
    }

    [Test]
    public async Task Portal_description_does_not_freeze_the_table_existence_result()
    {
        await using var api = await ManagementApi.StartAsync();
        var database = await api.CreateManagedDatabaseAsync();
        var key = await api.Server.IssueNarrowedAsync(database.Organization, database.Project, null, "documents:read", "documents:write");
        await using var wire = await PostgresWireClient.ConnectAsync(api.Server.PostgresPort, key, database.Name);
        await wire.QueryAsync("BEGIN");
        await wire.SyncAsync(PostgresWireClient.Parse("has_tables", HasTables), PostgresWireClient.Bind("before_create", "has_tables"),
            PostgresWireClient.Describe('P', "before_create"));
        await wire.QueryAsync("CREATE TABLE items (id INTEGER PRIMARY KEY)");
        var result = await wire.SyncAsync(PostgresWireClient.Execute("before_create"));
        await Assert.That(result.Single(message => message.Type == 'D').Fields[0]).IsEqualTo("t");
        await wire.QueryAsync("ROLLBACK");
    }

    [Test]
    public async Task Similar_catalog_queries_with_different_predicates_are_refused()
    {
        await using var api = await ManagementApi.StartAsync();
        await using var source = await SourceAsync(api);
        await using var connection = await source.OpenConnectionAsync();
        await StateAsync(connection, HasTables + " AND FALSE", "0A000");
        await StateAsync(connection, HasTables.Replace("'pg_catalog'", "'PG_CATALOG'", StringComparison.Ordinal), "0A000");
        await StateAsync(connection, HasTables + char.MaxValue + " AND FALSE", "42601");
        await StateAsync(connection, "SELECT count(*) FROM pg_depend", "0A000");
        await Assert.That(await ScalarAsync(connection, "SELECT 'FROM pg_class'")).IsEqualTo("FROM pg_class");
    }

    private static async Task<bool> AnyAsync(NpgsqlConnection connection)
    {
        await using var command = new NpgsqlCommand(HasTables, connection);
        return (bool)(await command.ExecuteScalarAsync())!;
    }
}
