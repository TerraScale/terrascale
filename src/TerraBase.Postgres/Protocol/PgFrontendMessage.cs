namespace TerraBase.Postgres.Protocol;

internal closed record class PgStartupPacket;

internal sealed record PgSslRequest : PgStartupPacket;

internal sealed record PgGssEncryptionRequest : PgStartupPacket;

internal sealed record PgCancelRequest(int ProcessId, int SecretKey) : PgStartupPacket;

internal sealed record PgStartupMessage(int ProtocolVersion, IReadOnlyDictionary<string, string> Parameters) : PgStartupPacket;

internal closed record class PgFrontendMessage;

internal sealed record PgPasswordMessage(string Password) : PgFrontendMessage;

internal sealed record PgQueryMessage(string Sql) : PgFrontendMessage;

internal sealed record PgParseMessage(string Name, string Sql, int[] ParameterOids) : PgFrontendMessage;

internal sealed record PgBindMessage(string Portal, string Statement, short[] ParameterFormats, byte[]?[] Parameters, short[] ResultFormats) : PgFrontendMessage;

internal sealed record PgDescribeMessage(char Target, string Name) : PgFrontendMessage;

internal sealed record PgExecuteMessage(string Portal, int MaxRows) : PgFrontendMessage;

internal sealed record PgCloseMessage(char Target, string Name) : PgFrontendMessage;

internal sealed record PgSyncMessage : PgFrontendMessage;

internal sealed record PgFlushMessage : PgFrontendMessage;

internal sealed record PgTerminateMessage : PgFrontendMessage;

internal sealed record PgCopyDataMessage(byte[] Data) : PgFrontendMessage;

internal sealed record PgCopyDoneMessage : PgFrontendMessage;

internal sealed record PgCopyFailMessage(string Reason) : PgFrontendMessage;

internal sealed record PgFunctionCallMessage : PgFrontendMessage;

internal sealed record PgUnknownMessage(char Type) : PgFrontendMessage;

internal readonly record struct PgReadDeadline(TimeSpan Limit, TimeSpan Grace);

internal sealed record PgPeerClosed;

internal sealed record PgIdleExpired;

internal sealed record PgReadInterrupted;

internal sealed record PgIncompleteExpired;

internal readonly union PgStartupRead(PgStartupPacket, PgPeerClosed, PgIdleExpired, PgError);

internal readonly union PgMessageRead(PgFrontendMessage, PgPeerClosed, PgIdleExpired, PgIncompleteExpired, PgReadInterrupted, PgError);
