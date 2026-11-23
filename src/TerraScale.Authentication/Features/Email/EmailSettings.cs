using Dapper;
using System.Net.Mail;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using TerraScale.Authentication.Contracts;
using TerraScale.Authentication.Storage;

namespace TerraScale.Authentication.Features.Email;

public sealed record EmailTemplateFields(
    [property: JsonPropertyName("subject")] string? Subject = null,
    [property: JsonPropertyName("preheader")] string? Preheader = null,
    [property: JsonPropertyName("heading")] string? Heading = null,
    [property: JsonPropertyName("body")] string? Body = null,
    [property: JsonPropertyName("button")] string? Button = null,
    [property: JsonPropertyName("footnote")] string? Footnote = null);

public sealed record EmailBranding
{
    [JsonPropertyName("brand_name")]
    public string? BrandName { get; init; }

    [JsonPropertyName("logo_url")]
    public string? LogoUrl { get; init; }

    [JsonPropertyName("logo_dark_url")]
    public string? LogoDarkUrl { get; init; }

    [JsonPropertyName("logo_width")]
    public int? LogoWidth { get; init; }

    [JsonPropertyName("logo_height")]
    public int? LogoHeight { get; init; }

    [JsonPropertyName("icon_url")]
    public string? IconUrl { get; init; }

    [JsonPropertyName("primary_color")]
    public string? PrimaryColor { get; init; }

    [JsonPropertyName("accent_color")]
    public string? AccentColor { get; init; }

    [JsonPropertyName("theme")]
    public string? Theme { get; init; }

    [JsonPropertyName("button_style")]
    public string? ButtonStyle { get; init; }

    [JsonPropertyName("footer_text")]
    public string? FooterText { get; init; }

    [JsonPropertyName("support_email")]
    public string? SupportEmail { get; init; }

    [JsonPropertyName("support_url")]
    public string? SupportUrl { get; init; }

    [JsonPropertyName("website_url")]
    public string? WebsiteUrl { get; init; }

    [JsonPropertyName("sender_name")]
    public string? SenderName { get; init; }

    [JsonPropertyName("reply_to")]
    public string? ReplyTo { get; init; }
}

public sealed record EmailActor(
    [property: JsonPropertyName("id")] string Id,
    [property: JsonPropertyName("email")] string? Email = null);

public sealed record EmailTemplateOverride(
    [property: JsonPropertyName("kind")] string Kind,
    [property: JsonPropertyName("locale")] string Locale,
    [property: JsonPropertyName("fields")] EmailTemplateFields Fields);

public sealed record EmailConfig(
    [property: JsonPropertyName("instance_id")] string InstanceId,
    [property: JsonPropertyName("version")] long Version,
    [property: JsonPropertyName("created_at")] DateTimeOffset? CreatedAt,
    [property: JsonPropertyName("actor")] EmailActor? Actor,
    [property: JsonPropertyName("change")] string? Change,
    [property: JsonPropertyName("branding")] EmailBranding Branding,
    [property: JsonPropertyName("templates")] IReadOnlyList<EmailTemplateOverride> Templates)
{
    public static EmailConfig Defaults(string instanceId) => new(instanceId, 0, null, null, null, new EmailBranding(), []);

    public EmailTemplateFields? Override(string kind, string locale) =>
        this.Templates.FirstOrDefault(entry => entry.Kind == kind && entry.Locale == locale)?.Fields;
}

public sealed class EmailSettings(AuthData data, TimeProvider clock)
{
    public async Task<EmailConfig> GetAsync(string instanceId, CancellationToken cancellationToken = default) =>
        await data.ReadAsync(connection => connection.QueryFirstOrDefaultAsync<EmailConfig>(
            "SELECT * FROM email_configs WHERE instance_id = @instanceId ORDER BY version DESC LIMIT 1", new { instanceId }), cancellationToken) is { } stored
            ? Valid(stored, instanceId) : EmailConfig.Defaults(instanceId);

    public async Task<EmailConfig?> GetVersionAsync(string instanceId, long version, CancellationToken cancellationToken = default)
    {
        if (version == 0)
        {
            return EmailConfig.Defaults(instanceId);
        }

        return await data.ReadAsync(connection => connection.QuerySingleOrDefaultAsync<EmailConfig>(
            "SELECT * FROM email_configs WHERE instance_id = @instanceId AND version = @version", new { instanceId, version }), cancellationToken) is { } stored
            ? Valid(stored, instanceId) : null;
    }

