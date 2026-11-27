using System.Data.Common;
using System.Text.Json;
using Dapper;
using TerraScale.Dashboard.Platform.Persistence;

namespace TerraScale.Dashboard.Platform.Mcp;

public sealed record McpRegistration(string ClientId, string Name, string[] RedirectUris,
    DateTimeOffset CreatedAt, DateTimeOffset LastActivityAt,
    DateTimeOffset? FirstSignInCompletedAt = null, DateTimeOffset? ClosedAt = null);

public static class McpRegistrations
{
    public static readonly TimeSpan UnfinishedLifetime = TimeSpan.FromHours(24);
    public static readonly TimeSpan IdleLifetime = TimeSpan.FromDays(30);
    private static readonly TimeSpan ActivityResolution = TimeSpan.FromMinutes(5);
    private static readonly TimeSpan ClockSkew = TimeSpan.FromMinutes(2);

    public static async Task<McpRegistration> CreateAsync(DashboardDb storage, string name, string[] redirectUris, DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        var at = now.UtcDateTime;
        var row = new McpClientRow
        {
            Id = AccountService.NewId(),
            Name = name,
            RedirectUris = JsonSerializer.Serialize(redirectUris, McpRowJson.Default.StringArray),
            CreatedAt = at,
            LastActivityAt = at,
            Version = AccountService.NewId(),
        };
        await using var connection = await storage.OpenAsync(cancellationToken);
        await connection.ExecuteAsync(DashboardSql.McpClients.Insert, row);
        return Registration(row);
    }

    public static Task<McpRegistration?> ReadActiveAsync(DashboardDb storage, TimeProvider time, string clientId, CancellationToken cancellationToken)
    {
        return ReadAsync(storage, time, clientId, Activity.None, cancellationToken);
    }

    public static Task<McpRegistration?> CompleteSignInAsync(DashboardDb storage, TimeProvider time, string clientId, CancellationToken cancellationToken)
    {
        return ReadAsync(storage, time, clientId, Activity.SignIn, cancellationToken);
    }

    public static Task<McpRegistration?> TouchAsync(DashboardDb storage, TimeProvider time, string clientId, CancellationToken cancellationToken)
    {
        return ReadAsync(storage, time, clientId, Activity.Authenticated, cancellationToken);
    }

    private const string UnfinishedRows = "FROM mcp_clients m WHERE m.first_sign_in_completed_at IS NULL AND m.closed_at IS NULL AND m.created_at <= @Cutoff";
    private const string Unfinished = "SELECT m.id " + UnfinishedRows;
    private const string ClientBytes = "octet_length(m.id) + coalesce(octet_length(m.closed_at), 0) + octet_length(m.created_at) "
        + "+ coalesce(octet_length(m.first_sign_in_completed_at), 0) + octet_length(m.last_activity_at) + octet_length(m.name) "
        + "+ octet_length(m.redirect_uris) + octet_length(m.version)";

    internal const int ReclaimStepCount = 3;

    private static readonly string[] Reclaim =
    [
        "DELETE FROM access_tokens WHERE id IN (" + StorageSweeper.Budgeted(
            $"SELECT id, {StorageSweeper.AccessTokenBytes} AS size FROM access_tokens WHERE mcp_client_id IN ({Unfinished})") + ")",
        "DELETE FROM mcp_codes WHERE id IN (" + StorageSweeper.Budgeted(
            $"SELECT id, {StorageSweeper.McpCodeBytes} AS size FROM mcp_codes WHERE client_id IN ({Unfinished})") + ")",
        "DELETE FROM mcp_clients WHERE id IN (" + StorageSweeper.Budgeted(
            $"SELECT m.id, {ClientBytes} AS size {UnfinishedRows} "
            + "AND NOT EXISTS (SELECT 1 FROM access_tokens a WHERE a.mcp_client_id = m.id) "
            + "AND NOT EXISTS (SELECT 1 FROM mcp_codes c WHERE c.client_id = m.id)") + ")",
    ];

