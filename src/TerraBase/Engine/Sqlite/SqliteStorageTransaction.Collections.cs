using System.Globalization;
using Microsoft.Data.Sqlite;

namespace TerraBase.Engine.Sqlite;

/// <summary>
/// A client database keeps each collection's materialized documents in a rowid table of its own, named after the
/// collection (docs/terrabase/multi-region-replication.md section 5): <c>id</c> and <c>doc</c>, live documents only.
/// Reads, queries, indexes and uniqueness see these rows; versions come from <c>_tb_state</c>. The system database keeps
/// its <c>documents</c> table until it becomes a lattice database too.
/// </summary>
internal sealed partial class SqliteStorageTransaction
{
    /// <summary>
    /// The quoted table of a client collection. Collection names match <see cref="DatabaseName"/> or name a reserved
    /// metadata collection, so neither holds a quote.
    /// </summary>
    internal static string Table(string collection) => DatabaseName.TryParse(collection, out _) || IsMetadata(collection)
        ? $"\"{collection}\""
        : throw new ArgumentException("invalid collection name", nameof(collection));

    internal static bool IsMetadata(string collection) => collection is DocumentDelta.Collections or DocumentDelta.Indexes or DocumentDelta.Unique;

    /// <summary>The statement that creates a client collection's table.</summary>
    internal static string CreateTable(string collection) =>
        $"CREATE TABLE {Table(collection)} (id TEXT NOT NULL UNIQUE, doc TEXT NOT NULL);";

    // Worker-only: where the live documents of a collection are read from, for the query compilers.
    private RowSource Rows(string collection) => store.IsSystem
        ? RowSource.System
        : HasTable(collection)
            ? RowSource.Client(collection)
            : RowSource.Empty;

    // Worker-only: the names of a client database's collections, in name order.
    private List<string> CollectionNames(CancellationToken cancellationToken)
    {
        var names = new List<string>();
        using var reader = Command(SqliteStatements.ClientCollectionNames).ExecuteReader();
        while (reader.Read())
        {
            cancellationToken.ThrowIfCancellationRequested();
            names.Add(reader.GetString(0));
        }

        return names;
    }

    // Worker-only: whether a client collection's table exists. Metadata tables always do.
    private bool HasTable(string collection) =>
        IsMetadata(collection) || Command(SqliteStatements.GetCollection, collection).ExecuteScalar() is not null;

    // Worker-only: a statement over one collection's table ({0} in sql), prepared once per transaction.
    private SqliteCommand TableCommand(string sql, string collection, string? id = null)
    {
        var text = string.Format(CultureInfo.InvariantCulture, sql, Table(collection));
        if (!_bodyCommands.TryGetValue(text, out var command))
        {
            command = session.Connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText = text;
            _bodyCommands.Add(text, command);
        }

        command.Parameters.Clear();
        command.Parameters.AddWithValue("$collection", collection);
        if (id is not null)
        {
            command.Parameters.AddWithValue("$id", id);
        }

        return command;
    }
}

/// <summary>Where the live documents of one collection are, for the query compilers.</summary>
/// <param name="From">The table the rows are read from.</param>
/// <param name="Live">The condition that selects the collection's live rows of <paramref name="From"/>.</param>
/// <param name="Json">The column of the document JSON.</param>
/// <param name="Version">The expression of a row's version.</param>
internal sealed record RowSource(string From, string Live, string Json, string Version)
{
    /// <summary>The system database's documents table: tombstones have a NULL body.</summary>
    public static RowSource System { get; } =
        new("documents INDEXED BY documents_live", "collection = $collection AND body IS NOT NULL", "body", "version");

    /// <summary>A client collection's table, with versions from <c>_tb_state</c>.</summary>
    public static RowSource Client(string collection)
    {
        var table = SqliteStorageTransaction.Table(collection);
        return new(table, "1", "doc", $"(SELECT s.stamp FROM _tb_state s WHERE s.collection = $collection AND s.id = {table}.id)");
    }

    /// <summary>A client collection that does not exist: no rows.</summary>
    public static RowSource Empty { get; } = new("(SELECT NULL AS id, NULL AS doc WHERE 0)", "1", "doc", "NULL");
}
