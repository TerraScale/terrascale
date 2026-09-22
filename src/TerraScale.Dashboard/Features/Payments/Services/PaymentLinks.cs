using TerraScale.Dashboard.Features.Payments.Models;

namespace TerraScale.Dashboard.Features.Payments.Services;

/// <summary>Origin and return-path rules shared by the checkout and portal
/// endpoints. Return paths stay app-relative so a checkout can never bounce a
/// customer to an attacker-controlled host.</summary>
public static class PaymentLinks
{
    public static Uri Origin(HttpContext context, StripePaymentOptions options)
    {
        if (options.PublicOrigin is { Length: > 0 } configured
            && Uri.TryCreate(configured, UriKind.Absolute, out var configuredOrigin))
        {
            return new Uri(configuredOrigin.GetLeftPart(UriPartial.Authority));
        }

        return new Uri($"{context.Request.Scheme}://{context.Request.Host}");
    }

    public static string NormalizeReturnPath(string? value, string fallback)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return fallback;
        }

        var path = value.Trim();
        return path.StartsWith('/')
            && !path.StartsWith("//", StringComparison.Ordinal)
            && !path.Contains("://", StringComparison.Ordinal)
            && !path.Contains('\n')
            && !path.Contains('\r')
            ? path
            : fallback;
    }

    public static string WithPayment(string path, string outcome) =>
        path + (path.Contains('?', StringComparison.Ordinal) ? '&' : '?') + "payment=" + outcome;

    public static Uri Absolute(Uri origin, string returnPath) =>
        new(origin.GetLeftPart(UriPartial.Authority).TrimEnd('/') + "/" + returnPath.TrimStart('/'));
}
