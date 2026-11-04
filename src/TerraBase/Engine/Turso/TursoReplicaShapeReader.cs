using TerraBase.Errors;
using TerraBase.Features.Memory;
using TerraBase.Features.Shapes;
using TerraBase.Tenancy;
using Turso;
using Turso.Data.Sqlite;

namespace TerraBase.Engine.Turso;

internal sealed class TursoReplicaShapeReader : IReplicaShapeReader
{
    private readonly TenantContext _database;
    private readonly SqliteConnection _connection;
    private readonly SqliteTransaction _transaction;
    private readonly List<SqliteCommand> _commands = [];
    private readonly TursoShapeReader _reader;

    private TursoReplicaShapeReader(TursoConnection connection, TenantContext database, TursoReplicaShapeSchema schema, long head)
    {
        _database = database;
        _connection = new SqliteConnection(connection, ownsConnection: true);
        using (var queryOnly = _connection.CreateCommand())
        {
            queryOnly.CommandText = "PRAGMA query_only = 1;";
            queryOnly.ExecuteNonQuery();
        }

        _transaction = _connection.BeginTransaction(deferred: true);
        _reader = new TursoShapeReader(Command, Command, database, _ => { }, schema, head);
        _ = _reader.ShapeHead();
    }

    internal static async ValueTask<IReplicaShapeReader> OpenAsync(TursoConnection connection, TenantContext database, TursoReplicaShapeSchema schema, long head, CancellationToken cancellationToken)
    {
        try
        {
            return await Task.Run(() => new TursoReplicaShapeReader(connection, database, schema, head), cancellationToken).ConfigureAwait(false);
        }
        catch
        {
            await connection.DisposeAsync().ConfigureAwait(false);
            throw;
        }
    }

    public ValueTask<LineageResult> ResolveAsync(string table, ShapeQuery query, CancellationToken cancellationToken) =>
        new(Task.Run(() => Resolve(table, query), cancellationToken));

    public ValueTask<ShapeGrant?> ReadGrantAsync(string table, CancellationToken cancellationToken) =>
        new(Task.Run(() => _reader.Grant(table), cancellationToken));

    public ValueTask<ShapeRead?> ReadAsync(ShapeScope scope, ShapeLineage lineage, long offset, IMemoryBudget memory, CancellationToken cancellationToken) =>
        new(Task.Run<ShapeRead?>(() =>
        {
            if (Resolve(scope.Table, scope.Query) is not ResolvedShape current)
            {
                return new MustRefetch("the replicated table definition changed");
            }

            if (current.Lineage != lineage)
            {
                return new MustRefetch("the shape lineage changed", current.Lineage);
            }

            if (offset > _reader.ShapeHead())
            {
                return null;
            }

            var floor = Command("SELECT coalesce(max(end_offset), 0) FROM _tb_shape_chunk WHERE handle = $handle AND offset = 0;");
            floor.Parameters.AddWithValue("$handle", "~floor:" + lineage.Handle);
            if (offset < (long)floor.ExecuteScalar()!)
            {
                return new MustRefetch("the shape offset is below the retention floor");
            }

            var definition = new ShapeSealDefinition(lineage.ShapeId, lineage.Handle, scope.Table, scope.Generation, Incarnation(), scope.Filter);
            try
            {
                return _reader.ReadChanges(definition, offset, null, memory, cancellationToken);
            }
            catch (ShapeSealOffsetException)
            {
                return new MustRefetch("the offset is not a retained commit boundary");
            }
        }, cancellationToken));

    public ValueTask DisposeAsync() => new(Task.Run(() =>
    {
        foreach (var command in _commands)
        {
            command.Dispose();
        }

        _transaction.Dispose();
        _connection.Dispose();
    }));

    private LineageResult Resolve(string table, ShapeQuery query)
    {
        if (_reader.ShapeTable(table) is not { Status: ShapeTableStatus.Current, Generation: { } generation })
        {
            return new Unavailable("the replicated shape definition is catching up; retry later");
        }

        ShapeFilter? filter = null;
        if (!query.IsEmpty)
        {
            switch (_reader.ReadFilter(table, generation, query))
            {
                case ShapeFilter parsed:
                    filter = parsed;
                    break;
                case Validation error:
                    return error;
                case Forbidden error:
                    return error;
                case Unavailable error:
                    return error;
            }
        }

        var handle = ShapeHandle.Compute(_database, Incarnation(), table, generation, filter);
        return new ResolvedShape(new ShapeScope(_database, table, generation, query, filter), new ShapeLineage(handle.ShapeId, handle.Handle));
    }

    private Ulid Incarnation() => Command(TursoStatements.Incarnation).ExecuteScalar() is string text && Ulid.TryParse(text, out var incarnation)
        ? incarnation : throw StoredDataCorruption.Create("the replicated database incarnation is missing or invalid");

    private SqliteCommand Command(string sql)
    {
        var command = _connection.CreateCommand();
        command.Transaction = _transaction;
        command.CommandText = sql;
        _commands.Add(command);
        return command;
    }
}
