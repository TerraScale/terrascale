using System.Text.Json;
using System.Text.Json.Serialization;
using TerraBase.Hosting;

namespace TerraBase.Features.Rows;

[JsonSourceGenerationOptions(JsonSerializerDefaults.Web, MaxDepth = RequestJson.StoredMaxDepth + 8)]
[JsonSerializable(typeof(RowBody))]
[JsonSerializable(typeof(RowsBatchBody))]
[JsonSerializable(typeof(RowAnswer))]
[JsonSerializable(typeof(RowsBatchAnswer))]
[JsonSerializable(typeof(RowsErrorBody))]
internal sealed partial class RowsJsonContext : JsonSerializerContext;
