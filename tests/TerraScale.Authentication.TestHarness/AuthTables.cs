using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using TerraScale.Authentication.Contracts;
using TerraScale.Authentication.Features.Oidc;
using TerraScale.Authentication.Storage;
using TerraScale.DotNet.TerraBase;

namespace TerraScale.Authentication.Tests;

public static class AuthTables
{
    extension(TerraBaseSqlDatabase database)
    {
        public AuthDbContext Context() =>
            new(new DbContextOptionsBuilder<AuthDbContext>().UseTerraBase(database).UseQueryTrackingBehavior(QueryTrackingBehavior.NoTracking).Options);

        public AuthData Data() =>
            new(new PooledDbContextFactory<AuthDbContext>(new DbContextOptionsBuilder<AuthDbContext>().UseTerraBase(database)
                .UseQueryTrackingBehavior(QueryTrackingBehavior.NoTracking).Options));

        public async Task MigrateAsync(CancellationToken cancellationToken = default)
        {
            await using var db = database.Context();
            await db.Database.MigrateAsync(cancellationToken);
        }

        public async Task<T> ReadAsync<T>(Func<AuthDbContext, Task<T>> read)
        {
            ArgumentNullException.ThrowIfNull(read);
            await using var db = database.Context();
            return await read(db);
        }

        public async Task<IReadOnlyList<string>> TableNamesAsync(CancellationToken cancellationToken = default) =>
            [.. (await database.QueryAsync("SELECT name FROM sqlite_schema WHERE type = 'table' ORDER BY name", cancellationToken: cancellationToken)).Select(row => row.GetString(0))];
    }

    extension(AuthData data)
    {
        public async Task<ProjectRecord> SeedProjectAsync(string projectId, string name = "Seeded", CancellationToken cancellationToken = default)
        {
            ArgumentNullException.ThrowIfNull(data);
            if (await data.ReadAsync((db, token) => db.Projects.SingleOrDefaultAsync(project => project.Id == projectId, token), cancellationToken) is { } existing)
            {
                return existing;
            }

            var now = DateTimeOffset.UtcNow;
            var organization = new OrganizationRecord(AuthIds.New(), OidcIssuer.InstanceId, name, AuthIds.New(), now, Owners: 1, Projects: 1);
            var project = new ProjectRecord(projectId, OidcIssuer.InstanceId, organization.Id, name, organization.CreatedBy, now);
            await data.SaveAsync([Change.Insert(organization), Change.Insert(project)], cancellationToken);
            return project;
        }
    }
}
