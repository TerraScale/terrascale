using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.Json.Serialization.Metadata;
using Microsoft.EntityFrameworkCore.ChangeTracking;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Microsoft.EntityFrameworkCore.Storage.ValueConversion;
using TerraScale.Authentication.Contracts;

namespace TerraScale.Authentication.Storage;

public static class JsonColumns
{
    extension<T>(PropertyBuilder<T> property)
    {
        public PropertyBuilder<T> HasJson(JsonTypeInfo<T> type)
        {
            ArgumentNullException.ThrowIfNull(type);
            property.HasConversion(
                new ValueConverter<T, string>(value => JsonSerializer.Serialize<T>(value, type), stored => JsonSerializer.Deserialize<T>(stored, type)!),
                new ValueComparer<T>(
                    (left, right) => JsonSerializer.Serialize<T>(left!, type) == JsonSerializer.Serialize<T>(right!, type),
                    value => JsonSerializer.Serialize<T>(value, type).GetHashCode(StringComparison.Ordinal),
                    value => JsonSerializer.Deserialize<T>(JsonSerializer.Serialize<T>(value, type), type)!));
            return property;
        }
    }
}

[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase)]
[JsonSerializable(typeof(IReadOnlyList<CredentialBinding>))]
[JsonSerializable(typeof(IReadOnlyList<string>))]
[JsonSerializable(typeof(IReadOnlyDictionary<string, JsonElement>))]
[JsonSerializable(typeof(IReadOnlyDictionary<string, string>))]
public sealed partial class StorageJson : JsonSerializerContext;
