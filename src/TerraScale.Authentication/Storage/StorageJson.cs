using System.Text.Json;
using System.Text.Json.Serialization;
using TerraScale.Authentication.Contracts;

namespace TerraScale.Authentication.Storage;

[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase)]
[JsonSerializable(typeof(IReadOnlyList<CredentialBinding>))]
[JsonSerializable(typeof(IReadOnlyList<string>))]
[JsonSerializable(typeof(IReadOnlyDictionary<string, JsonElement>))]
[JsonSerializable(typeof(IReadOnlyDictionary<string, string>))]
[JsonSerializable(typeof(JsonElement))]
public sealed partial class StorageJson : JsonSerializerContext;
