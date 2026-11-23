namespace TerraBase.Postgres.Protocol;

internal sealed record PgError(string SqlState, string Message, bool Fatal = false, string? Detail = null, string? Hint = null)
{
    public static PgError ProtocolViolation(string message) => new("08P01", message, Fatal: true);

    public static PgError Syntax(string message) => new("42601", message);

    public static PgError Unsupported(string feature) => new("0A000", $"{feature} is not supported");

    public static PgError InFailedTransaction { get; } = new("25P02", "current transaction is aborted, commands ignored until end of transaction block");
}

internal sealed record PgNotice(string SqlState, string Message);
