using System.Text.Json;
using System.Text.Json.Serialization;
using TerraScale.Endpoints.Example.Endpoints;

namespace TerraScale.Endpoints.Example;

[JsonSourceGenerationOptions(JsonSerializerDefaults.Web)]
[JsonSerializable(typeof(Widget))]
[JsonSerializable(typeof(CreateWidgetRequest))]
[JsonSerializable(typeof(GetWidgetRequest))]
[JsonSerializable(typeof(EchoWidgetRequest))]
[JsonSerializable(typeof(EchoWidgetResponse))]
[JsonSerializable(typeof(HealthResponse))]
internal sealed partial class ExampleJsonContext : JsonSerializerContext;
