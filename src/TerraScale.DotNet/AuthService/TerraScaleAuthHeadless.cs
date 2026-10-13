using System.Net;

namespace TerraScale.DotNet;

/// <summary>First-party confidential-client headless API. Collect credentials server-side and supply
/// the end user's validated IP, never an untrusted forwarding header. Every request is sent once.</summary>
public sealed class TerraScaleAuthHeadlessClient(TerraScaleAuthClient client)
{
    /// <summary>Starts sign-up, returning an opaque pending token even when the account already exists.
    /// Keep the pending token server-side for code redemption and resend.</summary>
    public Task<TerraScaleAuthHeadlessPendingSignUp> SignUpAsync(
        string clientId, string clientSecret, TerraScaleAuthHeadlessSignUp request, CancellationToken cancellationToken = default) =>
        client.SendAuthenticatedJsonAsync(HttpMethod.Post, Route("/sign-up"),
            request ?? throw new ArgumentNullException(nameof(request)), TerraScaleAuthIntegrationJsonContext.Default.TerraScaleAuthHeadlessSignUp,
            TerraScaleAuthIntegrationJsonContext.Default.TerraScaleAuthHeadlessPendingSignUp,
            TerraScaleAuthCredentials.Basic(clientId, clientSecret), HttpStatusCode.Accepted, 8 * 1024, cancellationToken);

    /// <summary>Starts code sign-in or resends sign-up with its original pending token. Success is 202 with no body.</summary>
    public Task StartEmailCodeAsync(
        string clientId, string clientSecret, TerraScaleAuthHeadlessEmailCode request, CancellationToken cancellationToken = default) =>
        client.SendAuthenticatedForStatusAsync(HttpMethod.Post, Route("/email-code/start"),
            request ?? throw new ArgumentNullException(nameof(request)), TerraScaleAuthIntegrationJsonContext.Default.TerraScaleAuthHeadlessEmailCode,
            TerraScaleAuthCredentials.Basic(clientId, clientSecret), HttpStatusCode.Accepted, 8 * 1024, cancellationToken);

    /// <summary>Starts password recovery. Success is 202 whether or not the account exists.</summary>
    public Task StartPasswordResetAsync(
        string clientId, string clientSecret, TerraScaleAuthHeadlessEmail request, CancellationToken cancellationToken = default) =>
        client.SendAuthenticatedForStatusAsync(HttpMethod.Post, Route("/password-reset/start"),
            request ?? throw new ArgumentNullException(nameof(request)), TerraScaleAuthIntegrationJsonContext.Default.TerraScaleAuthHeadlessEmail,
            TerraScaleAuthCredentials.Basic(clientId, clientSecret), HttpStatusCode.Accepted, 8 * 1024, cancellationToken);

    /// <summary>Consumes a reset code and changes the password (204). The producer ends prior sessions;
    /// already-issued offline access tokens remain valid until expiry.</summary>
    public Task CompletePasswordResetAsync(
        string clientId, string clientSecret, TerraScaleAuthHeadlessPasswordReset request, CancellationToken cancellationToken = default) =>
        client.SendAuthenticatedForStatusAsync(HttpMethod.Post, Route("/password-reset/complete"),
            request ?? throw new ArgumentNullException(nameof(request)), TerraScaleAuthIntegrationJsonContext.Default.TerraScaleAuthHeadlessPasswordReset,
            TerraScaleAuthCredentials.Basic(clientId, clientSecret), HttpStatusCode.NoContent, 8 * 1024, cancellationToken);

    /// <summary>Signs in with the existing custom password grant, using Basic client authentication.</summary>
    public Task<TerraScaleAuthTokenResponse> SignInWithPasswordAsync(
        string clientId, string clientSecret, string email, string password, IPAddress ipAddress,
        string? scope = null, CancellationToken cancellationToken = default) =>
        GrantAsync(clientId, clientSecret, "password", email, "password", password, ipAddress, scope, null, cancellationToken);

    /// <summary>Redeems a sign-in or sign-up code. Sign-up requires its original pending token.</summary>
    public Task<TerraScaleAuthTokenResponse> SignInWithEmailCodeAsync(
        string clientId, string clientSecret, string email, string code, IPAddress ipAddress,
        string? pendingToken = null, string? scope = null, CancellationToken cancellationToken = default) =>
        GrantAsync(clientId, clientSecret, "email-code", email, "code", code, ipAddress, scope, pendingToken, cancellationToken);

    private Task<TerraScaleAuthTokenResponse> GrantAsync(
        string clientId, string clientSecret, string grant, string email, string secretName, string secret,
        IPAddress ipAddress, string? scope, string? pendingToken, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(email);
        ArgumentException.ThrowIfNullOrWhiteSpace(secret);
        ArgumentNullException.ThrowIfNull(ipAddress);
        var form = new List<KeyValuePair<string, string>>
        {
            new("grant_type", "urn:terrascale:params:oauth:grant-type:" + grant),
            new("email", email), new(secretName, secret), new("ip_address", ipAddress.ToString())
        };
        if (scope is not null) form.Add(new("scope", scope));
        if (pendingToken is not null) form.Add(new("pending_token", pendingToken));
        return client.SendOAuthFormAsync("/" + TerraScaleAuthPaths.Escape(client.InstanceSlug) + "/oauth/token", form,
            TerraScaleAuthCredentials.Basic(clientId, clientSecret), TerraScaleAuthIntegrationJsonContext.Default.TerraScaleAuthTokenResponse, cancellationToken);
    }

    private string Route(string relative) => "/" + TerraScaleAuthPaths.Escape(client.InstanceSlug) + "/headless/v1" + relative;
}
