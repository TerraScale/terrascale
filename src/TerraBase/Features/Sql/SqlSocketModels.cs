namespace TerraBase.Features.Sql;

/// <summary>A client frame on the SQL WebSocket.</summary>
/// <param name="Id">Client-chosen request ID, echoed on the answer.</param>
/// <param name="Type"><c>auth</c>, <c>execute</c>, <c>begin</c>, <c>commit</c> or <c>rollback</c>.</param>
/// <param name="Statements">For <c>execute</c>: 1 to 256 statements, as on <c>sql/execute</c>.</param>
/// <param name="Token">For <c>auth</c>, the first frame of a socket opened without an Authorization header: the bearer credential.</param>
internal sealed record SqlSocketRequest(long? Id, string? Type, IReadOnlyList<SqlExecuteStatement>? Statements = null, string? Token = null);

/// <summary>A successful answer: <c>{"id","type":"result","results","transaction"}</c>.</summary>
/// <param name="Id">The request ID.</param>
/// <param name="Type">Always <c>result</c>.</param>
/// <param name="Results">One result per statement; empty for <c>begin</c>, <c>commit</c> and <c>rollback</c>.</param>
/// <param name="Transaction">Whether a transaction is open after the request.</param>
internal sealed record SqlSocketResult(long? Id, string Type, IReadOnlyList<SqlStatementBody> Results, bool Transaction);

/// <summary>A refused request: <c>{"id","type":"error","error","transaction"}</c>.</summary>
/// <param name="Id">The request ID, or null when the frame could not be read.</param>
/// <param name="Type">Always <c>error</c>.</param>
/// <param name="Error">The same error as <c>sql/execute</c> answers.</param>
/// <param name="Transaction">Whether a transaction is open after the request.</param>
internal sealed record SqlSocketError(long? Id, string Type, SqlErrorDetail Error, bool Transaction);
