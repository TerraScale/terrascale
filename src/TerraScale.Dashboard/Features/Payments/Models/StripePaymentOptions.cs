using System.Collections.Immutable;

namespace TerraScale.Dashboard.Features.Payments.Models;

/// <summary>
/// Stripe sandbox (test-mode) billing configuration. The dashboard reads the
/// same <c>Payments__Stripe__*</c> names the previous ts-dashboard deployment
/// published, so an operator moves a sandbox between the two without renaming
/// secrets. Secret keys and webhook secrets are never logged or rendered.
/// The API version is pinned by Stripe.net itself, not by configuration.
/// </summary>
public sealed record StripePaymentOptions(
    bool Enabled,
    string Mode,
    string SecretKey,
    string WebhookSecret,
    string? PublicOrigin,
    ImmutableDictionary<string, string> PriceIds)
{
    public const string SectionName = "Payments:Stripe";

    /// <summary>Sandbox keys are <c>sk_test_</c>; restricted sandbox keys are
    /// <c>rk_test_</c>. Live keys never carry the <c>_test_</c> segment.</summary>
    public bool IsTestMode =>
        SecretKey.StartsWith("sk_test_", StringComparison.Ordinal)
        || SecretKey.StartsWith("rk_test_", StringComparison.Ordinal);

    /// <summary>Configured Stripe Price for a plan key and billing interval.
    /// <c>scale</c> is accepted as the legacy name for the Business plan.</summary>
    public string? PriceId(string planKey, string interval)
    {
        var key = $"{NormalizePlan(planKey)}:{(interval == "annual" ? "annual" : "monthly")}";
        return PriceIds.TryGetValue(key, out var priceId) ? priceId : null;
    }

    public static string NormalizePlan(string? planKey) => planKey?.Trim().ToLowerInvariant() switch
    {
        "starter" => "starter",
        "pro" => "pro",
        "business" or "scale" => "business",
        _ => "",
    };

    public static StripePaymentOptions Load(IConfiguration configuration, IHostEnvironment environment)
    {
        string? Read(string name) => configuration[$"{SectionName}:{name}"]?.Trim();
        var enabled = Read("SecretKey") is { Length: > 0 };
        if (!enabled)
        {
            return new(false, "test", "", "", null, ImmutableDictionary<string, string>.Empty);
        }

        var secretKey = Read("SecretKey")!;
        var webhookSecret = Read("WebhookSecret") ?? "";
        var mode = (Read("Mode") ?? "test").ToLowerInvariant();
        if (mode is not ("test" or "live"))
        {
            throw new InvalidOperationException($"{SectionName}:Mode must be \"test\" or \"live\".");
        }

        var isTestKey = secretKey.StartsWith("sk_test_", StringComparison.Ordinal)
            || secretKey.StartsWith("rk_test_", StringComparison.Ordinal);
        if (mode == "test" && !isTestKey)
        {
            throw new InvalidOperationException($"{SectionName}:Mode is \"test\" but the secret key is not a test-mode key.");
        }

        if (mode == "live" && isTestKey)
        {
            throw new InvalidOperationException($"{SectionName}:Mode is \"live\" but the secret key is a test-mode key.");
        }

        if (environment.IsProduction() && isTestKey
            && configuration["TS_DASHBOARD_PRODUCTION_DEMO"] != "1"
            && configuration[$"{SectionName}:EnableTestModeInProduction"] != "1")
        {
            throw new InvalidOperationException(
                "The Stripe sandbox is development-only; set TS_DASHBOARD_PRODUCTION_DEMO=1 without Stripe keys, or explicitly acknowledge test mode in production.");
        }

        if (webhookSecret.Length == 0)
        {
            throw new InvalidOperationException($"{SectionName}:WebhookSecret is required when Stripe payments are enabled.");
        }

        var prices = ImmutableDictionary.CreateBuilder<string, string>(StringComparer.Ordinal);
        foreach (var plan in new[] { "starter", "pro", "business", "scale" })
        {
            foreach (var interval in new[] { "monthly", "annual" })
            {
                var value = Read($"{Capitalize(plan)}{Capitalize(interval)}PriceId");
                if (value is not { Length: > 0 })
                {
                    continue;
                }

                if (!value.StartsWith("price_", StringComparison.Ordinal))
                {
                    throw new InvalidOperationException($"{SectionName}:{Capitalize(plan)}{Capitalize(interval)}PriceId must be a Stripe price id.");
                }

                // Legacy "Scale" names the Business plan from the previous
                // deployment; the Business key wins when both are configured.
                prices.TryAdd($"{NormalizePlan(plan)}:{interval}", value);
            }
        }

        return new(
            true,
            mode,
            secretKey,
            webhookSecret,
            Read("PublicOrigin") is { Length: > 0 } publicOrigin ? publicOrigin.TrimEnd('/') : null,
            prices.ToImmutable());
    }

    private static string Capitalize(string value) => char.ToUpperInvariant(value[0]) + value[1..];
}
