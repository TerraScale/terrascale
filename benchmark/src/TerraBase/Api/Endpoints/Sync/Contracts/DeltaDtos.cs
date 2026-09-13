// <copyright file="DeltaDtos.cs" company="TerraScale">
// TerraBase (SE-08). See LICENSE.md.
// </copyright>

namespace TerraBase.Api;

/// <summary>The delta pull request.</summary>
public sealed record SyncDeltaDto
{
    /// <summary>The checkpoint bytes to serve after, base64.</summary>
    public required string From { get; init; }

    /// <summary>The decoded checkpoint bytes.</summary>
    public byte[] FromBytes => Convert.FromBase64String(this.From);
}

/// <summary>The delta pull answer.</summary>
public sealed record SyncDeltaResponseDto(
    ulong SessionId,
    string PayloadCodec,
    byte[] Payload,
    byte[] Next,
    uint Commits,
    bool SingleCommitOversize);

/// <summary>The snapshot pull answer.</summary>
public sealed record SyncSnapshotResponseDto(ulong SessionId, string PayloadCodec, byte[] Payload, byte[] Checkpoint);

/// <summary>The checkpoint acknowledgement request.</summary>
public sealed record SyncAckDto
{
    /// <summary>The durable checkpoint bytes, base64.</summary>
    public required string Checkpoint { get; init; }

    /// <summary>The decoded checkpoint bytes.</summary>
    public byte[] CheckpointBytes => Convert.FromBase64String(this.Checkpoint);
}

/// <summary>The acknowledged answer.</summary>
public sealed record SyncAckedDto(ulong SessionId);
