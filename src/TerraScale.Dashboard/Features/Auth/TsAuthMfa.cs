using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using TerraScale.Dashboard.Common.UI;

namespace TerraScale.Dashboard.Features.Auth
{
    // ts-auth enrols, shows recovery codes and disables two-factor on its hosted security page.
    public sealed record AccountMfaStatus(bool Enabled, int RecoveryCodesRemaining, string ManageUrl);

    // The caller's own two-factor status and an organization's two-factor policy, both owned by ts-auth.
    public interface ITsAuthMfa
    {
        // Null when ts-auth does not report a status; unknown never means disabled.
        Task<AccountMfaStatus?> GetAccountMfaAsync(SessionRecord session, CancellationToken cancellationToken);
        // Null when ts-auth does not report the organization's policy.
        Task<bool?> GetRequireMfaAsync(SessionRecord session, string organizationId, CancellationToken cancellationToken);
        // Owner only; ts-auth refuses with mfa_not_enrolled while the caller has no two-factor.
        Task SetRequireMfaAsync(SessionRecord session, string organizationId, bool requireMfa, CancellationToken cancellationToken);
    }

    // The selected organization requires two-factor and ts-auth refused this caller, who has none.
    public sealed class OrganizationMfaRequiredException(string organizationName, IReadOnlyList<(string Id, string Name)> others)
        : Exception(organizationName + " requires two-factor authentication.")
    {
        public string OrganizationName { get; } = organizationName;
        public IReadOnlyList<(string Id, string Name)> Others { get; } = others;
    }

    // Account MFA routes use the same ts-auth origin and the user's own bearer.
    public sealed partial class TsAuthGateway : ITsAuthMfa
    {
        public const string MfaRequiredCode = "mfa_required";
        public static readonly string ManageMfaUrl = new Uri(AuthOrigin, "ui/" + Tenant + "/security").ToString();

        public async Task<AccountMfaStatus?> GetAccountMfaAsync(SessionRecord session, CancellationToken cancellationToken)
        {
            try
            {
                using var body = await MfaCallAsync(HttpMethod.Get, "account/mfa", session, null, cancellationToken);
                var root = body.RootElement;
                var manage = root.TryGetProperty("manage_url", out var url) && Uri.TryCreate(url.GetString(), UriKind.Absolute, out var parsed) &&
                    parsed.Scheme == Uri.UriSchemeHttps && parsed.Host == AuthOrigin.Host ? parsed.ToString() : ManageMfaUrl;
                return new(root.GetProperty("enabled").GetBoolean(), root.TryGetProperty("recovery_codes_remaining", out var codes) ? codes.GetInt32() : 0, manage);
            }
            catch (Exception error) when (Unreported(error, cancellationToken)) { return null; }
        }

        public async Task<bool?> GetRequireMfaAsync(SessionRecord session, string organizationId, CancellationToken cancellationToken)
        {
            try
            {
                using var body = await MfaCallAsync(HttpMethod.Get, "organizations/" + Id(organizationId), session, null, cancellationToken);
                return body.RootElement.TryGetProperty("require_mfa", out var value) && value.ValueKind is JsonValueKind.True or JsonValueKind.False ? value.GetBoolean() : null;
            }
            catch (Exception error) when (Unreported(error, cancellationToken)) { return null; }
        }

        public async Task SetRequireMfaAsync(SessionRecord session, string organizationId, bool requireMfa, CancellationToken cancellationToken)
        {
            using var body = await MfaCallAsync(HttpMethod.Put, "organizations/" + Id(organizationId) + "/mfa-policy", session,
                requireMfa ? "{\"require_mfa\":true}" : "{\"require_mfa\":false}", cancellationToken);
            if (!body.RootElement.TryGetProperty("require_mfa", out var value) || value.ValueKind is not (JsonValueKind.True or JsonValueKind.False) || value.GetBoolean() != requireMfa)
            {
                throw new IOException("ts-auth returned an invalid organization.");
            }
        }

        private async Task<bool> MfaRefusedAsync(SessionRecord session, string organizationId, CancellationToken cancellationToken)
        {
            try { using var _ = await MfaCallAsync(HttpMethod.Get, "organizations/" + Id(organizationId), session, null, cancellationToken); return false; }
            catch (AccountAccessException error) { return error.Code == MfaRequiredCode; }
            catch (Exception error) when (Unreported(error, cancellationToken)) { return false; }
        }

        private static bool Unreported(Exception error, CancellationToken cancellationToken)
        {
            return error is AccountAccessException or HttpRequestException or IOException or JsonException or KeyNotFoundException or InvalidOperationException or FormatException ||
            error is OperationCanceledException && !cancellationToken.IsCancellationRequested;
        }

        // Refusals become account errors with ts-auth's fixed code, as on the SDK routes; server failures stay HttpRequestException.
        private async Task<JsonDocument> MfaCallAsync(HttpMethod method, string path, SessionRecord session, string? json, CancellationToken cancellationToken)
        {
            using var request = new HttpRequestMessage(method, new Uri(AuthOrigin, "v1/" + Tenant + "/" + path));
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", Bearer(session));
            request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
            if (json is not null)
            {
                request.Content = new StringContent(json, Encoding.UTF8, "application/json");
            }

            using var client = http.CreateClient("platform");
            using var response = await client.SendAsync(request, cancellationToken);
            if ((int)response.StatusCode is >= 400 and < 500)
            {
                string? code = null;
                try
                {
                    using var error = JsonDocument.Parse(await response.Content.ReadAsStringAsync(cancellationToken));
                    code = error.RootElement.GetProperty("error").GetProperty("code").GetString();
                }
                catch (Exception failure) when (failure is JsonException or KeyNotFoundException or InvalidOperationException) { }
                var status = (int)response.StatusCode;
                throw new AccountAccessException(Message(code, response.StatusCode), status == 400 ? StatusCodes.Status422UnprocessableEntity : status, code ?? "");
            }
            response.EnsureSuccessStatusCode();
            return JsonDocument.Parse(await response.Content.ReadAsStringAsync(cancellationToken));
        }
    }

    // The page an organization that requires two-factor shows a member who has not set it up.
    public sealed record MfaRequiredPage(DocumentModel Document, string OrganizationName, string CsrfToken, IReadOnlyList<(string Id, string Name)> Others)
    {
        public string ManageUrl => TsAuthGateway.ManageMfaUrl;
    }
}
