using System.Text.Json;
using System.Text.Json.Serialization;
using TerraBase.Hosting;

namespace TerraBase.Features.HomeApply;

[JsonSourceGenerationOptions(JsonSerializerDefaults.Web, MaxDepth = RequestJson.StoredMaxDepth + 8)]
[JsonSerializable(typeof(RegionTransaction))]
[JsonSerializable(typeof(HomeApplyResult))]
internal sealed partial class HomeApplyJsonContext : JsonSerializerContext;
