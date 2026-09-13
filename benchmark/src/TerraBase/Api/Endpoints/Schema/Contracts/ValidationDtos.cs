// <copyright file="ValidationDtos.cs" company="TerraScale">
// TerraBase (SE-08). See LICENSE.md.
// </copyright>

namespace TerraBase.Api;

/// <summary>The on-demand validation request: the document to validate.</summary>
public sealed record SchemaValidateDto
{
    /// <summary>
    /// The document value. Optional at the binding level so the local gate
    /// runs before the request's own shape is judged (the same ordering the
    /// document endpoints keep).
    /// </summary>
    public System.Text.Json.JsonElement? Document { get; init; }
}

/// <summary>The validation answer: the enforced version and the typed violations, never a field value.</summary>
public sealed record SchemaValidationDto(ulong? EnforcedVersion, bool Valid, ViolationDto[] Violations);

/// <summary>One typed policy violation of one document field.</summary>
/// <param name="Field">The violated field name, or empty when the document itself is the violation.</param>
/// <param name="Kind">The snake_case violation kind.</param>
public sealed record ViolationDto(string Field, string Kind);
