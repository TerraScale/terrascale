using System.Text.Json;
using System.Text.Json.Serialization;
using TerraScale.DotNet.Generated;

namespace TerraScale.DotNet;

[JsonSourceGenerationOptions(JsonSerializerDefaults.Web, NumberHandling = JsonNumberHandling.AllowReadingFromString)]
[JsonSerializable(typeof(OperationPage))]
[JsonSerializable(typeof(OperationSummary))]
internal partial class TerraScaleOperationDiscoveryJsonContext : JsonSerializerContext;
