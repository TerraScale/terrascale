using System.Text;
using Cronos;

namespace TerraScale.Platform.Features.Functions;

public sealed record FunctionDeleted;

public sealed record SecretDeleted;

public sealed record FunctionRunRecorded;

public readonly union DeployFunctionOutcome(
    FunctionBody,
    PlatformInvalidRequest,
    PlatformConflict,
    PlatformUnavailable);

public readonly union ListFunctionsOutcome(FunctionList, PlatformUnavailable);

public readonly union GetFunctionOutcome(FunctionBody, PlatformNotFound, PlatformUnavailable);

public readonly union DeleteFunctionOutcome(FunctionDeleted, PlatformNotFound, PlatformUnavailable);

public readonly union ListFunctionLogsOutcome(FunctionLogList, PlatformNotFound, PlatformUnavailable);

public readonly union GetFunctionStatsOutcome(FunctionStats, PlatformNotFound, PlatformUnavailable);

public readonly union ListFunctionSecretsOutcome(FunctionSecretList, PlatformNotFound, PlatformUnavailable);

public readonly union SetFunctionSecretOutcome(
    FunctionSecretBody,
    PlatformInvalidRequest,
    PlatformNotFound,
    PlatformConflict,
    PlatformUnavailable);

public readonly union DeleteFunctionSecretOutcome(SecretDeleted, PlatformInvalidRequest, PlatformNotFound, PlatformUnavailable);

public readonly union LoadFunctionOutcome(FunctionCode, PlatformNotFound, PlatformUnavailable);

public readonly union RecordFunctionRunOutcome(FunctionRunRecorded, PlatformUnavailable);

public sealed class FunctionService(PlatformStore store, FunctionSecretSeal seal, TimeProvider timeProvider)
{
    public const int MaxFunctions = 32;
    public const int MaxSecrets = 50;
    public const int MaxSecretBytes = 4 * 1024;
    public const int KeptLogs = 100;

    private const string FunctionColumns = "name, source, cron, created_at, updated_at";
    private const string Exists = "SELECT 1 FROM platform_functions WHERE database_id = ? AND name = ?";

    public static PlatformInvalidRequest? CheckCron(string? cron) =>
        cron is null || CronExpression.TryParse(cron, CronFormat.Standard, out _) ? null : new PlatformInvalidRequest("cron must be a five-field cron expression");

    public static long? NextRun(string? cron, DateTimeOffset after) =>
        cron is not null && CronExpression.TryParse(cron, CronFormat.Standard, out var expression)
            ? expression.GetNextOccurrence(after, TimeZoneInfo.Utc)?.ToUnixTimeMilliseconds()
            : null;

    public async ValueTask<DeployFunctionOutcome> DeployAsync(
        FunctionTarget target,
        string name,
        string? source,
        string? cron,
        CancellationToken cancellationToken)
    {
        if (source is not { Length: > 0 and <= FunctionRunner.MaxSourceChars })
        {
            return new PlatformInvalidRequest("source is required and at most 256 KiB");
        }

        if (FunctionRunner.Check(source) is { } rejected)
        {
            return new PlatformInvalidRequest("source is not valid JavaScript: " + rejected.Message);
        }

        if (CheckCron(cron) is { } invalidCron)
        {
            return invalidCron;
        }

        var now = timeProvider.GetUtcNow();
        var database = SqlValues.Text(target.Database.ToString());
        var function = SqlValues.Text(name);
        var upsert = SqlValues.Statement(
            "INSERT INTO platform_functions (database_id, name, organization_id, project_id, database_name, source, cron, cron_next, created_at, updated_at) "
            + "SELECT ?, ?, ?, ?, ?, ?, ?, ?, ?, ? WHERE (SELECT COUNT(*) FROM platform_functions WHERE database_id = ?) < ? "
            + "OR EXISTS (SELECT 1 FROM platform_functions WHERE database_id = ? AND name = ?) "
            + "ON CONFLICT (database_id, name) DO UPDATE SET source = excluded.source, cron = excluded.cron, cron_next = excluded.cron_next, "
            + "updated_at = excluded.updated_at, database_name = excluded.database_name",
            database,
            function,
            SqlValues.Text(target.Organization.ToString()),
            SqlValues.Text(target.Project.ToString()),
            SqlValues.Text(target.DatabaseName),
            SqlValues.Text(source),
            SqlValues.OptionalText(cron),
            SqlValues.OptionalNumber(NextRun(cron, now)),
            SqlValues.Number(now.UnixMs()),
            SqlValues.Number(now.UnixMs()),
            database,
            SqlValues.Number(MaxFunctions),
            database,
            function);
        var read = SqlValues.Statement($"SELECT {FunctionColumns} FROM platform_functions WHERE database_id = ? AND name = ?", database, function);
        var outcome = await store.ExecuteAsync([upsert, read], SqlContext.Fast, cancellationToken);
        return outcome switch
        {
            SqlExecuteResponse { Results: var results } when results[0].RowsAffected == 0 =>
                new PlatformConflict($"at most {MaxFunctions} functions may be deployed per database", "function_limit"),
            SqlExecuteResponse { Results: var results } => results[1].FirstRow() is { } row
                ? Body(row, withSource: true)
                : new PlatformUnavailable("the deployed function was not readable", "platform_state"),
            PlatformConflict conflict => conflict,
            PlatformUnavailable unavailable => unavailable,
        };
    }

