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
        if (Base64Url.DecodeFromChars(encoded, bytes, out _, out var written) != OperationStatus.Done
            || TerraBaseAnswers.Parse(bytes.AsSpan(0, written), PlatformJsonContext.Default.DictionaryStringJsonElement) is not { } payload)
        {
            return null;
        }

        if (Canonical(payload) is not { } canonical)
        {
            return null;
        }

        var guest = payload.TryGetValue("is_anonymous", out var anonymous) && anonymous.ValueKind == JsonValueKind.True;
        return new EndUserToken(Convert.ToHexStringLower(SHA256.HashData(canonical)), guest);
    }

    private static byte[]? Canonical(Dictionary<string, JsonElement> payload)
    {
        var buffer = new ArrayBufferWriter<byte>();
        try
        {
            using var writer = new Utf8JsonWriter(buffer);
            writer.WriteStartObject();
            foreach (var (name, value) in payload.Where(static member => RulesInputs.Contains(member.Key)).OrderBy(static member => member.Key, StringComparer.Ordinal))
            {
                writer.WritePropertyName(name);
                WriteCanonical(writer, value);
            }

            writer.WriteEndObject();
            writer.Flush();
        }
        catch (Exception error) when (error is InvalidOperationException or ArgumentException or JsonException)
        {
            return null;
        }

        return buffer.WrittenSpan.ToArray();
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
}
