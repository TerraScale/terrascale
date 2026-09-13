// <copyright file="ValueDtos.cs" company="TerraScale">
// TerraBase (SE-08). See LICENSE.md.
// </copyright>

namespace TerraBase.Api;

using System.Text.Json;
using TerraBase.Protocol;

/// <summary>The JSON surface of the typed API: DTOs over the TBEP bodies, <see cref="System.Text.Json"/> only.</summary>
public static class Dto
{
    /// <summary>Converts PascalCase variant names to their snake_case wire names.</summary>
    /// <param name="value">The variant name.</param>
    /// <returns>The snake_case name.</returns>
    public static string ToSnakeCase(this string value) =>
        string.Concat(value.Select((c, i) => i > 0 && char.IsUpper(c) ? "_" + char.ToLowerInvariant(c) : char.ToLowerInvariant(c).ToString()));
}

/// <summary>The recursive document-body value on the JSON surface: exactly one discriminator set, mirroring the wire value.</summary>
public sealed record JsonValue
{
    /// <summary>The null discriminator.</summary>
    public bool? Null { get; init; }

    /// <summary>The boolean discriminator.</summary>
    public bool? Boolean { get; init; }

    /// <summary>The integer discriminator.</summary>
    [System.Diagnostics.CodeAnalysis.SuppressMessage("Naming", "CA1720", Justification = "Integer is the documented JSON discriminator name.")]
    public long? Integer { get; init; }

    /// <summary>The number discriminator.</summary>
    public double? Number { get; init; }

    /// <summary>The string discriminator.</summary>
    [System.Diagnostics.CodeAnalysis.SuppressMessage("Naming", "CA1720", Justification = "String is the documented JSON discriminator name.")]
    public string? String { get; init; }

    /// <summary>The array discriminator.</summary>
    public IReadOnlyList<JsonValue>? Array { get; init; }

    /// <summary>The object discriminator.</summary>
    [System.Diagnostics.CodeAnalysis.SuppressMessage("Naming", "CA1720", Justification = "Object is the documented JSON discriminator name.")]
    public IReadOnlyDictionary<string, JsonValue>? Object { get; init; }

    /// <summary>Converts to the protocol's value.</summary>
    /// <returns>The <see cref="TbepValue"/>.</returns>
    public TbepValue ToValue()
    {
        var discriminatorCount =
            (this.Null is not null ? 1 : 0)
            + (this.Boolean is not null ? 1 : 0)
            + (this.Integer is not null ? 1 : 0)
            + (this.Number is not null ? 1 : 0)
            + (this.String is not null ? 1 : 0)
            + (this.Array is not null ? 1 : 0)
            + (this.Object is not null ? 1 : 0);

        if (discriminatorCount != 1 || this.Null is false)
        {
            throw new JsonException("a value carries exactly one discriminator");
        }

        if (this.Null is true)
        {
            return TbepValue.Null;
        }

        if (this.Boolean is { } boolean)
        {
            return TbepValue.Of(boolean);
        }

        if (this.Integer is { } integer)
        {
            return TbepValue.Of(integer);
        }

        if (this.Number is { } number)
        {
            return TbepValue.Of(number);
        }

        if (this.String is { } text)
        {
            return TbepValue.Of(text);
        }

        if (this.Array is { } items)
        {
            if (items.Any(static item => item is null))
            {
                throw new JsonException("an array cannot contain a null value discriminator");
            }

            return TbepValue.Of(items.Select(static item => item.ToValue()).ToArray());
        }

        if (this.Object is { } fields)
        {
            if (fields.Any(static field => field.Key is null || field.Value is null))
            {
                throw new JsonException("an object cannot contain a null field name or value");
            }

            return TbepValue.Of(
                fields.Select(static field => new ObjectField(field.Key, field.Value.ToValue())).ToArray());
        }

        throw new JsonException("a value carries exactly one discriminator");
    }

    /// <summary>Builds from the protocol's value.</summary>
    /// <param name="value">The <see cref="TbepValue"/>.</param>
    /// <returns>The JSON value.</returns>
    public static JsonValue Of(TbepValue value)
    {
        ArgumentNullException.ThrowIfNull(value);
        return value.Tag switch
        {
            0 => new JsonValue { Null = true },
            1 => new JsonValue { Boolean = (bool)value.Payload! },
            2 => new JsonValue { Integer = (long)value.Payload! },
            3 => new JsonValue { Number = (double)value.Payload! },
            4 => new JsonValue { String = (string)value.Payload! },
            5 => new JsonValue { Array = ((TbepValue[])value.Payload!).Select(Of).ToArray() },
            6 => new JsonValue { Object = ((ObjectField[])value.Payload!).ToDictionary(field => field.Name, field => Of(field.Value)) },
            _ => throw new JsonException($"unknown value tag {value.Tag}"),
        };
    }
}