    public async ValueTask<ListFunctionsOutcome> ListAsync(FunctionTarget target, CancellationToken cancellationToken)
    {
        var outcome = await store.ReadAsync(
            SqlValues.Statement($"SELECT {FunctionColumns} FROM platform_functions WHERE database_id = ? ORDER BY name", SqlValues.Text(target.Database.ToString())),
            cancellationToken);
        return outcome switch
        {
            SqlExecuteResponse { Results: var results } => new FunctionList([.. results[0].RowsOf().Select(static row => Body(row, withSource: false))]),
            PlatformConflict conflict => new PlatformUnavailable(conflict.Message, "platform_state"),
            PlatformUnavailable unavailable => unavailable,
        };
    }

    public async ValueTask<GetFunctionOutcome> GetAsync(FunctionTarget target, string name, CancellationToken cancellationToken)
    {
        var outcome = await store.ReadAsync(
            SqlValues.Statement($"SELECT {FunctionColumns} FROM platform_functions WHERE database_id = ? AND name = ?",
                SqlValues.Text(target.Database.ToString()), SqlValues.Text(name)),
            cancellationToken);
        return outcome switch
        {
            SqlExecuteResponse { Results: var results } => results[0].FirstRow() is { } row ? Body(row, withSource: true) : Missing(),
            PlatformConflict conflict => new PlatformUnavailable(conflict.Message, "platform_state"),
            PlatformUnavailable unavailable => unavailable,
        };
    }

    public async ValueTask<DeleteFunctionOutcome> DeleteAsync(FunctionTarget target, string name, CancellationToken cancellationToken)
    {
        var database = SqlValues.Text(target.Database.ToString());
        var function = SqlValues.Text(name);
        IReadOnlyList<SqlHttpStatement> statements =
        [
            SqlValues.Statement("DELETE FROM platform_functions WHERE database_id = ? AND name = ?", database, function),
            SqlValues.Statement("DELETE FROM platform_function_secrets WHERE database_id = ? AND function_name = ?", database, function),
            SqlValues.Statement("DELETE FROM platform_function_logs WHERE database_id = ? AND function_name = ?", database, function),
            SqlValues.Statement("DELETE FROM platform_function_stats WHERE database_id = ? AND function_name = ?", database, function),
        ];
        var outcome = await store.ExecuteAsync(statements, SqlContext.Fast, cancellationToken);
        return outcome switch
        {
            SqlExecuteResponse { Results: var results } => results[0].RowsAffected == 0 ? Missing() : new FunctionDeleted(),
            PlatformConflict conflict => new PlatformUnavailable(conflict.Message, "platform_state"),
            PlatformUnavailable unavailable => unavailable,
        };
    }

    public async ValueTask<ListFunctionLogsOutcome> LogsAsync(FunctionTarget target, string name, int limit, CancellationToken cancellationToken)
    {
        var database = SqlValues.Text(target.Database.ToString());
        var function = SqlValues.Text(name);
        IReadOnlyList<SqlHttpStatement> statements =
        [
            SqlValues.Statement(Exists, database, function),
            SqlValues.Statement(
                "SELECT body FROM platform_function_logs WHERE database_id = ? AND function_name = ? ORDER BY at DESC, id DESC LIMIT ?",
                database, function, SqlValues.Number(limit)),
        ];
        var outcome = await store.ExecuteAsync(statements, SqlContext.Fast, cancellationToken);
        return outcome switch
        {
            SqlExecuteResponse { Results: var results } when results[0].Rows.Count == 0 => Missing(),
            SqlExecuteResponse { Results: var results } => new FunctionLogList([.. results[1].RowsOf().Select(static row => Log(row.Text(0))).OfType<FunctionLogBody>()]),
            PlatformConflict conflict => new PlatformUnavailable(conflict.Message, "platform_state"),
            PlatformUnavailable unavailable => unavailable,
        };
    }

