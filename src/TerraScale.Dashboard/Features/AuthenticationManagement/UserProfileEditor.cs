using System.Text;
using System.Text.Json;
using TerraScale.Dashboard.Errors;

namespace TerraScale.Dashboard.Features.AuthenticationManagement;

public static partial class AuthenticationConsole
{
    private static JsonElement Metadata(string? text)
    {
        return ProfileMetadata(text) switch
        {
            JsonElement value => value,
            Validation error => throw new AccountAccessException(error.Message, 422),
        };
    }

    private static Valid<JsonElement> ProfileMetadata(string? text)
    {
        try
        {
            if (text is not null && Encoding.UTF8.GetByteCount(text) <= 8192 && JsonElement.Parse(text) is { ValueKind: JsonValueKind.Object } value)
            {
                return value;
            }
        }
        catch (JsonException)
        {
            return new Validation("Metadata must be a JSON object of at most 8192 UTF-8 bytes.");
        }

        return new Validation("Metadata must be a JSON object of at most 8192 UTF-8 bytes.");
    }
}
