using System.Text.Json.Serialization;

namespace TerraScale.Authentication.Contracts;

public sealed record SigningKey(
    [property: JsonPropertyName("instanceId"), JsonRequired] string InstanceId,
    [property: JsonPropertyName("masterKeyId"), JsonRequired] string MasterKeyId,
    [property: JsonPropertyName("id"), JsonRequired] string Id,
    [property: JsonPropertyName("algorithm"), JsonRequired] string Algorithm,
    [property: JsonPropertyName("secretBox"), JsonRequired] string SecretBox,
    [property: JsonPropertyName("createdAt"), JsonRequired] long CreatedAt,
    [property: JsonPropertyName("activatesAt"), JsonRequired] long ActivatesAt,
    [property: JsonPropertyName("x"), JsonRequired] string X,
    [property: JsonPropertyName("y"), JsonRequired] string Y,
    [property: JsonPropertyName("publicMetadataMac"), JsonRequired] string PublicMetadataMac)
{
    [JsonIgnore]
    public string? Version { get; init; }
}

public sealed record AttemptBucket(
    [property: JsonPropertyName("window"), JsonRequired] long Window,
    [property: JsonPropertyName("used"), JsonRequired] int Used);
