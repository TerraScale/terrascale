using System.Net;
using System.Net.Sockets;
using System.Text;
using TerraScale.Auth.Features.Oidc;

namespace TerraScale.Auth.Tests;

internal static class RawHttp
{
    internal static async Task<(HttpStatusCode Status, TimeSpan? RetryAfter)> DuplicateForwardedAsync(
        Uri baseAddress, string path, string first, string last, int attempt)
    {
        // HttpClient combines repeated request headers; use a real HTTP/1.1 socket to preserve two wire lines.
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        using var client = new TcpClient();
        await client.ConnectAsync(baseAddress.Host, baseAddress.Port, deadline.Token);
        var body = $"grant_type=refresh_token&refresh_token=invalid&email=unknown-{attempt}%40example.test&code=000000";
        var authorization = Convert.ToBase64String(Encoding.UTF8.GetBytes(OidcIssuer.DashboardClientId + ":wrong-secret"));
        var request = $"POST {path} HTTP/1.1\r\nHost: spoofed.example.invalid\r\nConnection: close\r\n"
            + $"Origin: {OidcHarness.PublicOrigin}\r\nAuthorization: Basic {authorization}\r\n"
            + $"X-Forwarded-For: {first}\r\nX-Forwarded-For: {last}\r\n"
            + $"Content-Type: application/x-www-form-urlencoded\r\nContent-Length: {Encoding.ASCII.GetByteCount(body)}\r\n\r\n{body}";
        await client.GetStream().WriteAsync(Encoding.ASCII.GetBytes(request), deadline.Token);
        using var reader = new StreamReader(client.GetStream(), Encoding.ASCII);
        var statusLine = await reader.ReadLineAsync(deadline.Token) ?? throw new InvalidOperationException("No HTTP response.");
        var status = (HttpStatusCode)int.Parse(statusLine.Split(' ')[1], System.Globalization.CultureInfo.InvariantCulture);
        TimeSpan? retryAfter = null;
        while (await reader.ReadLineAsync(deadline.Token) is { Length: > 0 } header)
        {
            if (header.StartsWith("Retry-After:", StringComparison.OrdinalIgnoreCase))
            {
                retryAfter = TimeSpan.FromSeconds(int.Parse(header["Retry-After:".Length..].Trim(), System.Globalization.CultureInfo.InvariantCulture));
            }
        }

        return (status, retryAfter);
    }
}