    private const string ById = DashboardSql.McpClients.Select + " WHERE id = @Id";

    internal static async Task<bool> ReclaimUnfinishedStepAsync(
        DashboardDb storage,
        TimeProvider time,
        long started,
        TimeSpan budget,
        int step,
        DateTime now,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(storage);
        ArgumentOutOfRangeException.ThrowIfNegative(step);
        ArgumentOutOfRangeException.ThrowIfGreaterThanOrEqual(step, Reclaim.Length);
        await using var connection = await storage.OpenAsync(cancellationToken);
        var sql = Reclaim[step];
        var cutoff = now - UnfinishedLifetime;
        return await StorageSweeper.DrainAsync(time, started, budget,
            () => connection.ExecuteAsync(sql, new { Cutoff = cutoff }), cancellationToken);
    }

    private enum Activity { None, SignIn, Authenticated }

    private static async Task<McpRegistration?> ReadAsync(DashboardDb storage, TimeProvider time, string clientId, Activity activity,
        CancellationToken cancellationToken)
    {
        if (!Canonical(clientId))
        {
            return null;
        }

        await using var connection = await storage.OpenAsync(cancellationToken);
        for (var attempt = 0; attempt < 8; attempt++)
        {
            var row = await connection.QuerySingleOrDefaultAsync<McpClientRow>(ById, new { Id = clientId });
            if (row is null || row.ClosedAt is not null)
            {
                return null;
            }

            var now = time.GetUtcNow().UtcDateTime;
            if (row.CreatedAt > now + ClockSkew || row.LastActivityAt > now + ClockSkew)
            {
                throw new IOException("MCP registration activity is in the future.");
            }

            var expired = row.FirstSignInCompletedAt is null
                ? now - row.CreatedAt >= UnfinishedLifetime
                : now - row.LastActivityAt >= IdleLifetime;
            if (!expired && activity == Activity.None)
            {
                return Registration(row);
            }

            if (!expired && activity == Activity.Authenticated && row.FirstSignInCompletedAt is null)
            {
                return null;
            }

            if (!expired && activity == Activity.Authenticated && now - row.LastActivityAt < ActivityResolution)
            {
                return Registration(row);
            }

            if (expired)
            {
                row.ClosedAt = now;
            }
            else
            {
                row.FirstSignInCompletedAt ??= now;
                row.LastActivityAt = row.LastActivityAt > now ? row.LastActivityAt : now;
            }

            row.OriginalVersion = row.Version;
            row.Version = AccountService.NewId();
            if (await UpdateAsync(connection, row) == 1)
            {
                return expired ? null : Registration(row);
            }

            await DashboardDb.ConflictBackoffAsync(attempt, time, cancellationToken);
        }
        throw new IOException("MCP registration changed concurrently.");
    }

    private static async Task<int> UpdateAsync(DbConnection connection, McpClientRow row)
    {
        try
        {
            return await connection.ExecuteAsync(DashboardSql.McpClients.Update, row);
        }
        catch (Exception error) when (DashboardDb.IsConflict(error))
        {
            return 0;
        }
    }

    private static McpRegistration Registration(McpClientRow row)
    {
        return new(row.Id, row.Name, JsonSerializer.Deserialize(row.RedirectUris, McpRowJson.Default.StringArray) ?? [], Utc(row.CreatedAt), Utc(row.LastActivityAt),
            row.FirstSignInCompletedAt is { } first ? Utc(first) : null, row.ClosedAt is { } closed ? Utc(closed) : null);
    }

    private static DateTimeOffset Utc(DateTime value)
    {
        return new(DateTime.SpecifyKind(value, DateTimeKind.Utc));
    }

    private static bool Canonical(string? clientId)
    {
        return clientId is { Length: 26 } && TerraScaleIds.IsValid(clientId) && !clientId.Any(char.IsLower);
    }
}
