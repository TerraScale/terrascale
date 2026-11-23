using System.Data;
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

    private const string Reclaim = "DELETE FROM mcp_clients WHERE first_sign_in_completed_at IS NULL AND closed_at IS NULL AND created_at <= @Cutoff";
    private const string ById = DashboardSql.McpClients.Select + " WHERE id = @Id";

    public static async Task ReclaimUnfinishedAsync(DashboardDb storage, DateTime now, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(storage);
        await using var connection = await storage.OpenAsync(cancellationToken);
        await connection.ExecuteAsync(Reclaim, new { Cutoff = now - UnfinishedLifetime });
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
            if (row.CreatedAt > now || row.LastActivityAt > now)
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

            if (expired)
            {
                row.ClosedAt = now;
            }
            else
            {
                row.FirstSignInCompletedAt ??= now;
                row.LastActivityAt = now;
            }

            row.OriginalVersion = row.Version;
            row.Version = AccountService.NewId();
            if (await connection.ExecuteAsync(DashboardSql.McpClients.Update, row) == 1)
            {
                return expired ? null : Registration(row);
            }
        }
        throw new IOException("MCP registration changed concurrently.");
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
