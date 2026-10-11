using System.Text.Json;
using System.Text.Json.Serialization;
using TerraScale.Endpoints.Example.Endpoints;

namespace TerraScale.Endpoints.Example;

/// <summary>Source-generated JSON contracts for every request and response type (required for native AOT).</summary>
[JsonSourceGenerationOptions(JsonSerializerDefaults.Web)]
[JsonSerializable(typeof(Widget))]
[JsonSerializable(typeof(CreateWidgetRequest))]
[JsonSerializable(typeof(GetWidgetRequest))]
[JsonSerializable(typeof(EchoWidgetRequest))]
[JsonSerializable(typeof(EchoWidgetResponse))]
[JsonSerializable(typeof(HealthResponse))]
internal sealed partial class ExampleJsonContext : JsonSerializerContext;
