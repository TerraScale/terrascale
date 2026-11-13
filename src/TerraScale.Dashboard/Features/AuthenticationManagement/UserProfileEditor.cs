using System.Text;
using System.Text.Json;

namespace TerraScale.Dashboard.Features.AuthenticationManagement;

public static partial class AuthenticationConsole
{
    private static JsonElement ProfileMetadata(string? text)
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
            throw new AccountAccessException("Metadata must be a JSON object of at most 8192 UTF-8 bytes.", 422);
        }

        throw new AccountAccessException("Metadata must be a JSON object of at most 8192 UTF-8 bytes.", 422);
    }
}
