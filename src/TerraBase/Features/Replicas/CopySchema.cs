using TerraBase.Engine;
using TerraScale.TerraBase.Contracts;

namespace TerraBase.Features.Replicas;

internal static class CopySchema
{
    internal static readonly SqlStatement Tables = new("SELECT tbl_name FROM sqlite_schema", [], new Dictionary<string, object?>(), 10_000, 4L * 1024 * 1024,
        TimeSpan.FromSeconds(2));

    internal static bool HoldsUserTables(SqlStatementResult schema) =>
        schema.Truncated || schema.Rows.Any(row => row[0] is not string table || !TerraBaseSqlNames.IsInternalTable(table));
}
