using System.Collections.Concurrent;
using TerraBase.Features.Callers;
using TerraBase.Features.Catalog;
using TerraBase.Features.Qos;
using TerraBase.Features.Rules;
using TerraBase.Features.Sql;
using TerraBase.Hosting;
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

    public async ValueTask<PostgresAuthenticateOutcome> AuthenticateAsync(PostgresStartup startup, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(startup);
        if (startup.Password is not { Length: > 0 } secret)
        {
            return new PostgresPasswordRejected("send an API key or access token as the password");
        }

        var authenticated = await authentication.AuthenticatePasswordAsync(secret, startup.ProjectReference, cancellationToken);
        return authenticated switch
        {
            Caller caller => await ResolveAsync(caller, startup, cancellationToken),
            CredentialRejected rejected => new PostgresPasswordRejected(rejected.Message),
            BackendUnavailable unavailable => new PostgresBackendUnavailable(unavailable.Message),
        };
    }

    private async ValueTask<PostgresAuthenticateOutcome> ResolveAsync(Caller caller, PostgresStartup startup, CancellationToken cancellationToken)
    {
        var resolved = await access.DataAsync(caller, startup.ProjectReference, startup.DatabaseReference, cancellationToken);
        return resolved switch
        {
            ResolvedDatabase target => await GrantAsync(target, cancellationToken),
            InvalidInput invalid => new PostgresAccessDenied(invalid.Message),
            CredentialRejected rejected => new PostgresPasswordRejected(rejected.Message),
            CallerForbidden forbidden => new PostgresAccessDenied(forbidden.Message),
            ResourceNotFound missing => new PostgresAccessDenied(missing.Message),
            BackendUnavailable unavailable => new PostgresBackendUnavailable(unavailable.Message),
        };
    }

    private async ValueTask<PostgresAuthenticateOutcome> GrantAsync(ResolvedDatabase target, CancellationToken cancellationToken)
    {
        var tenant = await tenancy.Tenants.ForAsync(target.Project, cancellationToken);
        return tenant switch
        {
            Tenant known => tenancy.Sessions.TryOpen(known) switch
            {
                TenantSessions.TenantSession session => await OpenAsync(target, session, cancellationToken),
                TenantBusy => new PostgresSessionLimitReached(TenantSessions.LimitMessage),
            },
            NoTenant => await OpenAsync(target, null, cancellationToken),
            BackendUnavailable unavailable => new PostgresBackendUnavailable(unavailable.Message),
        };
    }

    private async ValueTask<PostgresAuthenticateOutcome> OpenAsync(ResolvedDatabase target, IDisposable? tenantSession, CancellationToken cancellationToken)
    {
        var count = _sessions.AddOrUpdate(target.Database.Id, 1, static (_, current) => current + 1);
        var lease = new SessionLease(this, target.Database.Id, tenantSession);
        if (count > MaxSessionsPerDatabase)
        {
            await lease.DisposeAsync();
            return new PostgresSessionLimitReached("the database has too many Postgres sessions");
        }

        var opened = await access.OpenAsync(target.Database, cancellationToken);
        return opened switch
        {
            ISqlDatabaseSession session => new PostgresGrant(new TerraBaseDatabaseExecutor(target, session, coordinator, rules, runner), target.Database.Name,
                target.CanWrite ? SqlAccess.Write : SqlAccess.Read, lease, lifetime.ApplicationStopping),
            BackendUnavailable unavailable => await ReleaseAsync(lease, unavailable),
        };
    }

    private static async ValueTask<PostgresAuthenticateOutcome> ReleaseAsync(SessionLease lease, BackendUnavailable unavailable)
    {
        await lease.DisposeAsync();
        return new PostgresBackendUnavailable(unavailable.Message);
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
