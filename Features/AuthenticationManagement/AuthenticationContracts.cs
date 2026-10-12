using System.ComponentModel;
using System.Text.Json.Serialization;

namespace TerraScale.Dashboard.Features.AuthenticationManagement;

// Concrete output contracts retain the Go console's discovery surface. No
// record below is a source of project identity or authorization credentials.
public sealed record AuthenticationCatalog(IReadOnlyList<AuthenticationSectionDefinition> Sections,
    IReadOnlyList<AuthenticationProviderDefinition> ConnectionProviders, IReadOnlyList<AuthenticationStrategy> Strategies);
public sealed record AuthenticationSectionDefinition(string Key, string Title, string Description, bool Editable, bool Revocable,
    IReadOnlyList<string>? Actions = null, IReadOnlyList<AuthenticationFieldDefinition>? Fields = null);
public sealed record AuthenticationFieldDefinition(string Key, string Label,
    [property: Description("text, email, url, number, textarea, password (secret), organization or user (record ID), role, select, or choices (comma-separated).")] string Type,
    bool Required, bool Immutable, bool Unique, string? Hint = null, string? Example = null, IReadOnlyList<string>? Options = null);
public sealed record AuthenticationProviderDefinition(string Protocol, string Kind, string Description, IReadOnlyList<AuthenticationFieldDefinition> Fields);
public sealed record AuthenticationStrategy(string Key, string Label, string Page, string Description, bool Enabled, bool FirstFactor, string? Requires = null);
public sealed record AuthenticationStrategies(IReadOnlyList<AuthenticationStrategy> Strategies);
public sealed record AuthenticationBranding(string Name, string? LogoUrl = null, string? ThemeColor = null);
public sealed record AuthenticationEvent(string Action, string Target, string Actor, DateTimeOffset OccurredAt);
public sealed record AuthenticationEvents(IReadOnlyList<AuthenticationEvent> Events);
public sealed record AuthenticationHostedPage(string Label, string Url);
public sealed record AuthenticationChecklistStep(string Title, string Detail, string Section, bool Done);
public sealed record AuthenticationOverview(string InstanceName, string Slug, bool CanManage, string Issuer, string DiscoveryUrl,
    string JwksUrl, string UserinfoUrl, string SignInUrl, string ManagementApi, IReadOnlyList<AuthenticationHostedPage> HostedPages,
    IReadOnlyDictionary<string, int> Counts, int BlockedUsers, IReadOnlyList<string> EnabledConnections,
    IReadOnlyList<AuthenticationStrategy> Strategies, AuthenticationBranding Branding,
    IReadOnlyList<AuthenticationChecklistStep> Checklist, IReadOnlyList<AuthenticationEvent> RecentEvents);
public sealed record AuthenticationResource(string Id, string Section, string Name, string Status,
    [property: Description("Stored field values. Secrets are never stored; credential_configured reports whether one was submitted.")] IReadOnlyDictionary<string, string> Values,
    DateTimeOffset CreatedAt, string? Detail = null, IReadOnlyList<string>? Tags = null, string? CallbackUrl = null, IReadOnlyList<string>? Actions = null);
public sealed record AuthenticationResources(string Section, bool CanManage, IReadOnlyList<AuthenticationResource> Resources);
public sealed record AuthenticationDeleted(string Id, string Outcome);

public sealed record AuthenticationEmailBranding(
    [property: JsonPropertyName("brand_name")] string? BrandName = null,
    [property: JsonPropertyName("logo_url")] string? LogoUrl = null,
    [property: JsonPropertyName("logo_dark_url")] string? LogoDarkUrl = null,
    [property: JsonPropertyName("logo_width")] int? LogoWidth = null,
    [property: JsonPropertyName("logo_height")] int? LogoHeight = null,
    [property: JsonPropertyName("icon_url")] string? IconUrl = null,
    [property: JsonPropertyName("primary_color")] string? PrimaryColor = null,
    [property: JsonPropertyName("accent_color")] string? AccentColor = null,
    string? Theme = null,
    [property: JsonPropertyName("button_style")] string? ButtonStyle = null,
    [property: JsonPropertyName("footer_text")] string? FooterText = null,
    [property: JsonPropertyName("support_email")] string? SupportEmail = null,
    [property: JsonPropertyName("support_url")] string? SupportUrl = null,
    [property: JsonPropertyName("website_url")] string? WebsiteUrl = null,
    [property: JsonPropertyName("sender_name")] string? SenderName = null,
    [property: JsonPropertyName("reply_to")] string? ReplyTo = null);
public sealed record AuthenticationEmailFields(string? Subject = null, string? Preheader = null, string? Heading = null,
    string? Body = null, string? Button = null, string? Footnote = null);
public sealed record AuthenticationEmailSummary(string Kind, string Locale, bool Overridden, IReadOnlyList<string> Variables);
public sealed record AuthenticationEmailTemplate(string Kind, string Locale, int Version, bool Overridden,
    IReadOnlyList<string> Variables, AuthenticationEmailFields Fields, AuthenticationEmailFields Defaults);
public sealed record AuthenticationEmails(string Tenant, bool CanManage, int Version, AuthenticationEmailBranding Branding,
    AuthenticationEmailBranding BrandingDefaults, IReadOnlyList<AuthenticationEmailSummary> Templates, AuthenticationEmailTemplate Template);
public sealed record AuthenticationRenderedEmail(string Subject, string Html, string Text);
public sealed record AuthenticationEmailSaved(int Version);
public sealed record AuthenticationEmailSent(string To);
