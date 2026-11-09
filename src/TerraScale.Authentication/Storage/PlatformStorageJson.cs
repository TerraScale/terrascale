using System.Text.Json.Serialization;
using TerraScale.Authentication.Contracts;

namespace TerraScale.Authentication.Storage;

[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase)]
[JsonSerializable(typeof(OwnershipTransferRecord))]
public sealed partial class PlatformStorageJson : JsonSerializerContext;
