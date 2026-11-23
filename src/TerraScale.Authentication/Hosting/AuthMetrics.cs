using System.Diagnostics.Metrics;
using OpenIddict.Server;
using OidcErrors = OpenIddict.Abstractions.OpenIddictConstants.Errors;
using static OpenIddict.Abstractions.OpenIddictConstants;
using static OpenIddict.Server.OpenIddictServerEvents;

namespace TerraScale.Authentication.Hosting;

public static class AuthMetrics
{
    public const string MeterName = "TerraScale.Authentication";

    private static readonly Meter Meter = new(MeterName);

    private static readonly Counter<long> SignIns = Meter.CreateCounter<long>("ts_auth.sign_ins", description: "Sign-in attempts by method and outcome.");

    private static readonly Counter<long> Tokens = Meter.CreateCounter<long>("ts_auth.tokens", description: "Token endpoint answers OpenIddict wrote, by grant and outcome.");

    private static readonly Counter<long> Emails = Meter.CreateCounter<long>("ts_auth.emails", description: "Transactional email deliveries by outcome.");

    private static readonly Histogram<double> EmailStageSeconds = Meter.CreateHistogram<double>("ts_auth.email_stage", unit: "s",
        description: "Email queue, preparation and sender duration; sender acceptance does not prove inbox delivery.");

    public static void EmailStage(string stage, double seconds) =>
        EmailStageSeconds.Record(Math.Max(0, seconds), new KeyValuePair<string, object?>("stage", stage));

    public static void SignIn(string method, string outcome) =>
        SignIns.Add(1, new KeyValuePair<string, object?>("method", method), new KeyValuePair<string, object?>("outcome", outcome));

    public static void Email(string outcome) => Emails.Add(1, new KeyValuePair<string, object?>("outcome", outcome));

    public static void Token(string? grantType, string? error) =>
        Tokens.Add(1, new KeyValuePair<string, object?>("grant_type", Grant(grantType)), new KeyValuePair<string, object?>("outcome", Outcome(error)));

    internal static void Publish()
    {
        foreach (var outcome in new[] { "sent", "failed", "bounced", "not_configured", "capacity_refused", "provider_exhausted", "admission_unavailable", "failover" })
        {
            Emails.Add(0, new KeyValuePair<string, object?>("outcome", outcome));
        }

        (string Method, string Outcome)[] signIns =
        [
            ("hosted_code", "rate_limited"), ("hosted_code", "refused"), ("hosted_code", "succeeded"),
            ("hosted_mfa", "challenged"), ("hosted_mfa", "expired"), ("hosted_mfa", "rate_limited"), ("hosted_mfa", "refused"), ("hosted_mfa", "succeeded"),
            ("hosted_passkey", "rate_limited"), ("hosted_passkey", "refused"), ("hosted_passkey", "expired"), ("hosted_passkey", "succeeded"),
            ("hosted_password", "rate_limited"), ("hosted_password", "refused"), ("hosted_password", "succeeded"),
            ("hosted_sign_up", "rate_limited"), ("hosted_sign_up", "refused"), ("hosted_sign_up", "succeeded"),
            ("hosted_social", "refused"), ("hosted_social", "succeeded"),
            ("hosted_sso", "rate_limited"), ("hosted_sso", "refused"), ("hosted_sso", "succeeded"),
        ];
        foreach (var (method, outcome) in signIns)
        {
            SignIns.Add(0, new KeyValuePair<string, object?>("method", method), new KeyValuePair<string, object?>("outcome", outcome));
        }

        string?[] grants = [GrantTypes.AuthorizationCode, GrantTypes.RefreshToken, GrantTypes.ClientCredentials, GrantTypes.TokenExchange, "other"];
        string?[] errors = [null, OidcErrors.InvalidRequest, OidcErrors.InvalidClient, OidcErrors.InvalidGrant, OidcErrors.UnauthorizedClient, OidcErrors.UnsupportedGrantType, OidcErrors.InvalidScope, OidcErrors.TemporarilyUnavailable, OidcErrors.ServerError, "other"];
        foreach (var grant in grants)
        {
            foreach (var error in errors)
            {
                Tokens.Add(0, new KeyValuePair<string, object?>("grant_type", Grant(grant)), new KeyValuePair<string, object?>("outcome", Outcome(error)));
            }
        }
    }

    internal static string Grant(string? grantType) => grantType switch
    {
        GrantTypes.AuthorizationCode => "authorization_code",
        GrantTypes.RefreshToken => "refresh_token",
        GrantTypes.ClientCredentials => "client_credentials",
        GrantTypes.TokenExchange => "token_exchange",
        _ => "other",
    };

    internal static string Outcome(string? error) => error switch
    {
        null or "" => "issued",
        OidcErrors.InvalidRequest or OidcErrors.InvalidClient or OidcErrors.InvalidGrant or OidcErrors.UnauthorizedClient
            or OidcErrors.UnsupportedGrantType or OidcErrors.InvalidScope or OidcErrors.TemporarilyUnavailable or OidcErrors.ServerError => error,
        _ => "other",
    };
}

public sealed class CountTokenResponses : IOpenIddictServerHandler<ApplyTokenResponseContext>
{
    public ValueTask HandleAsync(ApplyTokenResponseContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        AuthMetrics.Token(context.Request?.GrantType, context.Response.Error);
        return ValueTask.CompletedTask;
    }
}
