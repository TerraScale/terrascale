using System.Text.Json;
using System.Text.Json.Serialization;
using TerraBase.Hosting;

namespace TerraBase.Features.Clock;

[JsonSourceGenerationOptions(JsonSerializerDefaults.Web, MaxDepth = RequestJson.StoredMaxDepth + 8)]
[JsonSerializable(typeof(HlcState))]
[JsonSerializable(typeof(ClockReport))]
internal sealed partial class ClockJsonContext : JsonSerializerContext;
