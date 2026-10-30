using System.Diagnostics.Metrics;
using OpenIddict.Server;
using static OpenIddict.Abstractions.OpenIddictConstants;
using static OpenIddict.Server.OpenIddictServerEvents;

namespace TerraScale.Authentication.Hosting;

/// <summary>
/// Outcome counters for the Grafana dashboards (ops/grafana). Every label has a fixed, small set of values: nothing a
/// client sends reaches a label unmapped.
/// </summary>
public static class AuthMetrics
{
    /// <summary>The meter the counters are recorded on.</summary>
    public const string MeterName = "TerraScale.Authentication";

    private static readonly Meter Meter = new(MeterName);

    private static readonly Counter<long> SignIns = Meter.CreateCounter<long>("ts_auth.sign_ins", description: "Sign-in attempts by method and outcome.");

    private static readonly Counter<long> Tokens = Meter.CreateCounter<long>("ts_auth.tokens", description: "Token endpoint answers OpenIddict wrote, by grant and outcome.");

    private static readonly Counter<long> Emails = Meter.CreateCounter<long>("ts_auth.emails", description: "Transactional email deliveries by outcome.");

    private static readonly Histogram<double> EmailStageSeconds = Meter.CreateHistogram<double>("ts_auth.email_stage", unit: "s",
        description: "Email queue, preparation and sender duration; sender acceptance does not prove inbox delivery.");

    /// <summary>Records a fixed mail stage (queue, retry_wait, prepare, send), without recipient or tenant labels.</summary>
    public static void EmailStage(string stage, double seconds) =>
        EmailStageSeconds.Record(Math.Max(0, seconds), new KeyValuePair<string, object?>("stage", stage));

    /// <summary>Records a sign-in attempt. <paramref name="method"/> is hosted_code, hosted_password, hosted_sign_up or hosted_social.</summary>
    public static void SignIn(string method, string outcome) =>
        SignIns.Add(1, new KeyValuePair<string, object?>("method", method), new KeyValuePair<string, object?>("outcome", outcome));

    /// <summary>Records an email delivery: sent, failed, bounced or not_configured.</summary>
    public static void Email(string outcome) => Emails.Add(1, new KeyValuePair<string, object?>("outcome", outcome));

    /// <summary>Records a token response: issued, or its OAuth error code (other for anything non-standard).</summary>
    public static void Token(string? grantType, string? error) =>
        Tokens.Add(1, new KeyValuePair<string, object?>("grant_type", Grant(grantType)), new KeyValuePair<string, object?>("outcome", Outcome(error)));

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
        Errors.InvalidRequest or Errors.InvalidClient or Errors.InvalidGrant or Errors.UnauthorizedClient
            or Errors.UnsupportedGrantType or Errors.InvalidScope or Errors.TemporarilyUnavailable or Errors.ServerError => error,
        _ => "other",
    };
}

/// <summary>Counts every token response OpenIddict applies (<see cref="AuthMetrics.Token"/>).</summary>
public sealed class CountTokenResponses : IOpenIddictServerHandler<ApplyTokenResponseContext>
{
    /// <inheritdoc />
    public ValueTask HandleAsync(ApplyTokenResponseContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        AuthMetrics.Token(context.Request?.GrantType, context.Response.Error);
        return ValueTask.CompletedTask;
    }
}
