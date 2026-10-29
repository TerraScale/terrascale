using System.Net.Http.Headers;
using System.Text;

namespace TerraScale.DotNet;

internal static class TerraScaleAuthCredentials
{
    internal static AuthenticationHeaderValue Basic(string clientId, string clientSecret)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(clientId);
        ArgumentException.ThrowIfNullOrWhiteSpace(clientSecret);
        var encoded = Uri.EscapeDataString(clientId) + ":" + Uri.EscapeDataString(clientSecret);
        if (encoded.Length > 16 * 1024) throw new ArgumentException("OAuth client credentials exceed the byte limit.");
        return new AuthenticationHeaderValue("Basic", Convert.ToBase64String(Encoding.ASCII.GetBytes(encoded)));
    }

    internal static AuthenticationHeaderValue Bearer(string accessToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(accessToken);
        if (accessToken.Length > 8 * 1024 || accessToken.Any(character => char.IsControl(character) || char.IsWhiteSpace(character)))
        {
            throw new ArgumentException("A sign-in token must be at most 8192 characters without whitespace or control characters.", nameof(accessToken));
        }
        return new AuthenticationHeaderValue("Bearer", accessToken);
    }
}