    public async Task<IReadOnlyList<EmailConfig>> ListVersionsAsync(string instanceId, int limit, CancellationToken cancellationToken = default) =>
        [.. (await data.ReadAsync(connection => connection.QueryAsync<EmailConfig>(
            "SELECT * FROM email_configs WHERE instance_id = @instanceId ORDER BY version DESC LIMIT @limit", new { instanceId, limit }), cancellationToken))
            .Select(config => Valid(config, instanceId))];

    public async Task<EmailConfig?> SaveAsync(string instanceId, long expectedVersion, EmailActor actor, string change, Func<EmailConfig, EmailConfig> update, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(update);
        var current = await this.GetAsync(instanceId, cancellationToken);
        if (current.Version != expectedVersion)
        {
            return null;
        }

        var next = update(current) with
        {
            InstanceId = instanceId,
            Version = current.Version + 1,
            CreatedAt = clock.GetUtcNow(),
            Actor = actor,
            Change = change,
        };
        return await data.SaveAsync(Change.Insert(next), cancellationToken) ? next : null;
    }

    private static EmailConfig Valid(EmailConfig config, string instanceId)
    {
        try
        {
            if (config is { Branding: not null, Templates: not null } && config.InstanceId == instanceId
                && config.Version > 0 && EmailValidation.Branding(config.Branding) == config.Branding
                && config.Templates.All(entry => entry?.Fields is not null && AuthEmails.Kinds.TryGetValue(entry.Kind ?? string.Empty, out var kind)
                    && AuthEmails.Locales.Contains(entry.Locale) && EmailValidation.Fields(kind, entry.Fields) == entry.Fields)
                && config.Templates.DistinctBy(entry => (entry.Kind, entry.Locale)).Count() == config.Templates.Count)
            {
                return config;
            }
        }
        catch (ApiException error) when (error.Status == StatusCodes.Status422UnprocessableEntity)
        {
            throw ApiException.Unavailable(new InvalidDataException("A stored email configuration is malformed.", error));
        }

        throw ApiException.Unavailable(new InvalidDataException("A stored email configuration is malformed."));
    }
}

public static partial class EmailValidation
{
    public static readonly IReadOnlyDictionary<string, int> FieldLimits = new Dictionary<string, int>(StringComparer.Ordinal)
    {
        ["subject"] = 200,
        ["preheader"] = 200,
        ["heading"] = 200,
        ["body"] = 2000,
        ["button"] = 60,
        ["footnote"] = 500,
    };

    public static EmailBranding Branding(EmailBranding? branding)
    {
        branding ??= new EmailBranding();
        var checkedBranding = branding with
        {
            BrandName = Text("branding.brand_name", branding.BrandName, 80, multiline: false),
            LogoUrl = HttpsUrl("branding.logo_url", branding.LogoUrl),
            LogoDarkUrl = HttpsUrl("branding.logo_dark_url", branding.LogoDarkUrl),
            IconUrl = HttpsUrl("branding.icon_url", branding.IconUrl),
            PrimaryColor = Color("branding.primary_color", branding.PrimaryColor),
            AccentColor = Color("branding.accent_color", branding.AccentColor),
            Theme = OneOf("branding.theme", branding.Theme, "auto", "light", "dark"),
            ButtonStyle = OneOf("branding.button_style", branding.ButtonStyle, "rounded", "pill", "square"),
            FooterText = Text("branding.footer_text", branding.FooterText, 500, multiline: true),
            SupportEmail = Address("branding.support_email", branding.SupportEmail),
            SupportUrl = HttpsUrl("branding.support_url", branding.SupportUrl),
            WebsiteUrl = HttpsUrl("branding.website_url", branding.WebsiteUrl),
            SenderName = Text("branding.sender_name", branding.SenderName, 80, multiline: false),
            ReplyTo = Address("branding.reply_to", branding.ReplyTo),
        };
        if (checkedBranding.SenderName?.IndexOfAny(['@', '<', '>', '"']) >= 0)
        {
            throw Invalid("branding.sender_name must not contain @, <, > or \".");
        }

        if (checkedBranding.LogoUrl is null)
        {
            if (checkedBranding.LogoDarkUrl is not null || branding.LogoWidth is not null || branding.LogoHeight is not null)
            {
                throw Invalid("branding.logo_dark_url, logo_width and logo_height need branding.logo_url.");
            }
        }
        else if (branding.LogoWidth is not (>= 16 and <= 400) || branding.LogoHeight is not (>= 16 and <= 120))
        {
            throw Invalid("branding.logo_url needs logo_width (16 to 400) and logo_height (16 to 120), in CSS pixels.");
        }

        return checkedBranding;
    }

