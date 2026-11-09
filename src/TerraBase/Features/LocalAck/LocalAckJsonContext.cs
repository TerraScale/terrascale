using System.Text.Json;
using System.Text.Json.Serialization;
using TerraBase.Hosting;

namespace TerraBase.Features.LocalAck;

[JsonSourceGenerationOptions(JsonSerializerDefaults.Web, MaxDepth = RequestJson.StoredMaxDepth + 8)]
[JsonSerializable(typeof(HeldTransaction))]
[JsonSerializable(typeof(HoldRelease))]
internal sealed partial class LocalAckJsonContext : JsonSerializerContext;
