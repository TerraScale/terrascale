// <copyright file="BackboneSource.cs" company="TerraScale">
// TerraBase (SE-08). See LICENSE.md.
// </copyright>

namespace TerraBase.Api;

using TerraBase.Protocol;

/// <summary>
/// The journal-facing source for one API backbone stream. The source owns
/// sequence assignment and delivers replayed and live notifications through
/// one ordered sink. The hub owns only the client-facing bounded queue.
/// </summary>
internal interface IBackboneSource
{
    /// <summary>The engine generation loss notification.</summary>
    event Action<int>? GenerationLost;

    /// <summary>Subscribes one sink to a tenant-scoped journal stream.</summary>
    /// <param name="subscriptionId">The caller-assigned subscription identity.</param>
    /// <param name="tenant">The tenant scope.</param>
    /// <param name="principal">The principal scope.</param>
    /// <param name="collections">The collection filter, or <c>null</c> for all collections.</param>
    /// <param name="fromSequence">The inclusive replay start, or <c>null</c> for live-only.</param>
    /// <param name="sink">The ordered notification sink.</param>
    /// <param name="cancellationToken">A cancellation token.</param>
    /// <returns>The source-owned subscription identity and replay cursor.</returns>
    Task<BackboneSourceSubscription> SubscribeAsync(
        ulong subscriptionId,
        string tenant,
        string principal,
        string[]? collections,
        ulong? fromSequence,
        IBackboneNotificationSink sink,
        CancellationToken cancellationToken = default);

    /// <summary>Removes one source subscription.</summary>
    /// <param name="subscriptionId">The subscription identity.</param>
    /// <param name="tenant">The tenant scope.</param>
    /// <param name="principal">The principal scope.</param>
    /// <param name="cancellationToken">A cancellation token.</param>
    Task UnsubscribeAsync(
        ulong subscriptionId,
        string tenant,
        string principal,
        CancellationToken cancellationToken = default);
}

/// <summary>The notification sink shared by journal and TBEP sources.</summary>
internal interface IBackboneNotificationSink : TbepChannel.ITbepNotificationSink
{
}

/// <summary>The source-owned identity and replay cursor returned by subscribe.</summary>
/// <param name="SubscriptionId">The source subscription identity.</param>
/// <param name="StartSequence">The first sequence not covered by replay.</param>
internal sealed record BackboneSourceSubscription(ulong SubscriptionId, ulong StartSequence);

/// <summary>The current TBEP-backed source adapter.</summary>
internal sealed class TbepBackboneSource(EngineSupervisor supervisor) : IBackboneSource
{
    /// <inheritdoc/>
    public event Action<int>? GenerationLost
    {
        add => supervisor.EngineLost += value;
        remove => supervisor.EngineLost -= value;
    }

    /// <inheritdoc/>
    public async Task<BackboneSourceSubscription> SubscribeAsync(
        ulong subscriptionId,
        string tenant,
        string principal,
        string[]? collections,
        ulong? fromSequence,
        IBackboneNotificationSink sink,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(sink);
        var channel = await supervisor.ChannelForAsync(tenant, principal, cancellationToken).ConfigureAwait(false);
        var response = await channel.BackboneSubscribeAsync(
            subscriptionId,
            collections,
            fromSequence,
            sink,
            cancellationToken).ConfigureAwait(false);
        return new BackboneSourceSubscription(response.SubscriptionId, response.StartSequence);
    }

    /// <inheritdoc/>
    public async Task UnsubscribeAsync(
        ulong subscriptionId,
        string tenant,
        string principal,
        CancellationToken cancellationToken = default)
    {
        var channel = await supervisor.ChannelForAsync(tenant, principal, cancellationToken).ConfigureAwait(false);
        await channel.BackboneUnsubscribeAsync(subscriptionId, cancellationToken).ConfigureAwait(false);
    }
}
