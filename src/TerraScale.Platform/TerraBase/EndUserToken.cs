using System.Buffers;
using System.Buffers.Text;
using System.Collections.Frozen;
using System.Diagnostics;
using System.Security.Cryptography;

namespace TerraScale.Platform.TerraBase;

public sealed record EndUserToken(string Digest, bool Guest)
{
    private static readonly FrozenSet<string> RulesInputs = FrozenSet.Create(
        StringComparer.Ordinal, "iss", "sub", "aud", "token_use", "database", "org_id", "org_role", "is_anonymous", "claims");

    public static EndUserToken? Read(CallerCredential credential)
    {
        ArgumentNullException.ThrowIfNull(credential);
        if (!credential.IsJwt || credential.BearerToken is not { } token || token.Split('.') is not [_, var encoded, _])
        {
            return null;
        }

        var bytes = new byte[Base64Url.GetMaxDecodedLength(encoded.Length)];
        if (Base64Url.DecodeFromChars(encoded, bytes, out _, out var written) != OperationStatus.Done || Parse(bytes.AsMemory(0, written)) is not { } payload)
        {
            return null;
        }

        using (payload)
        {
            var root = payload.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
            {
                return null;
            }

            var buffer = new ArrayBufferWriter<byte>();
            using (var writer = new Utf8JsonWriter(buffer))
            {
                writer.WriteStartObject();
                foreach (var property in root.EnumerateObject().Where(static property => RulesInputs.Contains(property.Name)).OrderBy(static property => property.Name, StringComparer.Ordinal))
                {
                    writer.WritePropertyName(property.Name);
                    WriteCanonical(writer, property.Value);
                }

                writer.WriteEndObject();
            }

            var guest = root.TryGetProperty("is_anonymous", out var anonymous) && anonymous.ValueKind == JsonValueKind.True;
            return new EndUserToken(Convert.ToHexStringLower(SHA256.HashData(buffer.WrittenSpan)), guest);
        }
    }

    private static void WriteCanonical(Utf8JsonWriter writer, JsonElement element)
    {
        switch (element.ValueKind)
        {
            case JsonValueKind.Object:
                writer.WriteStartObject();
                foreach (var property in element.EnumerateObject().OrderBy(static property => property.Name, StringComparer.Ordinal))
                {
                    writer.WritePropertyName(property.Name);
                    WriteCanonical(writer, property.Value);
                }

                writer.WriteEndObject();
                return;
            case JsonValueKind.Array:
                writer.WriteStartArray();
                foreach (var item in element.EnumerateArray())
                {
                    WriteCanonical(writer, item);
                }

                writer.WriteEndArray();
                return;
            case JsonValueKind.String or JsonValueKind.Number or JsonValueKind.True or JsonValueKind.False or JsonValueKind.Null:
                element.WriteTo(writer);
                return;
            default:
                throw new UnreachableException();
        }
    }

    private static JsonDocument? Parse(ReadOnlyMemory<byte> json)
    {
        try
        {
            return JsonDocument.Parse(json);
        }
        catch (JsonException)
        {
            return null;
        }
    }
}
