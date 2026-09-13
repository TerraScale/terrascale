// <copyright file="ExecutionDtos.cs" company="TerraScale">
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