    public async ValueTask<GetFunctionStatsOutcome> StatsAsync(FunctionTarget target, string name, string window, CancellationToken cancellationToken)
    {
        var now = timeProvider.GetUtcNow();
        IReadOnlyList<SqlHttpStatement> statements =
        [
            SqlValues.Statement(Exists, SqlValues.Text(target.Database.ToString()), SqlValues.Text(name)),
            FunctionStatsMath.Read(target, name, window, now),
        ];
        var outcome = await store.ExecuteAsync(statements, SqlContext.Fast, cancellationToken);
        return outcome switch
        {
            SqlExecuteResponse { Results: var results } when results[0].Rows.Count == 0 => Missing(),
            SqlExecuteResponse { Results: var results } => FunctionStatsMath.Aggregate(window, results[1], now),
            PlatformConflict conflict => new PlatformUnavailable(conflict.Message, "platform_state"),
            PlatformUnavailable unavailable => unavailable,
        };
    }

    public async ValueTask<ListFunctionSecretsOutcome> ListSecretsAsync(FunctionTarget target, string name, CancellationToken cancellationToken)
    {
        var database = SqlValues.Text(target.Database.ToString());
        var function = SqlValues.Text(name);
        IReadOnlyList<SqlHttpStatement> statements =
        [
            SqlValues.Statement(Exists, database, function),
            SqlValues.Statement(
                "SELECT name, updated_at FROM platform_function_secrets WHERE database_id = ? AND function_name = ? ORDER BY name", database, function),
        ];
        var outcome = await store.ExecuteAsync(statements, SqlContext.Fast, cancellationToken);
        return outcome switch
        {
            SqlExecuteResponse { Results: var results } when results[0].Rows.Count == 0 => Missing(),
            SqlExecuteResponse { Results: var results } =>
                new FunctionSecretList([.. results[1].RowsOf().Select(static row => new FunctionSecretBody(row.Text(0), row.Number(1).FromUnixMs()))]),
            PlatformConflict conflict => new PlatformUnavailable(conflict.Message, "platform_state"),
            PlatformUnavailable unavailable => unavailable,
        };
    }

    public async ValueTask<SetFunctionSecretOutcome> SetSecretAsync(
        FunctionTarget target,
        string name,
        string secret,
        string? value,
        CancellationToken cancellationToken)
    {
        if (FunctionNames.CheckSecret(secret) is { } invalid)
        {
            return invalid;
        }

        if (value is null || Encoding.UTF8.GetByteCount(value) is 0 or > MaxSecretBytes)
        {
            return new PlatformInvalidRequest("value is required and at most 4 KiB");
        }

        var now = timeProvider.GetUtcNow();
        var database = SqlValues.Text(target.Database.ToString());
        var function = SqlValues.Text(name);
        var key = SqlValues.Text(secret);
        var upsert = SqlValues.Statement(
            "INSERT INTO platform_function_secrets (database_id, function_name, name, sealed, updated_at) SELECT ?, ?, ?, ?, ? "
            + "WHERE EXISTS (SELECT 1 FROM platform_functions WHERE database_id = ? AND name = ?) "
            + "AND ((SELECT COUNT(*) FROM platform_function_secrets WHERE database_id = ? AND function_name = ?) < ? "
            + "OR EXISTS (SELECT 1 FROM platform_function_secrets WHERE database_id = ? AND function_name = ? AND name = ?)) "
            + "ON CONFLICT (database_id, function_name, name) DO UPDATE SET sealed = excluded.sealed, updated_at = excluded.updated_at",
            database,
            function,
            key,
            SqlValues.Text(seal.Seal(target.Database, name, secret, value)),
            SqlValues.Number(now.UnixMs()),
            database,
            function,
            database,
            function,
            SqlValues.Number(MaxSecrets),
            database,
            function,
            key);
        var outcome = await store.ExecuteAsync([upsert, SqlValues.Statement(Exists, database, function)], SqlContext.Fast, cancellationToken);
        return outcome switch
        {
            SqlExecuteResponse { Results: var results } when results[1].Rows.Count == 0 => Missing(),
            SqlExecuteResponse { Results: var results } when results[0].RowsAffected == 0 =>
                new PlatformConflict($"at most {MaxSecrets} secrets may be set per function", "function_secret_limit"),
            SqlExecuteResponse => new FunctionSecretBody(secret, DateTimeOffset.FromUnixTimeMilliseconds(now.UnixMs())),
            PlatformConflict conflict => conflict,
            PlatformUnavailable unavailable => unavailable,
        };
    }

