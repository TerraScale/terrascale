// <copyright file="ErrorDtos.cs" company="TerraScale">
// TerraBase (SE-08). See LICENSE.md.
// </copyright>

namespace TerraBase.Api;

using System.Text.Json.Serialization;

/// <summary>The typed error answer.</summary>
public sealed record ErrorDto(string Code, string Detail)
{
    /// <summary>
    /// Set on a checkpoint refusal: the consumer resyncs by pulling a
    /// snapshot. Serialized only when demanded — a plain typed refusal
    /// omits the member instead of stating a null marker, so null-unsafe
    /// refusal parsers never see one (the #93 cross-repo defect).
    /// </summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public bool? ResyncRequired { get; init; }
}
