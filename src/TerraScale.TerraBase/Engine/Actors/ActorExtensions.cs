using System.Diagnostics.Metrics;
using TerraBase.Hosting;

namespace TerraBase.Engine.Actors;

/// <summary>Opt-in registration of the preparatory actor transport; production storage does not yet consume it.</summary>
public static class ActorExtensions
{
    /// <summary>Registers one router with independent, bounded mailboxes. No worker is created until it is resolved.</summary>
    /// <param name="services">The host's services.</param>
    /// <param name="actorCount">Number of logical actors, between 1 and 256.</param>
    /// <param name="maxItemsPerActor">Maximum queued plus executing callbacks per actor.</param>
    /// <param name="maxBytesPerActor">Maximum retained payload bytes per actor.</param>
    /// <returns>The services.</returns>
    public static IServiceCollection AddTerraBaseActors(
        this IServiceCollection services, int actorCount, int maxItemsPerActor = 256, int maxBytesPerActor = 2 * 1024 * 1024)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentOutOfRangeException.ThrowIfLessThan(actorCount, 1);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(actorCount, 256);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maxItemsPerActor);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maxBytesPerActor);
        services.AddMetrics();
        return services.AddSingleton<IActorRouter>(provider => new ChannelActorRouter(
            actorCount, maxItemsPerActor, maxBytesPerActor,
            provider.GetRequiredService<IMeterFactory>().Create(Telemetry.MeterName)));
    }
}
