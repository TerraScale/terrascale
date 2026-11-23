using Microsoft.AspNetCore.Connections;
using Microsoft.AspNetCore.Server.Kestrel.Core;
using Microsoft.Extensions.DependencyInjection;

namespace TerraBase.Postgres.Hosting;

public static class PostgresServiceCollectionExtensions
{
    extension(IServiceCollection services)
    {
        public IServiceCollection AddTerraBasePostgres(PostgresServerOptions options)
        {
            ArgumentNullException.ThrowIfNull(options);
            services.AddSingleton(options);
            services.AddSingleton<PostgresBackendRegistry>();
            services.AddSingleton<PostgresConnectionHandler>();
            return services;
        }
    }

    extension(ListenOptions listenOptions)
    {
        public ListenOptions UseTerraBasePostgres()
        {
            listenOptions.UseConnectionHandler<PostgresConnectionHandler>();
            return listenOptions;
        }
    }
}
