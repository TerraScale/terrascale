using Microsoft.Extensions.DependencyInjection.Extensions;
using TerraBase.Features.Sql.Routing;

namespace TerraBase.Features.Sql;

/// <summary>Registers the relational SQL slice.</summary>
public static class SqlExtensions
{
    /// <summary>Registers <see cref="ISqlService"/>.</summary>
    public static IServiceCollection AddTerraBaseSql(this IServiceCollection services)
    {
        services.TryAddKeyedSingleton<TimeProvider>(SqlLimits.TransactionClock, (provider, _) => provider.GetRequiredService<TimeProvider>());
        services.AddSingleton<SchemaFactsCache>();
        services.AddSingleton<WriteRouter>();
        return services.AddSingleton<ISqlService, SqlService>();
    }
}
