using Microsoft.Extensions.DependencyInjection.Extensions;
using TerraBase.Features.Sql.Routing;

namespace TerraBase.Features.Sql;

public static class SqlExtensions
{
    public static IServiceCollection AddTerraBaseSql(this IServiceCollection services)
    {
        services.TryAddKeyedSingleton<TimeProvider>(SqlLimits.TransactionClock, (provider, _) => provider.GetRequiredService<TimeProvider>());
        services.AddSingleton<SchemaFactsCache>();
        services.AddSingleton<WriteRouter>();
        return services.AddSingleton<ISqlService, SqlService>();
    }
}
