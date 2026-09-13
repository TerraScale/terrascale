// <copyright file="FamilyDtos.cs" company="TerraScale">
// TerraBase (SE-08). See LICENSE.md.
// </copyright>

namespace TerraBase.Api;

/// <summary>The typed family request: a codec name and its bytes.</summary>
public sealed record FamilyRequestDto
{
    /// <summary>The request codec identity.</summary>
    public required string RequestCodec { get; init; }

    /// <summary>The request bytes, base64.</summary>
    public required string Request { get; init; }

    /// <summary>The decoded request bytes.</summary>
    public byte[] RequestBytes => Convert.FromBase64String(this.Request);
}

/// <summary>The typed family answer.</summary>
public sealed record FamilyResponseDto(string ResponseCodec, byte[] Response);
