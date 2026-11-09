using System.Text.Json;
using System.Text.Json.Serialization;
using TerraBase.Hosting;

namespace TerraBase.Features.Sql;

[JsonSourceGenerationOptions(JsonSerializerDefaults.Web, MaxDepth = RequestJson.StoredMaxDepth + 8)]
[JsonSerializable(typeof(SqlExecuteRequest))]
[JsonSerializable(typeof(SqlExecuteBody))]
[JsonSerializable(typeof(SqlErrorBody))]
[JsonSerializable(typeof(SqlSocketRequest))]
[JsonSerializable(typeof(SqlSocketResult))]
[JsonSerializable(typeof(SqlSocketError))]
internal sealed partial class SqlJsonContext : JsonSerializerContext;
