// <copyright file="ShutdownDtos.cs" company="TerraScale">
// TerraBase (SE-08). See LICENSE.md.
// </copyright>

namespace TerraBase.Api;

/// <summary>The engine shutdown request.</summary>
public sealed record ShutdownRequestDto
{
    /// <summary>The shutdown reason: <c>operator</c> (default) or <c>supervisor</c>.</summary>
    public string? Reason { get; init; }
}

/// <summary>The engine shutdown answer.</summary>
public sealed record ShutdownResponseDto(string State, uint InFlight);
