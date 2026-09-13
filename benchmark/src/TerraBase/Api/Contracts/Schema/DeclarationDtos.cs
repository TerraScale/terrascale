// <copyright file="DeclarationDtos.cs" company="TerraScale">
// TerraBase (SE-08). See LICENSE.md.
// </copyright>

namespace TerraBase.Api;

/// <summary>The schema declaration request: the typed schema document, or <c>null</c> to clear the policy.</summary>
public sealed record SchemaDeclareDto
{
    /// <summary>The schema document of the documented JSON Schema subset.</summary>
    public System.Text.Json.JsonElement? Schema { get; init; }
}

/// <summary>The recorded schema state.</summary>
public sealed record SchemaStatusDto(ulong? EnforcedVersion, uint RuleCount);
