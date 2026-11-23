using TerraBase.Postgres.Protocol;
using TerraBase.Postgres.Sql;

namespace TerraBase.Postgres.Sessions;

internal sealed record PgDescription(IReadOnlyList<PgColumn>? Columns);

internal readonly union PgDescribeResult(PgDescription, PgError);

internal sealed partial class PostgresConnection
{
    private static readonly PgDescription NoData = new(null);

    private async ValueTask<PgDescribeResult> DescribeColumnsAsync(PgCommand command, IReadOnlyList<SqlValue> parameters) => command switch
    {
        PgSqlCommand { Explain: true } => new PgDescription([new PgColumn("QUERY PLAN", PgOid.Text)]),
        PgSqlCommand { Access: SqlAccess.Read } read => await ProbeAsync(read, parameters),
        PgSqlCommand { Returning: { } items, TargetTable: { } table } => await ReturningAsync(table, items),
        PgSqlCommand => NoData,
        PgCannedCommand canned => new PgDescription(canned.Result.Columns),
        PgShowCommand show => Show(show) switch
        {
            PgResult result => new PgDescription(result.Columns),
            PgError error => error,
        },
        PgSetConfigCommand => new PgDescription([new PgColumn("set_config", PgOid.Text)]),
        PgEmptyCommand or PgBeginCommand or PgCommitCommand or PgRollbackCommand or PgSavepointCommand or PgReleaseCommand or PgRollbackToCommand
            or PgSetCommand or PgResetAllCommand or PgSetTransactionCommand or PgDeallocateCommand or PgDiscardCommand or PgListenCommand
            or PgUnlistenCommand or PgNoOpCommand or PgCopyFromCommand or PgCopyToCommand => NoData,
    };

    private async ValueTask<PgDescribeResult> ProbeAsync(PgSqlCommand command, IReadOnlyList<SqlValue> parameters)
    {
        if (session.Transaction is { Failed: true })
        {
            return PgError.InFailedTransaction;
        }

        var probed = await ReadMetadataAsync(command.Rewrite.ProbeSql, command.Rewrite.Bind(parameters));
        return probed switch
        {
            SqlResultSet set => new PgDescription(set.Columns.Count == 0 ? null : InferColumns(set.Columns, set.Rows, command.Rewrite.UsesCatalog)),
            PgError error => error,
        };
    }

    private async ValueTask<PgDescribeResult> ReturningAsync(string table, IReadOnlyList<PgReturningItem> items)
    {
        if (session.Transaction is { Failed: true })
        {
            return PgError.InFailedTransaction;
        }

        var schema = await ReadMetadataAsync("SELECT name, type FROM pragma_table_info(?1)", [SqlValue.FromText(table)]);
        return schema switch
        {
            SqlResultSet set => new PgDescription(ReturningColumns(set, items)),
            PgError error => error,
        };
    }

    private static List<PgColumn> ReturningColumns(SqlResultSet set, IReadOnlyList<PgReturningItem> items)
    {
        List<(string Name, int Oid)> table = [];
        foreach (var row in set.Rows)
        {
            if (row.Count >= 2 && row[0].TryGetText(out var name))
            {
                table.Add((name, PgOid.FromDeclaredType(row[1].TryGetText(out var declared) ? declared : null) is > 0 and var oid ? oid : PgOid.Text));
            }
        }

        List<PgColumn> columns = [];
        foreach (var item in items)
        {
            if (item.Column is null && item.Name == "*")
            {
                columns.AddRange(table.Select(static column => new PgColumn(column.Name, column.Oid)));
                continue;
            }

            var match = table.FindIndex(column => item.Column is { } name && column.Name.Equals(name, StringComparison.OrdinalIgnoreCase));
            columns.Add(new PgColumn(item.Name, match >= 0 ? table[match].Oid : PgOid.Text));
        }

        return columns;
    }
}
