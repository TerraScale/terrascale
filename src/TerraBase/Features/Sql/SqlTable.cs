using System.Text.Json;
using System.Text.Json.Serialization;
using TerraBase.Engine;
using TerraBase.Hosting;

namespace TerraBase.Features.Sql;

/// <summary>SQLite column affinity, from the declared type (https://sqlite.org/datatype3.html section 3.1).</summary>
internal enum SqlAffinity
{
    Integer,
    Text,
    Blob,
    Real,
    Numeric,
}

/// <summary>One column of a SQL table: a top-level field of each row's document.</summary>
/// <param name="Name">Column and field name: a letter, then letters, digits or <c>_</c>, at most 64 characters.</param>
/// <param name="Type">Declared type as written (rendered from the syntax tree), empty when none.</param>
/// <param name="NotNull">Whether <c>NOT NULL</c> (always for primary key columns).</param>
/// <param name="Default">The literal <c>DEFAULT</c>, or <see langword="null"/> for none.</param>
internal sealed record SqlColumnDefinition(
    string Name,
    string Type,
    [property: JsonPropertyName("not_null")] bool NotNull,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] JsonElement? Default = null)
{
    /// <summary>The column's affinity.</summary>
    [JsonIgnore]
    public SqlAffinity Affinity => SqlTable.AffinityOf(Type);
}

/// <summary>A table definition, kept as member <c>sql</c> of its collection's <c>_collections</c> metadata document.</summary>
/// <param name="Columns">Columns in declaration order.</param>
/// <param name="PrimaryKey">Primary key columns in key order; empty when the table has none (ids are generated ULIDs).</param>
internal sealed record SqlTableDefinition(
    IReadOnlyList<SqlColumnDefinition> Columns,
    [property: JsonPropertyName("primary_key")] IReadOnlyList<string> PrimaryKey);

/// <summary>Reads and writes table definitions in a storage transaction.</summary>
internal static class SqlTable
{
    /// <summary>Most columns of a table.</summary>
    public const int MaxColumns = 256;

    /// <summary>The affinity SQLite gives a declared type.</summary>
    /// <summary>Whether <paramref name="type"/> is one of Postgres's auto-increment types: SMALLSERIAL, SERIAL or BIGSERIAL.</summary>
    public static bool IsSerial(string type) =>
        type.ToUpperInvariant() is "SERIAL" or "SMALLSERIAL" or "BIGSERIAL" or "SERIAL2" or "SERIAL4" or "SERIAL8";

    public static SqlAffinity AffinityOf(string type)
    {
        var upper = type.ToUpperInvariant();
        return upper.Contains("INT", StringComparison.Ordinal) ? SqlAffinity.Integer
            : upper.Contains("CHAR", StringComparison.Ordinal) || upper.Contains("CLOB", StringComparison.Ordinal) || upper.Contains("TEXT", StringComparison.Ordinal) ? SqlAffinity.Text
            : upper.Length == 0 || upper.Contains("BLOB", StringComparison.Ordinal) ? SqlAffinity.Blob
            : upper.Contains("REAL", StringComparison.Ordinal) || upper.Contains("FLOA", StringComparison.Ordinal) || upper.Contains("DOUB", StringComparison.Ordinal) ? SqlAffinity.Real
            : SqlAffinity.Numeric;
    }

    /// <summary>The definition of SQL table <paramref name="name"/>, or <see langword="null"/> when no SQL table has the name.</summary>
    public static async ValueTask<SqlTableDefinition?> ReadAsync(IStorageTransaction transaction, string name, CancellationToken cancellationToken) =>
        await transaction.GetCollectionTableAsync(name, cancellationToken).ConfigureAwait(false) is { } json
            ? json.Deserialize(TerraBaseJsonContext.Default.SqlTableDefinition)
            : null;

    /// <summary>Stores <paramref name="definition"/> on the existing collection <paramref name="name"/>.</summary>
    public static async ValueTask WriteAsync(IStorageTransaction transaction, string name, SqlTableDefinition definition, CancellationToken cancellationToken)
    {
        var json = JsonSerializer.SerializeToElement(definition, TerraBaseJsonContext.Default.SqlTableDefinition);
        if (!await transaction.SetCollectionTableAsync(name, json, cancellationToken).ConfigureAwait(false))
        {
            throw new InvalidOperationException("the table's collection must exist");
        }
    }
}
