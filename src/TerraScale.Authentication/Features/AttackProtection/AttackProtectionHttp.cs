using System.Diagnostics.CodeAnalysis;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace TerraScale.Authentication.Features.AttackProtection;

public sealed record TurnstileKeys(string? SiteKey, string? Secret)
{
    public bool Configured => !string.IsNullOrWhiteSpace(this.SiteKey) && !string.IsNullOrWhiteSpace(this.Secret);

    public override string ToString() => nameof(TurnstileKeys);
}

public sealed partial class AttackProtectionHttp(IHttpClientFactory clients, ILogger<AttackProtectionHttp> logger)
{
    public const string ClientName = "attack-protection";

    [SuppressMessage("Security", "CA5350", Justification = "HIBP requires SHA-1 for k-anonymous password lookup; it never protects stored credentials.")]
    public async Task<bool> BreachedAsync(string password, CancellationToken cancellationToken)
    {
        var digest = Convert.ToHexString(SHA1.HashData(Encoding.UTF8.GetBytes(password)));
        using var request = new HttpRequestMessage(HttpMethod.Get, "https://api.pwnedpasswords.com/range/" + digest[..5]);
        request.Headers.Add("Add-Padding", "true");
        try
        {
            using var client = clients.CreateClient(ClientName);
            using var response = await client.SendAsync(request, cancellationToken);
            response.EnsureSuccessStatusCode();
            using var reader = new StringReader(await response.Content.ReadAsStringAsync(cancellationToken));
            while (await reader.ReadLineAsync(cancellationToken) is { } line)
            {
                if (line.IndexOf(':') is 35 && line.AsSpan(0, 35).Equals(digest.AsSpan(5), StringComparison.OrdinalIgnoreCase)
                    && long.TryParse(line.AsSpan(36), out var count) && count > 0)
                {
                    return true;
                }
            }

            return false;
        }
        catch (HttpRequestException)
        {
            BreachUnavailable(logger);
            return false;
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            BreachUnavailable(logger);
            return false;
        }
    }

    public async Task<bool> ChallengeAsync(TurnstileKeys keys, string? token, string? ip, string hostname, string action, string project, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(keys);
        if (!keys.Configured || token is not { Length: > 0 and <= 2048 } || string.IsNullOrWhiteSpace(ip))
        {
            return false;
        }

        using var body = new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["secret"] = keys.Secret!,
            ["response"] = token,
            ["remoteip"] = ip,
        });
        try
        {
            using var client = clients.CreateClient(ClientName);
            using var response = await client.PostAsync("https://challenges.cloudflare.com/turnstile/v0/siteverify", body, cancellationToken);
            var result = response.IsSuccessStatusCode ? await JsonSerializer.DeserializeAsync(
                await response.Content.ReadAsStreamAsync(cancellationToken), AttackProtectionJson.Default.TurnstileResponse, cancellationToken) : null;
            return result is { Success: true } && string.Equals(result.Hostname, hostname, StringComparison.OrdinalIgnoreCase)
                && result.Action == action && result.Cdata == project;
        }
        catch (HttpRequestException)
        {
            return false;
        }
        catch (JsonException)
        {
            return false;
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return false;
        }
    }

    [LoggerMessage(Level = LogLevel.Warning, Message = "Breached-password service unavailable; the local common-password policy remains enforced.")]
    private static partial void BreachUnavailable(ILogger logger);
}