    public async ValueTask<DeleteFunctionSecretOutcome> DeleteSecretAsync(
        FunctionTarget target,
        string name,
        string secret,
        CancellationToken cancellationToken)
    {
        if (FunctionNames.CheckSecret(secret) is { } invalid)
        {
            return invalid;
        }

        var database = SqlValues.Text(target.Database.ToString());
        var function = SqlValues.Text(name);
        IReadOnlyList<SqlHttpStatement> statements =
        [
            SqlValues.Statement(Exists, database, function),
            SqlValues.Statement("DELETE FROM platform_function_secrets WHERE database_id = ? AND function_name = ? AND name = ?", database, function, SqlValues.Text(secret)),
        ];
        var outcome = await store.ExecuteAsync(statements, SqlContext.Fast, cancellationToken);
        return outcome switch
        {
            SqlExecuteResponse { Results: var results } when results[0].Rows.Count == 0 => Missing(),
            SqlExecuteResponse { Results: var results } when results[1].RowsAffected == 0 => new PlatformNotFound("secret not found", "secret_not_found"),
            SqlExecuteResponse => new SecretDeleted(),
            PlatformConflict conflict => new PlatformUnavailable(conflict.Message, "platform_state"),
            PlatformUnavailable unavailable => unavailable,
        };
    }

    public async ValueTask<LoadFunctionOutcome> LoadAsync(FunctionTarget target, string name, CancellationToken cancellationToken)
    {
        var database = SqlValues.Text(target.Database.ToString());
        var function = SqlValues.Text(name);
        IReadOnlyList<SqlHttpStatement> statements =
        [
            SqlValues.Statement("SELECT source FROM platform_functions WHERE database_id = ? AND name = ?", database, function),
            SqlValues.Statement("SELECT name, sealed FROM platform_function_secrets WHERE database_id = ? AND function_name = ?", database, function),
        ];
        var outcome = await store.ExecuteAsync(statements, SqlContext.Fast, cancellationToken);
        return outcome switch
        {
            SqlExecuteResponse { Results: var results } => results[0].FirstRow() is { } row
                ? new FunctionCode(row.Text(0), [.. results[1].RowsOf().Select(static secret => new SealedSecret(secret.Text(0), secret.Text(1)))])
                : Missing(),
            PlatformConflict conflict => new PlatformUnavailable(conflict.Message, "platform_state"),
            PlatformUnavailable unavailable => unavailable,
        };
    }

    public async ValueTask<RecordFunctionRunOutcome> RecordAsync(FunctionTarget target, string name, FunctionLogBody log, CancellationToken cancellationToken)
    {
        var database = SqlValues.Text(target.Database.ToString());
        var function = SqlValues.Text(name);
        IReadOnlyList<SqlHttpStatement> statements =
        [
            SqlValues.Statement(
                "INSERT INTO platform_function_logs (database_id, function_name, id, at, body) VALUES (?, ?, ?, ?, ?)",
                database, function, SqlValues.Text(log.Id), SqlValues.Number(log.At.UnixMs()),
                SqlValues.Text(JsonSerializer.Serialize(log, FunctionsJsonContext.Default.FunctionLogBody))),
            SqlValues.Statement(
                "DELETE FROM platform_function_logs WHERE database_id = ? AND function_name = ? AND id NOT IN "
                + "(SELECT id FROM platform_function_logs WHERE database_id = ? AND function_name = ? ORDER BY at DESC, id DESC LIMIT ?)",
                database, function, database, function, SqlValues.Number(KeptLogs)),
            FunctionStatsMath.Upsert(target, name, log),
            SqlValues.Statement(
                "DELETE FROM platform_function_stats WHERE database_id = ? AND function_name = ? AND minute < ?",
                database, function, SqlValues.Number(log.At.ToUnixTimeSeconds() - FunctionStatsMath.RetainedSeconds)),
        ];
        var outcome = await store.ExecuteAsync(statements, SqlContext.Fast, cancellationToken);
        return outcome switch
        {
            SqlExecuteResponse => new FunctionRunRecorded(),
            PlatformConflict conflict => new PlatformUnavailable(conflict.Message, "platform_state"),
            PlatformUnavailable unavailable => unavailable,
        };
    }

    private static PlatformNotFound Missing() => new("the function does not exist", "function_not_found");

    private static FunctionBody Body(SqlRow row, bool withSource) =>
        new(row.Text(0), withSource ? row.Text(1) : null, row.OptionalText(2), row.Number(3).FromUnixMs(), row.Number(4).FromUnixMs());

    private static FunctionLogBody? Log(string json) => TerraBaseAnswers.Parse(Encoding.UTF8.GetBytes(json), FunctionsJsonContext.Default.FunctionLogBody);
}
