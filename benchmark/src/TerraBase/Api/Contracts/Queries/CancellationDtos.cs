// <copyright file="CancellationDtos.cs" company="TerraScale">
// TerraBase (SE-08). See LICENSE.md.
// </copyright>

namespace TerraBase.Api;

/// <summary>The query-cancellation live signal (GAP-Q12, PB-4034).</summary>
public sealed record QueryCancelDto
{
    /// <summary>The in-flight query identity.</summary>
    public required ulong QueryId { get; init; }
}

/// <summary>The query-cancellation answer.</summary>
public sealed record QueryCancelResponseDto(string Outcome);
