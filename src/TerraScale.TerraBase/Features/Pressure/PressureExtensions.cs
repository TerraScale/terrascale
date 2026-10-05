using System.Diagnostics;
using TerraScale.TerraBase.Features.ApiKeys;
using TerraScale.TerraBase.Hosting;
using TerraScale.TerraBase.Tenancy;

namespace TerraScale.TerraBase.Features.Pressure;

/// <summary>Cached node telemetry and HTTP request accounting.</summary>
public interface IPressureSignals
{
    /// <summary>The most recent immutable sample; fetching never scans or changes its timestamp.</summary>
    PressureBody Snapshot { get; }

    /// <summary>Records a completed request; the organization must come from a validated API key.</summary>
    void Record(TerraBaseListener listener, TimeSpan elapsed, string? organization, PlanTier? plan = null);

    /// <summary>Accounts for a client transport connection opening or closing.</summary>
    void ConnectionOpened();

    /// <summary>Accounts for a client transport connection closing.</summary>
    void ConnectionClosed();
}

/// <summary>Registers pressure sampling and records requests on their actual listener.</summary>
public static class PressureExtensions
{
    /// <summary>Registers host-scoped counters, metrics and background sampling.</summary>
    public static IServiceCollection AddTerraBasePressure(this IServiceCollection services)
    {
        services.AddSingleton<IPressureSignals, PressureSignals>();
        services.AddHostedService(provider => (PressureSignals)provider.GetRequiredService<IPressureSignals>());
        return services;
    }

    /// <summary>Counts completed requests, including errors, before listener and authorization checks.</summary>
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
