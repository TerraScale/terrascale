using System.Text;

namespace TerraBase.Storage;

public static class SqlLimits
{
    public const int MaxStatementBytes = 16 * 1024;

    internal const int MaxResultRows = 10_000;
    internal const int MaxResultBytes = 4 * 1024 * 1024;
    internal const int MaxWriteRows = 1024;
    internal const int MaxWriteBytes = 8 * 1024 * 1024;

    public static SqlError StatementTooLong(int index) => SqliteStatus.Unsupported("a statement is at most 16 KiB").ToSqlError(index);

    public static bool StatementFits(string sql) =>
        sql.Length <= MaxStatementBytes && Encoding.UTF8.GetByteCount(sql) <= MaxStatementBytes;
}
