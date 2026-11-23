using TerraBase.Postgres.Protocol;
using TerraScale.Contracts.Consistency;

namespace TerraBase.Postgres.Sql;

internal sealed record PgResult(IReadOnlyList<PgColumn>? Columns, IReadOnlyList<IReadOnlyList<SqlValue>> Rows, string? Tag, PgNotice? Notice = null)
{
    public static PgResult Command(string tag) => new(null, [], tag);

    public static PgResult EmptyQuery { get; } = new(null, [], null);
}

internal readonly union PgOutcome(PgResult, PgError);

internal enum PgCopyFormat
{
    Text,
    Csv,
    Binary,
}

internal sealed record PgCopyOptions(PgCopyFormat Format, char Delimiter, string Null, bool Header, char Quote, char Escape);

internal sealed record PgReturningItem(string? Column, string Name);

internal closed record class PgCommand;

internal sealed record PgEmptyCommand : PgCommand;

internal sealed record PgSqlCommand(
    PgRewrite Rewrite,
    SqlAccess Access,
    string Verb,
    string? TagOverride,
    string? TargetTable,
    IReadOnlyList<PgReturningItem>? Returning,
    bool Explain) : PgCommand;

internal sealed record PgCannedCommand(PgResult Result) : PgCommand;

internal sealed record PgBeginCommand(ConsistencyMode? Mode, SqlAccess? Access) : PgCommand;

internal sealed record PgCommitCommand : PgCommand;

internal sealed record PgRollbackCommand : PgCommand;

internal sealed record PgSavepointCommand(string Name) : PgCommand;

internal sealed record PgReleaseCommand(string Name) : PgCommand;

internal sealed record PgRollbackToCommand(string Name) : PgCommand;

internal sealed record PgSetCommand(string Name, string? Value, bool Local) : PgCommand;

internal sealed record PgResetAllCommand : PgCommand;

internal sealed record PgSetTransactionCommand(ConsistencyMode? Mode, SqlAccess? Access) : PgCommand;

internal sealed record PgSetConfigCommand(string Name, string Value, bool Local) : PgCommand;

internal sealed record PgShowCommand(string Name) : PgCommand;

internal sealed record PgDeallocateCommand(string? Name) : PgCommand;

internal sealed record PgDiscardCommand : PgCommand;

internal sealed record PgListenCommand(string Channel) : PgCommand;

internal sealed record PgUnlistenCommand(string? Channel) : PgCommand;

internal sealed record PgNoOpCommand(string Tag) : PgCommand;

internal sealed record PgCopyFromCommand(string Table, IReadOnlyList<string> Columns, PgCopyOptions Options) : PgCommand;

internal sealed record PgCopyToCommand(PgSqlCommand Query, PgCopyOptions Options) : PgCommand;
