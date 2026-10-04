namespace TerraScale.TerraBase.Features.Membership;

/// <summary>Member lifecycle, in order.</summary>
public enum MemberState
{
    /// <summary>Proposed, not certified.</summary>
    Proposed,

    /// <summary>Certified into the configuration.</summary>
    Certified,

    /// <summary>Copying state as a learner.</summary>
    Copying,

    /// <summary>Caught up, not yet serving acknowledgments.</summary>
    CaughtUp,

    /// <summary>Serving; may satisfy acknowledgments.</summary>
    Active,

    /// <summary>Handing off before retirement.</summary>
    Draining,

    /// <summary>Retired; must bootstrap again to return.</summary>
    Retired,
}

/// <summary>One member of a certified configuration.</summary>
/// <param name="MemberId">Stable member identifier.</param>
/// <param name="Endpoint">gRPC address of the member's peer listener.</param>
/// <param name="State">Lifecycle state.</param>
public sealed record Member(Ulid MemberId, Uri Endpoint, MemberState State);

/// <summary>A signed, certified membership configuration.</summary>
/// <param name="Epoch">Monotonic membership epoch.</param>
/// <param name="ConfigurationId">SHA-256 identity of the certified configuration.</param>
/// <param name="Members">Members of this configuration.</param>
public sealed record MembershipConfiguration(ulong Epoch, ReadOnlyMemory<byte> ConfigurationId, IReadOnlyList<Member> Members);

/// <summary>Source of the current certified membership configuration. Not implemented yet.</summary>
public interface IMembershipAuthority
{
    /// <summary>The current certified configuration.</summary>
    ValueTask<MembershipConfiguration> GetCurrentAsync(CancellationToken cancellationToken);
}
