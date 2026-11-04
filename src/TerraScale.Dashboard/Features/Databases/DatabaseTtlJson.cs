using System.Text.Json;
using System.Text.Json.Serialization;

namespace TerraScale.Dashboard.Features.Databases
{
    [JsonSourceGenerationOptions(JsonSerializerDefaults.Web)]
    [JsonSerializable(typeof(DatabaseRecord))]
    internal sealed partial class DatabaseTtlJson : JsonSerializerContext;
}
