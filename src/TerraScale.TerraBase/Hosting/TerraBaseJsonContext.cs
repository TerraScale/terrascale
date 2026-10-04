using System.Text.Json.Serialization;
using TerraScale.TerraBase.Features.Health;

namespace TerraScale.TerraBase.Hosting;

/// <summary>Source-generated JSON metadata for every request and response type (native AOT).</summary>
[JsonSerializable(typeof(StatusBody))]
internal sealed partial class TerraBaseJsonContext : JsonSerializerContext;
