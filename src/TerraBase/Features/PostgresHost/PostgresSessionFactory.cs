using System.Collections.Concurrent;
using TerraBase.Features.Callers;
using TerraBase.Features.Catalog;
using TerraBase.Features.Qos;
using TerraBase.Features.Rules;
using TerraBase.Features.Sql;
using TerraBase.Postgres.Hosting;
using TerraScale.Contracts.Cluster;

namespace TerraBase.Features.PostgresHost;

internal sealed record PostgresTenancy(TenantDirectory Tenants, TenantSessions Sessions);

internal sealed class PostgresSessionFactory(
    CallerAuthentication authentication,
    DatabaseAccess access,
    IClusterCoordinator coordinator,
    RulesEnforcer rules,
    SqlRunner runner,
    PostgresTenancy tenancy,
    IHostApplicationLifetime lifetime) : IPostgresSessionFactory
{
    public const int MaxSessionsPerDatabase = 256;

    private readonly ConcurrentDictionary<Ulid, int> _sessions = new();

    public async ValueTask<Result<PostgresGrant, Unauthorized, Forbidden, QuotaExceeded, Unavailable>> AuthenticateAsync(
        PostgresStartup startup, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(startup);
        if (startup.Password is not { Length: > 0 } secret)
        {
            return new Unauthorized("send an API key or access token as the password", "unauthorized");
        }

        return await authentication.AuthenticatePasswordAsync(secret, startup.ProjectReference, cancellationToken) switch
        {
            Caller caller => await access.DataAsync(caller, startup.ProjectReference, startup.DatabaseReference, cancellationToken) switch
            {
                ResolvedDatabase target => await GrantAsync(target, cancellationToken),
                Validation invalid => new Forbidden(invalid.Message, invalid.Code),
                Unauthorized unauthorized => unauthorized,
                Forbidden forbidden => forbidden,
                NotFound missing => new Forbidden(missing.Message ?? "the database does not exist", missing.Code),
                Unavailable unavailable => unavailable,
            },
            Unauthorized unauthorized => unauthorized,
            Unavailable unavailable => unavailable,
        };
    }

    private async ValueTask<Result<PostgresGrant, Unauthorized, Forbidden, QuotaExceeded, Unavailable>> GrantAsync(ResolvedDatabase target, CancellationToken cancellationToken)
    {
        var tenant = await tenancy.Tenants.ForAsync(target.Project, cancellationToken);
        return tenant switch
        {
            Tenant known => tenancy.Sessions.TryOpen(known) switch
            {
                TenantSessions.TenantSession session => await OpenAsync(target, session, cancellationToken),
                TenantBusy => new QuotaExceeded(TenantSessions.LimitMessage, "tenant_busy"),
            },
            NoTenant => await OpenAsync(target, null, cancellationToken),
            Unavailable unavailable => unavailable,
        };
    }

    private async ValueTask<Result<PostgresGrant, Unauthorized, Forbidden, QuotaExceeded, Unavailable>> OpenAsync(
        ResolvedDatabase target, IDisposable? tenantSession, CancellationToken cancellationToken)
    {
        var count = _sessions.AddOrUpdate(target.Database.Id, 1, static (_, current) => current + 1);
        var lease = new SessionLease(this, target.Database.Id, tenantSession);
        if (count > MaxSessionsPerDatabase)
        {
            await lease.DisposeAsync();
            return new QuotaExceeded("the database has too many Postgres sessions", "too_many_sessions");
        }

        return await access.OpenAsync(target.Database, cancellationToken) switch
        {
            ISqlDatabaseSession session => new PostgresGrant(new TerraBaseDatabaseExecutor(target, session, coordinator, rules, runner), target.Database.Name,
                target.CanWrite ? SqlAccess.Write : SqlAccess.Read, lease, lifetime.ApplicationStopping),
            Unavailable unavailable => await ReleaseAsync(lease, unavailable),
        };
    }

    private static async ValueTask<Result<PostgresGrant, Unauthorized, Forbidden, QuotaExceeded, Unavailable>> ReleaseAsync(SessionLease lease, Unavailable unavailable)
    {
        await lease.DisposeAsync();
        return unavailable;
    }

    private void Release(Ulid databaseId)
    {
        if (_sessions.AddOrUpdate(databaseId, 0, static (_, current) => current - 1) <= 0)
        {
            _sessions.TryRemove(new KeyValuePair<Ulid, int>(databaseId, 0));
        }
    }

    private sealed class SessionLease(PostgresSessionFactory owner, Ulid databaseId, IDisposable? tenantSession) : IAsyncDisposable
    {
        private int _released;

        public ValueTask DisposeAsync()
        {
            if (Interlocked.Exchange(ref _released, 1) == 0)
            {
                owner.Release(databaseId);
                tenantSession?.Dispose();
            }

            return ValueTask.CompletedTask;
        }
    }
}
