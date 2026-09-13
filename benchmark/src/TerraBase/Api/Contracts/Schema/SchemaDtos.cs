// <copyright file="SchemaDtos.cs" company="TerraScale">
// TerraBase (SE-08). See LICENSE.md.
// </copyright>

namespace TerraBase.Api;

using System.Text.Json;
using TerraBase.Protocol;

// ---------------------------------------------------------------------------
// The schema surface (SM-04): typed schema-lifecycle DTOs. The schema
// document and the validated document arrive as ordinary JSON and convert
// to the protocol's typed value at the boundary — never as bytes, never as
// an opaque codec payload, so the closed keyword set of the documented
// JSON Schema subset is the only shape the surface accepts.
// ---------------------------------------------------------------------------

/// <summary>Conversions between plain JSON elements and the protocol's typed value.</summary>
public static class JsonValues
{
    /// <summary>Converts one plain JSON element to the protocol's typed value.</summary>
    /// <param name="element">The JSON element.</param>
    /// <returns>The <see cref="TbepValue"/>.</returns>
    /// <exception cref="JsonException">The element is not a value the protocol carries.</exception>
    public static TbepValue ToValue(JsonElement element) => element.ValueKind switch
    {
        JsonValueKind.Null or JsonValueKind.Undefined => TbepValue.Null,
        JsonValueKind.True => TbepValue.Of(true),
        JsonValueKind.False => TbepValue.Of(false),
        JsonValueKind.Number when element.TryGetInt64(out var integer) => TbepValue.Of(integer),
        JsonValueKind.Number => TbepValue.Of(element.GetDouble()),
        JsonValueKind.String => TbepValue.Of(element.GetString()!),
        JsonValueKind.Array => TbepValue.Of(element.EnumerateArray().Select(ToValue).ToArray()),
        JsonValueKind.Object => TbepValue.Of(
            element.EnumerateObject().Select(property => new ObjectField(property.Name, ToValue(property.Value))).ToArray()),
        _ => throw new JsonException($"a JSON kind the protocol does not carry: {element.ValueKind}"),
    };

    /// <summary>Converts the protocol's typed value to a serializable JSON object.</summary>
    /// <param name="value">The <see cref="TbepValue"/>.</param>
    /// <returns>A boxed JSON value.</returns>
    /// <exception cref="JsonException">The value carries an unknown tag.</exception>
    public static object? ToJson(TbepValue value) => value.Tag switch
    {
        0 => null,
        1 => (bool)value.Payload!,
        2 => (long)value.Payload!,
        3 => (double)value.Payload!,
        4 => (string)value.Payload!,
        5 => ((TbepValue[])value.Payload!).Select(ToJson).ToArray(),
        6 => ((ObjectField[])value.Payload!).ToDictionary(field => field.Name, field => ToJson(field.Value)),
        _ => throw new JsonException($"unknown value tag {value.Tag}"),
    };
}

/// <summary>The schema declaration request: the typed schema document, or <c>null</c> to clear the policy.</summary>
public sealed record SchemaDeclareDto
{
    /// <summary>The schema document of the documented JSON Schema subset.</summary>
    public JsonElement? Schema { get; init; }
}

/// <summary>The recorded schema state.</summary>
public sealed record SchemaStatusDto(ulong? EnforcedVersion, uint RuleCount);

/// <summary>The on-demand validation request: the document to validate.</summary>
public sealed record SchemaValidateDto
{
    /// <summary>
    /// The document value. Optional at the binding level so the local gate
    /// runs before the request's own shape is judged (the same ordering the
    /// document endpoints keep).
    /// </summary>
    public JsonElement? Document { get; init; }
}

/// <summary>The validation answer: the enforced version and the typed violations, never a field value.</summary>
public sealed record SchemaValidationDto(ulong? EnforcedVersion, bool Valid, ViolationDto[] Violations);

/// <summary>One typed policy violation of one document field.</summary>
/// <param name="Field">The violated field name, or empty when the document itself is the violation.</param>
/// <param name="Kind">The snake_case violation kind.</param>
public sealed record ViolationDto(string Field, string Kind);
