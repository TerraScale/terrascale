using System.Text;
using TerraBase.Postgres.Protocol;

namespace TerraBase.Postgres.Sql;

internal sealed record PgRewriteContext(string DatabaseName, string UserName, int ProcessId, IReadOnlyDictionary<string, string> Settings)
{
    public const string ServerVersion = "16.4 (TerraBase v2)";

    public const string ServerVersionNumber = "160004";

    public static string Version => $"PostgreSQL {ServerVersion} on SQLite, compiled by TerraBase, 64-bit";

    public static string Literal(string value) =>
        value.Contains('\'', StringComparison.Ordinal)
            ? string.Concat("'", value.Replace("'", "''", StringComparison.Ordinal), "'")
            : string.Concat("'", value, "'");
}

internal sealed record PgParameterColumn(int Parameter, string Column);

internal sealed record PgRewrite(
    string Sql,
    int ParameterCount,
    IReadOnlyDictionary<int, int> ParameterTypes,
    IReadOnlyList<PgParameterColumn> ParameterColumns,
    IReadOnlyList<string> ReferencedNames,
    bool UsesCatalog,
    IReadOnlyList<SqlValue> LiftedLiterals,
    bool DependsOnSettings = false,
    bool DependsOnSessionIdentity = false)
{
    private const int MaxStatementBytes = 16 * 1024;
    private const string ProbePrefix = "SELECT * FROM (";
    private const string ProbeSuffix = ") LIMIT 1";

    internal static PgError StatementTooLong { get; } = new("0A000", "a statement is at most 16 KiB");

    internal static PgError NulCharacter { get; } = new("22021", "invalid byte sequence for encoding \"UTF8\": 0x00");

    internal string ProbeSql => string.Concat(ProbePrefix, Sql, ProbeSuffix);

    internal static bool Fits(ReadOnlySpan<char> sql, bool describe = false)
    {
        var limit = MaxStatementBytes - (describe ? ProbePrefix.Length + ProbeSuffix.Length : 0);
        if (sql.Length * 3 <= limit)
        {
            return true;
        }

        return Encoding.UTF8.GetByteCount(sql) <= limit;
    }

    internal IReadOnlyList<SqlValue> Bind(IReadOnlyList<SqlValue> parameters) =>
        LiftedLiterals.Count == 0 ? parameters : [.. parameters.Take(ParameterCount), .. LiftedLiterals];
}

internal readonly union PgRewriteResult(PgRewrite, PgError);
