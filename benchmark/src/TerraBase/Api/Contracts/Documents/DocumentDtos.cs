// <copyright file="DocumentDtos.cs" company="TerraScale">
// TerraBase (SE-08). See LICENSE.md.
// </copyright>

namespace TerraBase.Api;

using TerraBase.Protocol;

/// <summary>One document body: the identity, the version, and the body.</summary>
public sealed record DocumentBodyDto(string Id, string Collection, ulong Version, JsonValue Body)
{
    /// <summary>Builds from the protocol body.</summary>
    /// <param name="body">The protocol body.</param>
    /// <returns>The DTO.</returns>
    public static DocumentBodyDto Of(DocumentBody body) => new(body.Id, body.Collection, body.Version, JsonValue.Of(body.Body));
}

/// <summary>The document read answer.</summary>
public sealed record DocumentReadDto(bool Found, DocumentBodyDto? Document);
