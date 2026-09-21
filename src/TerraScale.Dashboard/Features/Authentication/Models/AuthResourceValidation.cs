using System.Net.Mail;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace TerraScale.Dashboard.Features.Authentication.Models;

public static class AuthResourceValidation
{
    public static string? Validate(AuthSection section, IReadOnlyDictionary<string, string> values, AuthInstanceState state, string id)
    {
        foreach (var field in section.Fields)
        {
            var value = values.GetValueOrDefault(field.Key, "");
            if (field.Required && value.Length == 0) return $"{field.Label} is required.";
            if (value.Length > 16384) return $"{field.Label} is too long (maximum 16,384 characters).";
            if (value.Length == 0) continue;
            if (field.Type == "email" && (!MailAddress.TryCreate(value, out var mail) || mail.Address != value || value.Length > 320))
                return "Enter a valid email address.";
            if (field.Type == "url" && !HttpUrl(value)) return $"{field.Label} must be an absolute HTTP(S) URL.";
            if (field.Type == "select" && !(field.Options ?? []).Contains(value)) return $"Choose a valid {field.Label.ToLowerInvariant()}.";
            if (field.Type == "slug" && !Regex.IsMatch(value, "^[a-z0-9]+(?:-[a-z0-9]+)*$")) return "Use lowercase letters, numbers and hyphens for the slug.";
            if (field.Type == "template" && !Regex.IsMatch(value, "^[a-z0-9_-]{1,48}$")) return "Template names must use 1–48 lowercase letters, numbers, underscores or hyphens.";
            if (field.Type == "domain" && (Uri.CheckHostName(value) != UriHostNameType.Dns || !value.Contains('.'))) return "Enter a domain such as example.com, without a URL scheme or path.";
            if (field.Key == "lifetime_seconds" && (!int.TryParse(value, out var seconds) || seconds is < 1 or > 86400)) return "Token lifetime must be between 1 and 86,400 seconds.";
            if (field.Type is "json" or "claims")
            {
                try
                {
                    using var json = JsonDocument.Parse(value);
                    if (json.RootElement.ValueKind != JsonValueKind.Object) return $"{field.Label} must be a JSON object.";
                    if (field.Type == "claims" && json.RootElement.EnumerateObject().Any(property => property.Value.ValueKind != JsonValueKind.String))
                        return "Claim values must be strings.";
                }
                catch (JsonException) { return $"{field.Label} must be valid JSON."; }
            }
            if (field.Type is "uris" or "origins")
                foreach (var address in value.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
                {
                    if (!HttpUrl(address) || address.Contains('*')) return $"{field.Label} must contain exact HTTP(S) URLs without wildcards.";
                    var uri = new Uri(address);
                    if (uri.Scheme != "https" && !uri.IsLoopback || uri.UserInfo.Length > 0 || uri.Fragment.Length > 0
                        || field.Type == "origins" && (uri.AbsolutePath != "/" || uri.Query.Length > 0))
                        return $"{field.Label} require HTTPS (or loopback HTTP); origins cannot include paths or queries.";
                }
            if (field.Type is "organization" or "user" && !state.Resources.Any(resource => resource.Section == (field.Type == "user" ? "users" : "organizations") && resource.Id == value))
                return $"Choose an existing {field.Type} in this project.";
        }
        foreach (var key in section.Key switch { "users" => new[] { "email", "username" }, "organizations" => ["slug"], "templates" or "domains" => ["name"], _ => Array.Empty<string>() })
            if (values.GetValueOrDefault(key, "").Length > 0 && state.Resources.Any(resource => resource.Section == section.Key && resource.Id != id
                && string.Equals(resource.Value(key), values[key], StringComparison.OrdinalIgnoreCase))) return $"This {key} is already in use.";
        if (section.Key == "memberships" && state.Resources.Any(resource => resource.Section == "memberships" && resource.Id != id
            && resource.Value("user") == values["user"] && resource.Value("organization") == values["organization"])) return "This user is already a member of the organization.";
        if (section.Key == "connections")
        {
            if (values["protocol"] == "OIDC" && values["client_id"].Length == 0) return "An OIDC client ID is required.";
            if (values["protocol"] == "SAML" && (values["sso_url"].Length == 0 || !values["certificate"].Contains("BEGIN CERTIFICATE", StringComparison.Ordinal))) return "SAML connections require a sign-in URL and PEM certificate.";
        }
        if (section.Key == "clients" && values["type"] != "Machine-to-machine" && values["redirect_uris"].Length == 0) return "Add at least one redirect URI for this application.";
        if (section.Key == "keys")
        {
            string[] scopes = ["*", "users", "sessions", "organizations", "oauth-clients", "connections", "directories", "jwt-templates", "webhooks", "audit-events", "instance", "api-keys", "billing", "domains"];
            if (values["scopes"].Split(',', StringSplitOptions.TrimEntries).Any(scope => !scopes.Contains(scope))) return "Choose scopes from the supported API families.";
        }
        if (section.Key == "webhooks" && new Uri(values["url"]).Scheme != "https") return "Webhook destinations must use HTTPS.";
        return null;
    }

    private static bool HttpUrl(string value) => Uri.TryCreate(value, UriKind.Absolute, out var uri) && uri.Scheme is "http" or "https" && uri.Host.Length > 0;
}
