using System.Text.Json;
using System.Text.Json.Serialization;
using TerraBase.Hosting;

namespace TerraBase.Features.Functions;

[JsonSourceGenerationOptions(JsonSerializerDefaults.Web, MaxDepth = RequestJson.StoredMaxDepth + 8)]
[JsonSerializable(typeof(DeployFunctionRequest))]
[JsonSerializable(typeof(FunctionBody))]
[JsonSerializable(typeof(FunctionBody[]))]
[JsonSerializable(typeof(FunctionLogBody))]
[JsonSerializable(typeof(FunctionLogBody[]))]
[JsonSerializable(typeof(FunctionRecord))]
[JsonSerializable(typeof(FunctionSchedule))]
internal sealed partial class FunctionsJsonContext : JsonSerializerContext;