    public static EmailTemplateFields Fields(AuthEmailKind kind, EmailTemplateFields? fields)
    {
        fields ??= new EmailTemplateFields();
        var variables = AuthEmails.Variables(kind);
        string? Check(string name, string? value)
        {
            var text = Text($"fields.{name}", value, FieldLimits[name], multiline: name is "body" or "footnote");
            if (text is null)
            {
                return null;
            }

            foreach (Match placeholder in AnyPlaceholder().Matches(text))
            {
                if (!variables.Contains(placeholder.Groups["name"].Value))
                {
                    throw Invalid($"fields.{name} uses {{{{ {placeholder.Groups["name"].Value} }}}}, which {AuthEmails.Name(kind)} does not have. Use: {string.Join(", ", variables)}.");
                }
            }

            var rest = AnyPlaceholder().Replace(text, string.Empty);
            return rest.Contains("{{", StringComparison.Ordinal) || rest.Contains("}}", StringComparison.Ordinal)
                || rest.Contains("{%", StringComparison.Ordinal) || rest.Contains("%}", StringComparison.Ordinal)
                ? throw Invalid($"fields.{name} may only use {{{{ name }}}} placeholders.")
                : text;
        }

        var button = Check("button", fields.Button);
        if (button is not null && kind is not (AuthEmailKind.MagicLink or AuthEmailKind.Invitation))
        {
            throw Invalid($"fields.button applies only to magic_link and invitation.");
        }

        return new EmailTemplateFields(Check("subject", fields.Subject), Check("preheader", fields.Preheader), Check("heading", fields.Heading),
            Check("body", fields.Body), button, Check("footnote", fields.Footnote));
    }

    public static EmailActor Actor(EmailActor? actor) =>
        actor is null || Text("actor.id", actor.Id, 128, multiline: false) is not { } id
            ? throw Invalid("actor.id is required.")
            : new EmailActor(id, Address("actor.email", actor.Email));

    public static string? Address(string field, string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        value = value.Trim();
        return value.Length <= 254 && !value.Any(char.IsControl) && !value.Any(char.IsWhiteSpace) && value.Count(character => character == '@') == 1
            && MailAddress.TryCreate(value, out var parsed) && parsed.Address == value && string.IsNullOrEmpty(parsed.DisplayName)
            ? value
            : throw Invalid($"{field} must be a plain email address.");
    }

    private static string? Text(string field, string? value, int limit, bool multiline)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        value = value.Replace("\r\n", "\n", StringComparison.Ordinal).Trim();
        return value.Length > limit ? throw Invalid($"{field} is longer than {limit} characters.")
            : value.Any(character => char.IsControl(character) && !(multiline && character == '\n')) ? throw Invalid($"{field} contains a control character.")
            : value;
    }

    private static string? HttpsUrl(string field, string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        value = value.Trim();
        return value.Length <= 2048 && Uri.TryCreate(value, UriKind.Absolute, out var url) && url.Scheme == Uri.UriSchemeHttps
            && string.IsNullOrEmpty(url.UserInfo) && !value.Any(character => char.IsControl(character) || char.IsWhiteSpace(character))
            ? url.AbsoluteUri
            : throw Invalid($"{field} must be an https URL without credentials, at most 2048 characters.");
    }

    private static string? Color(string field, string? value) =>
        string.IsNullOrWhiteSpace(value) ? null
        : HexColor().IsMatch(value.Trim()) ? value.Trim().ToLowerInvariant()
        : throw Invalid($"{field} must be #rrggbb.");

    private static string? OneOf(string field, string? value, params string[] allowed) =>
        string.IsNullOrWhiteSpace(value) ? null
        : allowed.Contains(value.Trim(), StringComparer.Ordinal) ? value.Trim()
        : throw Invalid($"{field} must be one of {string.Join(", ", allowed)}.");

    private static ApiException Invalid(string message) => new(StatusCodes.Status422UnprocessableEntity, "invalid_request", message);

    [GeneratedRegex(@"\A#[0-9A-Fa-f]{6}\z")]
    private static partial Regex HexColor();

    [GeneratedRegex(@"\{\{\s*(?<name>[^{}\s]+)\s*\}\}")]
    private static partial Regex AnyPlaceholder();
}

[JsonSourceGenerationOptions]
[JsonSerializable(typeof(EmailBranding))]
[JsonSerializable(typeof(EmailActor))]
[JsonSerializable(typeof(IReadOnlyList<EmailTemplateOverride>))]
internal sealed partial class EmailJson : JsonSerializerContext;
