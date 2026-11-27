using TerraBase.Postgres.Sessions;

namespace TerraBase.Postgres.Protocol;

internal closed record class PgStartupPacket;

internal sealed record PgSslRequest : PgStartupPacket;

internal sealed record PgGssEncryptionRequest : PgStartupPacket;

internal sealed record PgCancelRequest(int ProcessId, int SecretKey) : PgStartupPacket;

internal sealed record PgStartupMessage(int ProtocolVersion, IReadOnlyDictionary<string, string> Parameters) : PgStartupPacket;

internal readonly record struct PgParameterSlice(int Offset, int Length);

internal closed record class PgFrontendMessage;

internal sealed record PgPasswordMessage(string Password) : PgFrontendMessage;

internal sealed record PgQueryMessage : PgFrontendMessage
{
    public string Sql { get; private set; } = string.Empty;

    public CachedParseEntry? CachedEntry { get; private set; }

    public PgQueryMessage Reset(string sql, CachedParseEntry? cachedEntry)
    {
        Sql = sql;
        CachedEntry = cachedEntry;
        return this;
    }
}

internal sealed record PgParseMessage : PgFrontendMessage
{
    public string Name { get; private set; } = string.Empty;

    public string Sql { get; private set; } = string.Empty;

    public int[] ParameterOids { get; private set; } = [];

    public CachedParseEntry? CachedEntry { get; private set; }

    public PgParseMessage Reset(string name, string sql, int[] parameterOids, CachedParseEntry? cachedEntry)
    {
        Name = name;
        Sql = sql;
        ParameterOids = parameterOids;
        CachedEntry = cachedEntry;
        return this;
    }
}

internal sealed record PgBindMessage : PgFrontendMessage
{
    public string Portal { get; private set; } = string.Empty;

    public string Statement { get; private set; } = string.Empty;

    public short[] ParameterFormats { get; private set; } = [];

    public byte[] ParameterBuffer { get; private set; } = [];

    public PgParameterSlice[] Parameters { get; private set; } = [];

    public int ParameterCount { get; private set; }

    public short[] ResultFormats { get; private set; } = [];

    public PgBindMessage Reset(
        string portal,
        string statement,
        short[] parameterFormats,
        byte[] parameterBuffer,
        PgParameterSlice[] parameters,
        int parameterCount,
        short[] resultFormats)
    {
        Portal = portal;
        Statement = statement;
        ParameterFormats = parameterFormats;
        ParameterBuffer = parameterBuffer;
        Parameters = parameters;
        ParameterCount = parameterCount;
        ResultFormats = resultFormats;
        return this;
    }
}

internal sealed record PgDescribeMessage : PgFrontendMessage
{
    public char Target { get; private set; }

    public string Name { get; private set; } = string.Empty;

    public PgDescribeMessage Reset(char target, string name)
    {
        Target = target;
        Name = name;
        return this;
    }
}

internal sealed record PgExecuteMessage : PgFrontendMessage
{
    public string Portal { get; private set; } = string.Empty;

    public int MaxRows { get; private set; }

    public PgExecuteMessage Reset(string portal, int maxRows)
    {
        Portal = portal;
        MaxRows = maxRows;
        return this;
    }
}

internal sealed record PgCloseMessage : PgFrontendMessage
{
    public char Target { get; private set; }

    public string Name { get; private set; } = string.Empty;

    public PgCloseMessage Reset(char target, string name)
    {
        Target = target;
        Name = name;
        return this;
    }
}

internal sealed record PgSyncMessage : PgFrontendMessage
{
    public static PgSyncMessage Instance { get; } = new();
}

internal sealed record PgFlushMessage : PgFrontendMessage
{
    public static PgFlushMessage Instance { get; } = new();
}

internal sealed record PgTerminateMessage : PgFrontendMessage
{
    public static PgTerminateMessage Instance { get; } = new();
}

internal sealed record PgCopyDataMessage(byte[] Data) : PgFrontendMessage;

internal sealed record PgCopyDoneMessage : PgFrontendMessage
{
    public static PgCopyDoneMessage Instance { get; } = new();
}

internal sealed record PgCopyFailMessage(string Reason) : PgFrontendMessage;

internal sealed record PgFunctionCallMessage : PgFrontendMessage
{
    public static PgFunctionCallMessage Instance { get; } = new();
}

internal sealed record PgUnknownMessage(char Type) : PgFrontendMessage;

internal readonly record struct PgReadDeadline(TimeSpan Limit, TimeSpan Grace);

internal sealed record PgPeerClosed;

internal sealed record PgIdleExpired;

internal sealed record PgReadInterrupted;

internal sealed record PgIncompleteExpired;

internal readonly union PgStartupRead(PgStartupPacket, PgPeerClosed, PgIdleExpired, PgError);

internal readonly union PgMessageRead(PgFrontendMessage, PgPeerClosed, PgIdleExpired, PgIncompleteExpired, PgReadInterrupted, PgError);
