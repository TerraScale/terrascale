using System.Diagnostics.Metrics;
using Microsoft.Extensions.DependencyInjection.Extensions;
using TerraBase.Configuration;
using TerraBase.Hosting;

namespace TerraBase.Features.Memory;

public static class MemoryBudgetExtensions
{
    public static IServiceCollection AddTerraBaseMemory(this IServiceCollection services, TerraBaseOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        services.TryAddSingleton(options);
        services.AddMetrics();
        services.TryAddSingleton<IMemoryBudget>(provider =>
            new MemoryBudget(provider.GetRequiredService<TerraBaseOptions>().MemoryBudget, provider.GetRequiredService<IMeterFactory>().Create(Telemetry.MeterName)));
        return services;
    }
}
