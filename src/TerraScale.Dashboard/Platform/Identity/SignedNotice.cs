using System.Security.Cryptography;
using System.Text;

namespace TerraScale.Dashboard.Platform.Identity;

public static class SignedNotice
{
    public static string Sign(string csrfToken, string purpose, string text)
    {
        return Convert.ToHexStringLower(HMACSHA256.HashData(Encoding.UTF8.GetBytes(csrfToken), Encoding.UTF8.GetBytes(purpose + "\n" + text)));
    }

    public static bool Verify(HttpContext context, string purpose, string text, string signature)
    {
        return signature is { Length: 64 } && context.Request.Cookies[Csrf.Cookie] is { Length: 64 } nonce && nonce.All(char.IsAsciiHexDigitLower) && CryptographicOperations.FixedTimeEquals(
            Encoding.ASCII.GetBytes(Sign(Csrf.Token(context), purpose, text)), Encoding.ASCII.GetBytes(signature));
    }
}
