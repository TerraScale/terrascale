// <copyright file="SessionDtos.cs" company="TerraScale">
// TerraBase (SE-08). See LICENSE.md.
// </copyright>

namespace TerraBase.Api;

/// <summary>The sync session setup request: a collection scope and an optional resume checkpoint.</summary>
public sealed record SyncSessionSetupDto
{
    /// <summary>The collection scope.</summary>
    public required string Collection { get; init; }

    /// <summary>The resume checkpoint bytes, base64, or <c>null</c> for a fresh session.</summary>
    public string? Resume { get; init; }

    /// <summary>The decoded resume checkpoint, or <c>null</c>.</summary>
    public byte[]? ResumeBytes => this.Resume is null ? null : Convert.FromBase64String(this.Resume);
}

/// <summary>The sync session setup answer.</summary>
public sealed record SyncSessionDto(ulong SessionId, byte[] Checkpoint, byte[] RetainedFloor);

/// <summary>One live sync session of the caller's tenant.</summary>
public sealed record SyncSessionSummaryDto(ulong SessionId, string Collection, int Generation);
