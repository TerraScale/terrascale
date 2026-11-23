namespace TerraBase.Postgres.Sql;

internal sealed record PgRewriteContext(string DatabaseName, string UserName, int ProcessId, IReadOnlyDictionary<string, string> Settings)
{
    public const string ServerVersion = "16.4 (TerraBase v2)";

    public const string ServerVersionNumber = "160004";

    public static string Version => $"PostgreSQL {ServerVersion} on SQLite, compiled by TerraBase, 64-bit";

    public static string Literal(string value) => "'" + value.Replace("'", "''", StringComparison.Ordinal) + "'";
}

internal sealed record PgParameterColumn(int Parameter, string Column);

internal sealed record PgRewrite(
    string Sql,
    int ParameterCount,
    IReadOnlyDictionary<int, int> ParameterTypes,
    IReadOnlyList<PgParameterColumn> ParameterColumns,
    bool UsesCatalog);
