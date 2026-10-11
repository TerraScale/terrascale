using System.Runtime.CompilerServices;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using TerraBase.Features.CrossRegion;

namespace TerraBase.Testing;

/// <summary>
/// Request/reply between the regions of a test by subject, as <see cref="FakeRegionNetwork"/> carries it: tests decorate it
/// to drop, delay or rewrite one member's traffic. The harness puts it under a server's <see cref="IRegionPeers"/>.
/// </summary>
internal interface IRegionBus
{
    /// <summary>Sends <paramref name="request"/> to every responder of <paramref name="subject"/> and yields their replies as they come.</summary>
    IAsyncEnumerable<ReadOnlyMemory<byte>> RequestManyAsync(string subject, ReadOnlyMemory<byte> request, CancellationToken cancellationToken);

    /// <summary>The reply of one responder of <paramref name="subject"/>; throws when none answers in time.</summary>
    ValueTask<ReadOnlyMemory<byte>> RequestAsync(string subject, ReadOnlyMemory<byte> request, CancellationToken cancellationToken);

    /// <summary>Answers every request on <paramref name="subject"/> with <paramref name="answer"/> until <paramref name="cancellationToken"/> ends.</summary>
    Task ServeAsync(string subject, Func<ReadOnlyMemory<byte>, CancellationToken, ValueTask<byte[]>> answer, CancellationToken cancellationToken);
}

/// <summary>The subjects of a test's region bus: one every member of a cell answers, and one per member.</summary>
internal static class RegionSubjects
{
    /// <summary>The subject every member of <paramref name="cell"/> answers with the first page of its heads.</summary>
    public static string Heads(string cell) => $"wan.{cell}.heads";

    /// <summary>The subject of one member of <paramref name="cell"/>.</summary>
    public static string Member(string cell, string member) => $"wan.{cell}.member.{member}";
}

/// <summary>A server's <see cref="IRegionPeers"/> over a test's region bus; <paramref name="cells"/> names each region's cell.</summary>
internal sealed class BusRegionPeers(IRegionBus bus, IReadOnlyDictionary<string, string> cells, string region) : IRegionPeers
{
    public ValueTask<IReadOnlyList<string>> RegionsAsync(CancellationToken cancellationToken) =>
        ValueTask.FromResult<IReadOnlyList<string>>([.. cells.Keys.Where(other => other != region)]);

    public async IAsyncEnumerable<ReadOnlyMemory<byte>> RequestManyAsync(string region, ReadOnlyMemory<byte> request, [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        await foreach (var reply in bus.RequestManyAsync(RegionSubjects.Heads(cells[region]), request, cancellationToken))
        {
            yield return reply;
        }
    }

    public ValueTask<ReadOnlyMemory<byte>> RequestAsync(string region, string member, ReadOnlyMemory<byte> request, CancellationToken cancellationToken) =>
        bus.RequestAsync(RegionSubjects.Member(cells[region], member), request, cancellationToken);

    /// <summary>
    /// Answers the server's cell and member subjects with its <see cref="WanResponder"/> while it runs, as
    /// <c>Wan.Ask</c> does on its peer listener.
    /// </summary>
    public sealed class Responder(IRegionBus bus, WanResponder responder, string cell, string member) : BackgroundService
    {
        protected override Task ExecuteAsync(CancellationToken stoppingToken) => Task.WhenAll(
            bus.ServeAsync(RegionSubjects.Heads(cell), responder.AnswerAsync, stoppingToken),
            bus.ServeAsync(RegionSubjects.Member(cell, member), responder.AnswerAsync, stoppingToken));
    }

    /// <summary>Puts the test's region bus registered in <paramref name="services"/>, if any, under the server.</summary>
    public static void Register(IServiceCollection services, IReadOnlyDictionary<string, string> cells, string region, string member)
    {
        // Without its own cell the server knows no region of the bus.
        if (services.LastOrDefault(service => service.ServiceType == typeof(IRegionBus)) is not { } registered || !cells.ContainsKey(region))
        {
            return;
        }

        var bus = (IRegionBus)registered.ImplementationInstance!;
        services.AddSingleton<IRegionPeers>(new BusRegionPeers(bus, cells, region));
        services.AddSingleton<IHostedService>(provider => new Responder(bus, provider.GetRequiredService<WanResponder>(), cells[region], member));
    }
}
