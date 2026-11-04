using System.Text.Json;
using TerraBase.Features.Shapes;

namespace TerraBase.Engine.Turso;

internal sealed partial class TursoShapeReader
{
    internal const string ShapeGrantPrefix = "~grant:";

    internal ShapeGrant? Grant(string table)
    {
        var read = Command("SELECT body, end_offset FROM _tb_shape_chunk WHERE handle = $key AND offset = 0;");
        read.Parameters.AddWithValue("$key", ShapeGrantPrefix + table);
        CountRead("shape");
        using var reader = read.ExecuteReader();
        return reader.Read() ? ParseGrant(table, (byte[])reader.GetValue(0), reader.GetInt64(1)) : null;
    }

    internal IReadOnlyList<ShapeGrant> Grants()
    {
        var read = Command("SELECT substr(handle, 8), body, end_offset FROM _tb_shape_chunk WHERE handle >= '~grant:' AND handle < '~grant;' AND offset = 0 ORDER BY handle;");
        CountRead("shape");
        List<ShapeGrant> grants = [];
        using var reader = read.ExecuteReader();
        while (reader.Read())
        {
            grants.Add(ParseGrant(reader.GetString(0), (byte[])reader.GetValue(1), reader.GetInt64(2)));
        }

        return grants;
    }

    internal static byte[] GrantBody(ShapeGrant grant)
    {
        using var buffer = new MemoryStream();
        using (var json = new Utf8JsonWriter(buffer))
        {
            json.WriteStartObject();
            json.WriteString("where", grant.Where);
            if (grant.Columns is { } columns)
            {
                json.WriteStartArray("columns");
                foreach (var column in columns)
                {
                    json.WriteStringValue(column);
                }

                json.WriteEndArray();
            }
            else
            {
                json.WriteNull("columns");
            }

            json.WriteEndObject();
        }

        return buffer.ToArray();
    }

    private static ShapeGrant ParseGrant(string table, byte[] body, long updatedTicks)
    {
        using var document = JsonDocument.Parse(body);
        var root = document.RootElement;
        var where = root.GetProperty("where").GetString();
        var columns = root.GetProperty("columns") is { ValueKind: JsonValueKind.Array } list
            ? list.EnumerateArray().Select(column => column.GetString()!).ToList()
            : null;
        return new ShapeGrant(table, where, columns, new DateTimeOffset(updatedTicks, TimeSpan.Zero));
    }
}
