using System.Diagnostics.Metrics;
using Microsoft.Extensions.DependencyInjection.Extensions;
using TerraScale.TerraBase.Configuration;
using TerraScale.TerraBase.Hosting;

namespace TerraScale.TerraBase.Features.Memory;

/// <summary>Registers the process memory budget.</summary>
public static class MemoryBudgetExtensions
{
    /// <summary>Registers <see cref="IMemoryBudget"/> over <see cref="TerraBaseOptions.MemoryBudget"/>.</summary>
    public static IServiceCollection AddTerraBaseMemory(this IServiceCollection services, TerraBaseOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        services.TryAddSingleton(options);
        return services.AddMetrics().AddSingleton<IMemoryBudget>(provider =>
            new MemoryBudget(provider.GetRequiredService<TerraBaseOptions>().MemoryBudget, provider.GetRequiredService<IMeterFactory>().Create(Telemetry.MeterName)));
    }
}
