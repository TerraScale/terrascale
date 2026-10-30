using System.Text.Json;
using System.Text.Json.Serialization;

namespace TerraScale.TerraBase.Contracts.Webhooks
{
    /// <summary>The REST JSON shape of the Webhooks API.</summary>
    [JsonSourceGenerationOptions(JsonSerializerDefaults.Web, DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull, MaxDepth = 256)]
    [JsonSerializable(typeof(TerraBaseCreateChangeWebhookRequest))]
    [JsonSerializable(typeof(TerraBaseCreatedChangeWebhook))]
    [JsonSerializable(typeof(IReadOnlyList<TerraBaseChangeWebhook>))]
    [JsonSerializable(typeof(IReadOnlyList<TerraBaseChangeWebhookDelivery>))]
    public sealed partial class WebhooksJson : JsonSerializerContext;
}
