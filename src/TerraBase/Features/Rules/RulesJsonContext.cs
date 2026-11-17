using System.Text.Json;
using System.Text.Json.Serialization;
using TerraBase.Hosting;

namespace TerraBase.Features.Rules;

[JsonSourceGenerationOptions(JsonSerializerDefaults.Web, MaxDepth = RequestJson.StoredMaxDepth + 8)]
[JsonSerializable(typeof(RulesDocument))]
[JsonSerializable(typeof(RulesBody))]
[JsonSerializable(typeof(RulesVersionsBody))]
[JsonSerializable(typeof(RulesErrorBody))]
internal sealed partial class RulesJsonContext : JsonSerializerContext;
