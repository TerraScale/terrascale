using System.Text.Json;
using System.Text.Json.Serialization;
using TerraBase.Hosting;

namespace TerraBase.Features.Shapes;

[JsonSourceGenerationOptions(JsonSerializerDefaults.Web, MaxDepth = RequestJson.StoredMaxDepth + 8)]
[JsonSerializable(typeof(ShapeGrantRequest))]
[JsonSerializable(typeof(ShapeGrantBody))]
[JsonSerializable(typeof(ShapeGrantsBody))]
internal sealed partial class ShapesJsonContext : JsonSerializerContext;
