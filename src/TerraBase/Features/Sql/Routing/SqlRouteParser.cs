using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace TerraBase.Features.Sql.Routing;

internal sealed record SqlRouteTable(string? Schema, string Name);

internal sealed record SqlRouteColumn(string? Schema, string? Table, string Name);

internal sealed record SqlRouteWhere(string Shape, IReadOnlyList<SqlRouteColumn> EqualityColumns);

internal sealed record SqlRouteFlags(
    bool Unknown, bool With, bool Upsert, bool InsertSelect, bool Replace, bool UpdateFrom, bool ConflictClause, bool Returning, bool ColumnsComplete,
    bool TablesComplete);

internal sealed record SqlRouteFacts(
    int Version, int Start, int End, string Kind, IReadOnlyList<SqlRouteTable> Tables, SqlRouteWhere Where, IReadOnlyList<string> ColumnsTouched,
    string AssignmentDependence, bool Ddl, bool Explain, SqlRouteFlags Flags);

[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.SnakeCaseLower)]
[JsonSerializable(typeof(SqlRouteFacts))]
internal sealed partial class SqlRouteJsonContext : JsonSerializerContext;

internal static partial class SqlRouteParser
{
    private const string Library = "terrascale_sql_parser";
    private const int Found = 0;
    private const int NoStatement = 1;

    public static IReadOnlyList<SqlRouteFacts>? Parse(string sql)
    {
        ArgumentNullException.ThrowIfNull(sql);
        var bytes = Encoding.UTF8.GetBytes(sql);
        try
        {
            return Parse(bytes);
        }
        catch (Exception error) when (error is DllNotFoundException or EntryPointNotFoundException or BadImageFormatException or JsonException)
        {
            return null;
        }
    }

    private static unsafe List<SqlRouteFacts>? Parse(byte[] bytes)
    {
        var statements = new List<SqlRouteFacts>();
        nuint offset = 0;
        fixed (byte* sql = bytes)
        {
            while (offset < (nuint)bytes.Length)
            {
                byte* output = null;
                nuint length = 0;
                var status = Next(sql, (nuint)bytes.Length, offset, &output, &length);
                if (status == NoStatement)
                {
                    break;
                }

                if (status != Found)
                {
                    return null;
                }

                SqlRouteFacts? facts;
                try
                {
                    facts = JsonSerializer.Deserialize(new ReadOnlySpan<byte>(output, checked((int)length)), SqlRouteJsonContext.Default.SqlRouteFacts);
                }
                finally
                {
                    Free(output, length);
                }

                if (facts is null || facts.Version != 1 || (nuint)facts.End <= offset)
                {
                    return null;
                }

                statements.Add(facts);
                offset = (nuint)facts.End;
            }
        }

        return statements;
    }

    [LibraryImport(Library, EntryPoint = "ts_sql_next")]
    private static unsafe partial int Next(byte* sql, nuint length, nuint offset, byte** output, nuint* outputLength);

    [LibraryImport(Library, EntryPoint = "ts_sql_free")]
    private static unsafe partial void Free(byte* output, nuint length);
}
