using System.Buffers;
using System.Diagnostics;
using System.Text.Json;
using TerraBase.Errors;
using TerraBase.Features.Rules;
using TerraBase.Features.Shapes;

namespace TerraBase.Engine.Turso;

internal sealed partial class TursoShapeReader
{
    internal ShapeRowRead ReadRow(string table, long key, ShapeQuery query)
    {
        var schema = RulesTable(table);
        switch (schema.Status)
        {
            case ShapeTableStatus.Missing:
                return new NotFound("the table does not exist", "unknown_table");
            case ShapeTableStatus.Unsupported:
                return new Validation("rows are read from ordinary rowid tables", "unsupported_table");
        }

        var parsed = query.Viewer is null
            ? ShapeFilters.Parse(query, schema.Columns)
            : RulesReads.Filter(query, table, schema.Columns, Rules(null), this);
        if (parsed is not ShapeFilter filter)
        {
            return parsed switch
            {
                Validation error => error,
                Forbidden error => error,
                _ => throw new UnreachableException(),
            };
        }

        var select = TableCommand($"SELECT {string.Join(", ", filter.Columns.Select(Quote))} FROM {Quote(table)} WHERE rowid = $row_key"
            + (filter.Where is { } where ? $" AND ({where});" : ";"));
        select.Parameters.AddWithValue("$row_key", key);
        foreach (var (name, value) in filter.Parameters)
        {
            select.Parameters.AddWithValue(name, value ?? DBNull.Value);
        }

        CountRead("shape");
        try
        {
            using var reader = select.ExecuteReader();
            if (!reader.Read())
            {
                return RowNotFound;
            }

            var buffer = new ArrayBufferWriter<byte>();
            using (var writer = new Utf8JsonWriter(buffer))
            {
                writer.WriteStartObject();
                writer.WriteStartObject("row");
                for (var index = 0; index < filter.Columns.Count; index++)
                {
                    writer.WritePropertyName(filter.Columns[index]);
                    WriteShapeValue(writer, reader.IsDBNull(index) ? null : reader.GetValue(index));
                }

                writer.WriteEndObject();
                writer.WriteEndObject();
            }

            return new ShapeRow(buffer.WrittenSpan.ToArray());
        }
        finally
        {
            select.Parameters.Clear();
        }
    }

    private static readonly NotFound RowNotFound = new("the row does not exist or is not readable", "row_not_found");
}
