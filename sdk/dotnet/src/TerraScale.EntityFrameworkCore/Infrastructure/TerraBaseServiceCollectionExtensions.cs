using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.EntityFrameworkCore.Storage;
using Microsoft.EntityFrameworkCore.Update;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using TerraScale.EntityFrameworkCore.Migrations;
using TerraScale.EntityFrameworkCore.Storage;
using TerraScale.EntityFrameworkCore.Update;

namespace TerraScale.EntityFrameworkCore.Infrastructure;

internal static class TerraBaseServiceCollectionExtensions
{
    extension(IServiceCollection services)
    {
        public IServiceCollection AddEntityFrameworkTerraBase()
        {
            new EntityFrameworkRelationalServicesBuilder(services)
                .TryAdd<IDatabaseProvider, DatabaseProvider<TerraBaseOptionsExtension>>()
                .TryAdd<IRelationalConnection, TerraBaseRelationalConnection>()
                .TryAdd<IRelationalDatabaseCreator, TerraBaseDatabaseCreator>()
                .TryAdd<IExecutionStrategyFactory, TerraBaseExecutionStrategyFactory>()
                .TryAdd<IUpdateSqlGenerator, TerraBaseUpdateSqlGenerator>()
                .TryAdd<IModificationCommandBatchFactory, TerraBaseModificationCommandBatchFactory>()
                .TryAdd<IMigrationsSqlGenerator, TerraBaseMigrationsSqlGenerator>()
                .TryAdd<IHistoryRepository, TerraBaseHistoryRepository>()
                .TryAddProviderSpecificServices(provider => provider.TryAddSingleton<TerraBaseDataSources, TerraBaseDataSources>());
            services.AddEntityFrameworkSqlite();
            for (var i = services.Count - 1; i >= 0; i--)
            {
                if (services[i] is { ServiceType: var type } descriptor && type == typeof(IDatabaseProvider)
                    && (descriptor.ImplementationType ?? descriptor.ImplementationInstance?.GetType()) is { } implementation
                    && implementation != typeof(DatabaseProvider<TerraBaseOptionsExtension>))
                {
                    services.RemoveAt(i);
                }
            }

            return services;
        }
    }
}
