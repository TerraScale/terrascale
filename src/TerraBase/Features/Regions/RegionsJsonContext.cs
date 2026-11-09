using System.Text.Json;
using System.Text.Json.Serialization;
using TerraBase.Hosting;

namespace TerraBase.Features.Regions;

[JsonSourceGenerationOptions(JsonSerializerDefaults.Web, MaxDepth = RequestJson.StoredMaxDepth + 8)]
[JsonSerializable(typeof(RegionsBody))]
internal sealed partial class RegionsJsonContext : JsonSerializerContext;
