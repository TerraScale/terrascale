using System.Net;
using System.Text.Json;
using TerraScale.Dashboard.Platform.Persistence;

namespace TerraScale.Dashboard.Features.Auth
{
    // Operator commands sign in with ts-auth's device authorization grant (RFC 8628)
    // as its public CLI client. The user approves on ts-auth's hosted verification page;
    // the grant lives only in the command's memory and is revoked when it finishes.
    public sealed partial class TsAuthGateway
    {
        private const string CliClient = "00000000000000000000000007"; // ts-auth's built-in ts-cli client
        private const string DeviceGrantType = "urn:ietf:params:oauth:grant-type:device_code";

        public async Task<SessionRecord> DeviceSignInAsync(TextWriter output, CancellationToken cancellationToken)
        {
            using var issued = await CliPostAsync("device", new() { ["client_id"] = CliClient, ["scope"] = "openid email offline_access" }, cancellationToken);
            if (issued.Status != HttpStatusCode.OK)
            {
                throw new HttpRequestException("ts-auth refused the device authorization request.");
            }

            var row = issued.Body.RootElement;
            var device = row.GetProperty("device_code").GetString() ?? "";
            var code = row.GetProperty("user_code").GetString() ?? "";
            var verification = row.GetProperty("verification_uri").GetString() ?? "";
            var complete = row.TryGetProperty("verification_uri_complete", out var completeValue) ? completeValue.GetString() ?? "" : verification;
            var expires = row.GetProperty("expires_in").GetInt32();
            var interval = row.TryGetProperty("interval", out var intervalValue) ? intervalValue.GetInt32() : 5;
            if (device.Length is 0 or > 512 || code.Length is 0 or > 32 || code.Any(char.IsControl) || expires is <= 0 or > 1800 || interval is < 1 or > 600 ||
                !OnIssuer(verification) || !OnIssuer(complete))
            {
                throw new JsonException("ts-auth device authorization response is invalid.");
            }

            await output.WriteLineAsync("Approve CLI access: " + complete);
            await output.WriteLineAsync("User code: " + code);
            await output.FlushAsync(cancellationToken);
            using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            deadline.CancelAfter(TimeSpan.FromSeconds(expires));
            try
            {
                while (true)
                {
                    await Task.Delay(TimeSpan.FromSeconds(interval), deadline.Token);
                    using var answer = await CliPostAsync("token", new() { ["grant_type"] = DeviceGrantType, ["device_code"] = device, ["client_id"] = CliClient }, deadline.Token);
                    if (answer.Status == HttpStatusCode.OK)
                    {
                        return await CliSessionAsync(answer.Body.RootElement, null, deadline.Token);
                    }

                    if (answer.Status != HttpStatusCode.BadRequest)
                    {
                        throw new HttpRequestException("ts-auth device token polling failed.");
                    }

                    switch (answer.Body.RootElement.GetProperty("error").GetString())
                    {
                        case "authorization_pending": break;
                        case "slow_down": interval = checked(interval + 5); break;
                        case "access_denied": throw new DeviceSignInException("CLI authorization was denied.");
                        case "expired_token": throw new DeviceSignInException("CLI authorization expired. Start the command again.");
                        default: throw new HttpRequestException("ts-auth rejected the device token request.");
                    }
                }
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested && deadline.IsCancellationRequested)
            { throw new DeviceSignInException("CLI authorization expired. Start the command again."); }
        }

        // Rotates the CLI grant so platform authority is re-read from a freshly minted access token.
        public async Task<SessionRecord> DeviceRefreshAsync(SessionRecord session, CancellationToken cancellationToken)
        {
            if (string.IsNullOrWhiteSpace(session.RefreshToken))
            {
                throw new SignInRejectedException();
            }

            using var answer = await CliPostAsync("token", new() { ["grant_type"] = "refresh_token", ["refresh_token"] = session.RefreshToken, ["client_id"] = CliClient }, cancellationToken);
            if (answer.Status != HttpStatusCode.OK)
            {
                throw new SignInRejectedException();
            }

            return await CliSessionAsync(answer.Body.RootElement, session, cancellationToken);
        }

        public async Task DeviceRevokeAsync(SessionRecord session, CancellationToken cancellationToken)
        {
            using var client = http.CreateClient("platform");
            using var response = await client.PostAsync(new Uri(AuthOrigin, $"/{Tenant}/oauth/revoke"), new FormUrlEncodedContent(new Dictionary<string, string>
            { ["token"] = session.RefreshToken ?? session.AccessToken, ["client_id"] = CliClient }), cancellationToken);
            response.EnsureSuccessStatusCode();
        }

        private async Task<SessionRecord> CliSessionAsync(JsonElement tokens, SessionRecord? previous, CancellationToken cancellationToken)
        {
            var access = tokens.GetProperty("access_token").GetString() ?? "";
            var expiresIn = tokens.GetProperty("expires_in").GetInt32();
            if (access.Length is 0 or > 16384 || expiresIn is <= 0 or > 86400 ||
                !string.Equals(tokens.GetProperty("token_type").GetString(), "Bearer", StringComparison.OrdinalIgnoreCase))
            {
                throw new SignInRejectedException();
            }

            var refresh = tokens.TryGetProperty("refresh_token", out var refreshValue) ? refreshValue.GetString() : null;
            // The same userinfo check every browser session passes binds the token to one verified identity.
            var user = await UserInfoAsync(Client(), access, cancellationToken);
            var email = user.Email?.Trim().ToLowerInvariant() ?? "";
            if (string.IsNullOrWhiteSpace(user.Subject) || user.EmailVerified != true || email.Length is 0 or > 254 ||
                previous is not null && (previous.UserId != user.Subject || previous.Email != email))
            {
                throw new SignInRejectedException();
            }

            return new(user.Subject, email, email, access, string.IsNullOrWhiteSpace(refresh) ? previous?.RefreshToken : refresh, time.GetUtcNow().AddSeconds(expiresIn));
        }

        private async Task<CliAnswer> CliPostAsync(string endpoint, Dictionary<string, string> form, CancellationToken cancellationToken)
        {
            using var client = http.CreateClient("platform");
            using var response = await client.PostAsync(new Uri(AuthOrigin, $"/{Tenant}/oauth/{endpoint}"), new FormUrlEncodedContent(form), cancellationToken);
            return new(response.StatusCode, await PlatformJson.ReadAsync(response.Content, cancellationToken));
        }

        private static bool OnIssuer(string value)
        {
            return Uri.TryCreate(value, UriKind.Absolute, out var uri) && uri.Scheme == "https" &&
            uri.IsDefaultPort && uri.Host == AuthOrigin.Host && uri.UserInfo.Length == 0 && uri.Fragment.Length == 0;
        }

        private sealed record CliAnswer(HttpStatusCode Status, JsonDocument Body) : IDisposable
        {
            public void Dispose()
            {
                Body.Dispose();
            }
        }
    }

    public sealed class DeviceSignInException(string message) : Exception(message);
}
