using Microsoft.EntityFrameworkCore;
using TerraScale.Dashboard.Platform.Persistence;

namespace TerraScale.Dashboard.Platform.Mcp;

public sealed record McpRegistration(string ClientId, string Name, string[] RedirectUris,
    DateTimeOffset CreatedAt, DateTimeOffset LastActivityAt,
    DateTimeOffset? FirstSignInCompletedAt = null, DateTimeOffset? ClosedAt = null);

public static class McpRegistrations
{
    public static readonly TimeSpan UnfinishedLifetime = TimeSpan.FromHours(24);
    public static readonly TimeSpan IdleLifetime = TimeSpan.FromDays(30);

    public static async Task<McpRegistration> CreateAsync(IDbContextFactory<DashboardDbContext> storage, string name, string[] redirectUris, DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        var at = now.UtcDateTime;
        var row = new McpClientRow
        {
            Id = AccountService.NewId(),
            Name = name,
            RedirectUris = [.. redirectUris],
            CreatedAt = at,
            LastActivityAt = at,
            Version = AccountService.NewId(),
        };
        await using var db = await storage.CreateDbContextAsync(cancellationToken);
        db.McpClients.Add(row);
        await db.SaveChangesAsync(cancellationToken);
        return Registration(row);
    }

    public static Task<McpRegistration?> ReadActiveAsync(IDbContextFactory<DashboardDbContext> storage, TimeProvider time, string clientId, CancellationToken cancellationToken)
    {
        return ReadAsync(storage, time, clientId, Activity.None, cancellationToken);
    }

    public static Task<McpRegistration?> CompleteSignInAsync(IDbContextFactory<DashboardDbContext> storage, TimeProvider time, string clientId, CancellationToken cancellationToken)
    {
        return ReadAsync(storage, time, clientId, Activity.SignIn, cancellationToken);
    }

    public static Task<McpRegistration?> TouchAsync(IDbContextFactory<DashboardDbContext> storage, TimeProvider time, string clientId, CancellationToken cancellationToken)
    {
        return ReadAsync(storage, time, clientId, Activity.Authenticated, cancellationToken);
    }

    public static async Task ReclaimUnfinishedAsync(DashboardDbContext db, DateTime now, CancellationToken cancellationToken)
    {
        var cutoff = now - UnfinishedLifetime;
        await db.McpClients.Where(row => row.FirstSignInCompletedAt == null && row.ClosedAt == null && row.CreatedAt <= cutoff).ExecuteDeleteAsync(cancellationToken);
    }

    private enum Activity { None, SignIn, Authenticated }

    private static async Task<McpRegistration?> ReadAsync(IDbContextFactory<DashboardDbContext> storage, TimeProvider time, string clientId, Activity activity,
        CancellationToken cancellationToken)
    {
        if (!Canonical(clientId))
        {
            return null;
        }

        await using var db = await storage.CreateDbContextAsync(cancellationToken);
        for (var attempt = 0; attempt < 8; attempt++)
        {
            db.ChangeTracker.Clear();
            var row = await db.McpClients.SingleOrDefaultAsync(client => client.Id == clientId, cancellationToken);
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

            row.Version = AccountService.NewId();
            try
            {
                await db.SaveChangesAsync(cancellationToken);
                return expired ? null : Registration(row);
            }
            catch (DbUpdateConcurrencyException)
            {
            }
        }
        throw new IOException("MCP registration changed concurrently.");
    }

    private static McpRegistration Registration(McpClientRow row)
    {
        return new(row.Id, row.Name, [.. row.RedirectUris], Utc(row.CreatedAt), Utc(row.LastActivityAt),
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
