namespace TerraBase.Features.Sql;

internal sealed record SqlSocketRequest(long? Id, string? Type, IReadOnlyList<SqlExecuteStatement>? Statements = null, string? Token = null);

internal sealed record SqlSocketResult(long? Id, string Type, IReadOnlyList<SqlStatementBody> Results, bool Transaction);

internal sealed record SqlSocketError(long? Id, string Type, SqlErrorDetail Error, bool Transaction);
