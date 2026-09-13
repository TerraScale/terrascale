// <copyright file="QueryDtos.cs" company="TerraScale">
// TerraBase (SE-08). See LICENSE.md.
// </copyright>

namespace TerraBase.Api;

/// <summary>The query execution request.</summary>
public sealed record QueryRequestDto
{
    /// <summary>The query text (TQL).</summary>
    public required string Text { get; init; }

    /// <summary>The item bound; the default is 256.</summary>
    public uint? MaxItems { get; init; }
}

/// <summary>The query execution answer.</summary>
public sealed record QueryResponseDto(DocumentBodyDto[] Documents, uint Count, string Terminal);

/// <summary>The query-cancellation live signal (GAP-Q12, PB-4034).</summary>
public sealed record QueryCancelDto
{
    /// <summary>The in-flight query identity.</summary>
    public required ulong QueryId { get; init; }
}

/// <summary>The query-cancellation answer.</summary>
public sealed record QueryCancelResponseDto(string Outcome);
