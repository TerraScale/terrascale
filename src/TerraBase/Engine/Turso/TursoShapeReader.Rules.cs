using System.Text;
using System.Text.Json;
using TerraBase.Features.Rules;
using TerraBase.Features.Shapes;
using Turso.Data.Sqlite;

namespace TerraBase.Engine.Turso;

internal sealed partial class TursoShapeReader : IRulesSchema
{
    internal const string RulesCurrent = "~rules:current";
    internal const string RulesVersionPrefix = "~rules:v:";
    internal const string KeptShapeRows = "((handle >= '~grant:' AND handle < '~grant;') OR (handle >= '~rules:' AND handle < '~rules;')) AND offset = 0";
    private const int RulesHeaderBytes = 512;

    internal StoredRules? Rules(string? version)
    {
        if (version is null)
        {
            var current = Command("SELECT body FROM _tb_shape_chunk WHERE handle = $key AND offset = 0;");
            current.Parameters.AddWithValue("$key", RulesCurrent);
            CountRead("shape");
            using var pointer = current.ExecuteReader();
            if (!pointer.Read())
            {
                return null;
            }

            version = Encoding.ASCII.GetString(TursoSession.Bytes(pointer, 0));
        }

        var read = Command("SELECT body, sealed_at FROM _tb_shape_chunk WHERE handle = $key AND offset = 0;");
        read.Parameters.AddWithValue("$key", RulesVersionPrefix + version);
        CountRead("shape");
        using var reader = read.ExecuteReader();
        if (!reader.Read())
        {
            return null;
        }

        var body = TursoSession.Bytes(reader, 0);
        var split = Array.IndexOf(body, (byte)'\n');
        var (digest, updatedBy) = RulesHeader(body.AsSpan(0, split));
        var rules = JsonSerializer.Deserialize(body.AsSpan(split + 1), RulesJsonContext.Default.RulesSet)
            ?? throw new InvalidDataException("a stored rules version is damaged");
        return new StoredRules(version, rules, new DateTimeOffset(reader.GetInt64(1), TimeSpan.Zero), updatedBy, digest);
    }

    internal RulesVersionPage? RulesVersions(string? after, int limit)
    {
        var below = long.MaxValue;
        if (after is not null)
        {
            var anchor = Command("SELECT end_offset FROM _tb_shape_chunk WHERE handle = $key AND offset = 0;");
            anchor.Parameters.AddWithValue("$key", RulesVersionPrefix + after);
            CountRead("shape");
            if (anchor.ExecuteScalar() is not long sequence)
            {
                return null;
            }

            below = sequence;
        }

        var list = Command($"""
            SELECT substr(handle, {RulesVersionPrefix.Length + 1}), substr(body, 1, {RulesHeaderBytes}), sealed_at FROM _tb_shape_chunk
            WHERE handle >= '~rules:v:' AND handle < '~rules:v;' AND offset = 0 AND end_offset < $below ORDER BY end_offset DESC LIMIT $limit;
            """);
        list.Parameters.AddWithValue("$below", below);
        list.Parameters.AddWithValue("$limit", limit + 1);
        CountRead("shape");
        List<RulesVersionItem> items = [];
        using var reader = list.ExecuteReader();
        while (reader.Read())
        {
            var head = TursoSession.Bytes(reader, 1);
            var (digest, updatedBy) = RulesHeader(head.AsSpan(0, Array.IndexOf(head, (byte)'\n')));
            items.Add(new RulesVersionItem(reader.GetString(0), new DateTimeOffset(reader.GetInt64(2), TimeSpan.Zero), updatedBy, digest));
        }

        return items.Count > limit ? new RulesVersionPage(items[..limit], items[limit - 1].Version) : new RulesVersionPage(items, null);
    }

    internal static byte[] RulesBody(StoredRules rules)
    {
        var header = $"{rules.Digest} {rules.UpdatedBy}\n";
        if (rules.UpdatedBy.Length is 0 || rules.UpdatedBy.Any(char.IsWhiteSpace) || Encoding.UTF8.GetByteCount(header) > RulesHeaderBytes)
        {
            throw new ArgumentException("a rules author is one short word", nameof(rules));
        }

        return [.. Encoding.UTF8.GetBytes(header), .. JsonSerializer.SerializeToUtf8Bytes(rules.Rules, RulesJsonContext.Default.RulesSet)];
    }

    private static (string Digest, string UpdatedBy) RulesHeader(ReadOnlySpan<byte> header) =>
        Encoding.UTF8.GetString(header).Split(' ') is [var digest, var updatedBy]
            ? (digest, updatedBy)
            : throw new InvalidDataException("a stored rules version is damaged");

    public RulesTableSchema RulesTable(string table)
    {
        var state = ShapeTable(table);
        if (state.Status is ShapeTableStatus.Missing or ShapeTableStatus.Unsupported)
        {
            return new RulesTableSchema(state.Status, []);
        }

        var command = TableCommand($"PRAGMA table_xinfo({Quote(table)});");
        List<string> columns = [];
        CountRead("shape");
        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            if (reader.GetInt64(6) is not (1 or 2))
            {
                columns.Add(reader.GetString(1));
            }
        }

        return new RulesTableSchema(ShapeTableStatus.Current, columns);
    }

    public bool Runs(RulesTableSchema table, CompiledCondition condition)
    {
        ArgumentNullException.ThrowIfNull(table);
        ArgumentNullException.ThrowIfNull(condition);
        var projection = string.Join(", ", table.Columns.Select(column => "NULL AS " + Quote(column)));
        var probe = TableCommand($"SELECT {condition.Sql} FROM (SELECT {projection});");
        foreach (var parameter in condition.Parameters)
        {
            probe.Parameters.AddWithValue(parameter.Name, DBNull.Value);
        }

        CountRead("shape");
        try
        {
            _ = probe.ExecuteScalar();
            return true;
        }
        catch (SqliteException)
        {
            return false;
        }
        finally
        {
            probe.Parameters.Clear();
        }
    }
}
