// <copyright file="StreamDtos.cs" company="TerraScale">
// TerraBase (SE-08). See LICENSE.md.
// </copyright>

namespace TerraBase.Api;

/// <summary>The stream acknowledgement request.</summary>
public sealed record StreamAckDto
{
    /// <summary>The last sequence the consumer held.</summary>
    public required ulong ThroughSequence { get; init; }
}

/// <summary>The stream acknowledgement answer.</summary>
public sealed record StreamAckResponseDto(ulong SubscriptionId, ulong? AcknowledgedThrough);
