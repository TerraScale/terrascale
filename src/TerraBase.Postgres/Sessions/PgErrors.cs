using System.Globalization;
using TerraBase.Postgres.Hosting;
using TerraBase.Postgres.Protocol;
using TerraScale.Contracts.Cluster;

namespace TerraBase.Postgres.Sessions;

internal static class PgErrors
{
    public static PgError QueryCanceled { get; } = new("57014", "canceling statement due to user request");

    public static PgError StatementTimeout { get; } = new("57014", "canceling statement due to statement timeout");

    public static PgError TransactionExpired { get; } = new("57014", "the transaction expired: it was open for more than 5 seconds and was rolled back", Fatal: true);

    public static PgError AdministratorShutdown { get; } = new("57P01", "terminating connection due to administrator command", Fatal: true);

    public static PgError CredentialRevoked { get; } = new("28000", "the credential for this session was revoked", Fatal: true);

    public static PgError ReadOnlyCredential { get; } = new("42501", "permission denied: this credential can only read");

    public static PgError ReadOnlyTransaction { get; } = new("25006", "cannot execute a write in a read-only transaction");

    public static PgError PortalMemory { get; } =
        new("54000", $"portals exceed the session budget of {PostgresLimits.MaxPortalBytes.ToString(CultureInfo.InvariantCulture)} bytes", Hint: "close portals or fetch fewer rows");

    public static PgError TooManyChannels { get; } =
        new("54000", $"at most {PostgresLimits.MaxChannels.ToString(CultureInfo.InvariantCulture)} LISTEN channels per session");

    public static PgError From(SqlError error) => new(error.SqlState, error.Message, Detail: error.Code);

    public static PgError From(StorageBusy busy) =>
        new("55P03", busy.Message, Hint: $"retry after {busy.RetryAfter.TotalMilliseconds.ToString("0", CultureInfo.InvariantCulture)} ms");

    public static PgError From(StorageTimeout timeout) => new("57014", "canceling statement due to statement timeout", Detail: timeout.Message);

    public static PgError From(ClusterTimeout timeout) => new("57014", "canceling statement due to statement timeout", Detail: timeout.Message);

    public static PgError From(SerializationConflict conflict) =>
        new("40001", "could not serialize access due to concurrent update", Detail: conflict.Reason, Hint: "retry the transaction");

    public static PgError From(SchemaMismatch mismatch) =>
        new("40001", "could not serialize access due to a concurrent schema change",
            Detail: $"schema epoch {mismatch.ExpectedEpoch.ToString(CultureInfo.InvariantCulture)} changed to {mismatch.ActualEpoch.ToString(CultureInfo.InvariantCulture)}",
            Hint: "retry the statement");

    public static PgError From(ClusterUnavailable unavailable) => new("57P03", unavailable.Message, Detail: unavailable.Code, Hint: "retry the statement");
}
