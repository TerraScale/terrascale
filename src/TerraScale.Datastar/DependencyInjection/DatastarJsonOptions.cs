using System.Text.Json;

namespace TerraScale.Datastar.DependencyInjection;

public class DatastarJsonOptions
{
    public JsonSerializerOptions SignalsJsonSerializerOptions { get; set; } = new(JsonSerializerDefaults.Web);
}
