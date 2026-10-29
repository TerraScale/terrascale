using Microsoft.Extensions.DependencyInjection.Extensions;

namespace TerraBase.Features.Sql;

/// <summary>Registers the relational SQL slice.</summary>
public static class SqlExtensions
{
    /// <summary>Registers <see cref="ISqlService"/>.</summary>
    public static IServiceCollection AddTerraBaseSql(this IServiceCollection services)
    {
        services.TryAddKeyedSingleton<TimeProvider>(SqlLimits.TransactionClock, (provider, _) => provider.GetRequiredService<TimeProvider>());
        return services.AddSingleton<ISqlService, SqlService>();
    }
}
