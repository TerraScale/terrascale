using System.Diagnostics;
using TerraBase.Features.ApiKeys;
using TerraBase.Hosting;
using TerraBase.Tenancy;

namespace TerraBase.Features.Pressure;

public interface IPressureSignals
{
    PressureBody Snapshot { get; }

    void Record(TerraBaseListener listener, TimeSpan elapsed, string? organization, PlanTier? plan = null);

    void ConnectionOpened();

    void ConnectionClosed();
}

public static class PressureExtensions
{
    public static IServiceCollection AddTerraBasePressure(this IServiceCollection services)
    {
        services.AddSingleton<IPressureSignals, PressureSignals>();
        services.AddHostedService(provider => (PressureSignals)provider.GetRequiredService<IPressureSignals>());
        return services;
    }

    public static WebApplication UseTerraBasePressure(this WebApplication app)
    {
        var pressure = app.Services.GetRequiredService<IPressureSignals>();
        app.Use(async (context, next) =>
        {
            var started = Stopwatch.GetTimestamp();
            try
            {
                await next(context).ConfigureAwait(false);
            }
            finally
            {
                if (Listeners.ArrivedOn(context) is { } listener)
                {
                    pressure.Record(listener, Stopwatch.GetElapsedTime(started), context.Features.Get<ApiKey>()?.OrganizationId,
                        context.Features.Get<TenantContext>()?.Plan);
                }
            }
        });
        return app;
    }
}
