using System.Net.Security;

namespace TerraBase.Postgres.Hosting;

public sealed record PostgresServerOptions(SslServerAuthenticationOptions? Tls);

internal static class PostgresLimits
{
    public const int MaxStartupBytes = 10_000;
    public const int MaxMessageBytes = 8 * 1024 * 1024;
    public const int MaxConnections = 4096;
    public const int MaxPreparedStatements = 256;
    public const int MaxPortals = 256;
    public const int MaxParameters = ushort.MaxValue;
    public const int MaxCopyRecordBytes = 8 * 1024 * 1024;
    public const int CopyStatementsPerFlush = 512;
    public const int OutputFlushBytes = 64 * 1024;
    public const int NotificationQueue = 1024;
    public const int MaxChannelLength = 63;

    public static readonly TimeSpan HandshakeTimeout = TimeSpan.FromSeconds(10);
    public static readonly TimeSpan SessionIdleTimeout = TimeSpan.FromHours(1);
    public static readonly TimeSpan IdleInTransactionTimeout = TimeSpan.FromMinutes(5);
    public static readonly TimeSpan StatementTimeout = TimeSpan.FromMinutes(5);
    public static readonly TimeSpan WriteTimeout = TimeSpan.FromSeconds(30);
}
