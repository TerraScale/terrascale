using System.Net.Mail;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using TerraScale.Auth.Contracts;
using TerraScale.Auth.Store;
using TerraScale.DotNet.TerraBase;

namespace TerraScale.Auth.Features.Email;

/// <summary>The editable copy of one email kind in one locale. In an override, <see langword="null"/> keeps the default.</summary>
/// <param name="Subject">Subject line.</param>
/// <param name="Preheader">Inbox preview line.</param>
/// <param name="Heading">Heading.</param>
/// <param name="Body">Body text: a blank line starts a paragraph, a newline breaks a line.</param>
/// <param name="Button">Button label (link kinds only).</param>
/// <param name="Footnote">The security note at the end of the card.</param>
public sealed record EmailTemplateFields(
    [property: JsonPropertyName("subject")] string? Subject = null,
    [property: JsonPropertyName("preheader")] string? Preheader = null,
    [property: JsonPropertyName("heading")] string? Heading = null,
    [property: JsonPropertyName("body")] string? Body = null,
    [property: JsonPropertyName("button")] string? Button = null,
    [property: JsonPropertyName("footnote")] string? Footnote = null);

/// <summary>An instance's email branding. Every field is optional: <see langword="null"/> keeps the default.</summary>
public sealed record EmailBranding
{
    /// <summary>Product name, shown as <c>{{ app_name }}</c> and as the logo's alt text.</summary>
    [JsonPropertyName("brand_name")]
    public string? BrandName { get; init; }

    /// <summary>HTTPS URL of a PNG, JPEG or GIF logo for light backgrounds. Needs <see cref="LogoWidth"/> and <see cref="LogoHeight"/>.</summary>
    [JsonPropertyName("logo_url")]
    public string? LogoUrl { get; init; }

    /// <summary>HTTPS URL of the logo for dark backgrounds; the light logo is used when unset.</summary>
    [JsonPropertyName("logo_dark_url")]
    public string? LogoDarkUrl { get; init; }

    /// <summary>Logo width in CSS pixels, 16 to 400.</summary>
    [JsonPropertyName("logo_width")]
    public int? LogoWidth { get; init; }

    /// <summary>Logo height in CSS pixels, 16 to 120.</summary>
    [JsonPropertyName("logo_height")]
    public int? LogoHeight { get; init; }

    /// <summary>HTTPS URL of a square icon, shown with the brand name when there is no logo.</summary>
    [JsonPropertyName("icon_url")]
    public string? IconUrl { get; init; }

    /// <summary>Button colour, <c>#rrggbb</c>; the button text is black or white, whichever contrasts more.</summary>
    [JsonPropertyName("primary_color")]
    public string? PrimaryColor { get; init; }

    /// <summary>Link and code-box colour, <c>#rrggbb</c>; links are darkened or lightened until they reach 4.5:1.</summary>
    [JsonPropertyName("accent_color")]
    public string? AccentColor { get; init; }

    /// <summary><c>auto</c> (follow the reader's mode), <c>light</c> or <c>dark</c>.</summary>
    [JsonPropertyName("theme")]
    public string? Theme { get; init; }

    /// <summary><c>rounded</c>, <c>pill</c> or <c>square</c>.</summary>
    [JsonPropertyName("button_style")]
    public string? ButtonStyle { get; init; }

    /// <summary>Footer text, for example the company name and postal address; newlines are kept.</summary>
    [JsonPropertyName("footer_text")]
    public string? FooterText { get; init; }

    /// <summary>Support address, shown in the footer and as <c>{{ support_email }}</c>.</summary>
    [JsonPropertyName("support_email")]
    public string? SupportEmail { get; init; }

    /// <summary>HTTPS help page, shown in the footer when there is no support address.</summary>
    [JsonPropertyName("support_url")]
    public string? SupportUrl { get; init; }

    /// <summary>HTTPS website, linked in the footer.</summary>
    [JsonPropertyName("website_url")]
    public string? WebsiteUrl { get; init; }

    /// <summary>Display name of the From address.</summary>
    [JsonPropertyName("sender_name")]
    public string? SenderName { get; init; }

    /// <summary>Reply-To address.</summary>
    [JsonPropertyName("reply_to")]
    public string? ReplyTo { get; init; }
}

/// <summary>Who made a change, as the calling dashboard reports it.</summary>
/// <param name="Id">The dashboard's user id.</param>
/// <param name="Email">The user's email, if known.</param>
public sealed record EmailActor(
    [property: JsonPropertyName("id")] string Id,
    [property: JsonPropertyName("email")] string? Email = null);

/// <summary>One kind and locale's overridden copy.</summary>
/// <param name="Kind">The email kind, e.g. <c>sign_in_code</c>.</param>
/// <param name="Locale"><c>en</c> or <c>pt-BR</c>.</param>
/// <param name="Fields">The overridden fields.</param>
public sealed record EmailTemplateOverride(
    [property: JsonPropertyName("kind")] string Kind,
    [property: JsonPropertyName("locale")] string Locale,
    [property: JsonPropertyName("fields")] EmailTemplateFields Fields);

/// <summary>
/// One version of an instance's email configuration (collections <c>email_configs</c>, the current one, and
/// <c>email_config_versions</c>, every one). Version 0 is the defaults and is never stored.
/// </summary>
/// <param name="InstanceId">The owning instance.</param>
/// <param name="Version">1, 2, 3… in save order.</param>
/// <param name="CreatedAt">When it was saved; <see langword="null"/> for version 0.</param>
/// <param name="Actor">Who saved it.</param>
/// <param name="Change"><c>branding</c>, <c>template:{kind}/{locale}</c> or <c>rollback:{version}</c>.</param>
/// <param name="Branding">The branding.</param>
/// <param name="Templates">The overridden copy.</param>
public sealed record EmailConfig(
    [property: JsonPropertyName("instance_id")] string InstanceId,
    [property: JsonPropertyName("version")] long Version,
    [property: JsonPropertyName("created_at")] DateTimeOffset? CreatedAt,
    [property: JsonPropertyName("actor")] EmailActor? Actor,
    [property: JsonPropertyName("change")] string? Change,
    [property: JsonPropertyName("branding")] EmailBranding Branding,
    [property: JsonPropertyName("templates")] IReadOnlyList<EmailTemplateOverride> Templates)
{
    /// <summary>The defaults: version 0, nothing overridden.</summary>
    public static EmailConfig Defaults(string instanceId) => new(instanceId, 0, null, null, null, new EmailBranding(), []);

    /// <summary>The override of <paramref name="kind"/> in <paramref name="locale"/>, if any.</summary>
    public EmailTemplateFields? Override(string kind, string locale) =>
        this.Templates.FirstOrDefault(entry => entry.Kind == kind && entry.Locale == locale)?.Fields;
}

/// <summary>
/// Stores email configurations in TerraBase. A save checks the version the caller last read and writes the new current
/// configuration and its immutable history entry in one transaction, so concurrent saves have one winner and the
/// history never holds a version that did not become current. Reads are never cached.
/// </summary>
public sealed class EmailSettings(AuthStore store, TimeProvider clock)
{
    /// <summary>The current configuration, one per instance.</summary>
    public const string Collection = "email_configs";

    /// <summary>Every saved version, addressed by instance and version number.</summary>
    public const string VersionCollection = "email_config_versions";

    /// <summary>The current configuration, or the defaults when nothing was saved.</summary>
    public async Task<EmailConfig> GetAsync(string instanceId, CancellationToken cancellationToken = default) =>
        (await this.ReadAsync(instanceId, cancellationToken).ConfigureAwait(false)).Config;

    /// <summary>A saved version, version 0 (the defaults), or <see langword="null"/> when it does not exist.</summary>
    public async Task<EmailConfig?> GetVersionAsync(string instanceId, long version, CancellationToken cancellationToken = default)
    {
        if (version == 0)
        {
            return EmailConfig.Defaults(instanceId);
        }

        var document = await store.GetAsync(VersionCollection, instanceId, VersionKey(version), cancellationToken).ConfigureAwait(false);
        return document is null ? null : Read(document.Document, instanceId);
    }

    /// <summary>The newest <paramref name="limit"/> saved versions, newest first.</summary>
    public async Task<IReadOnlyList<EmailConfig>> ListVersionsAsync(string instanceId, int limit, CancellationToken cancellationToken = default)
    {
        var filter = JsonSerializer.SerializeToElement(new JsonObject { ["eq"] = new JsonArray("/instance_id", instanceId) });
        var items = await store.QueryAsync(VersionCollection, filter, limit, [new TerraBaseSort("/version", "desc")], cancellationToken).ConfigureAwait(false);
        return [.. items.Select(item => Read(item.Document, instanceId)).OrderByDescending(config => config.Version)];
    }

    /// <summary>
    /// Saves <paramref name="update"/> of the current configuration as the next version, if the current version is still
    /// <paramref name="expectedVersion"/>. Returns <see langword="null"/> when another save came first.
    /// </summary>
    public async Task<EmailConfig?> SaveAsync(string instanceId, long expectedVersion, EmailActor actor, string change, Func<EmailConfig, EmailConfig> update, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(update);
        var (current, etag) = await this.ReadAsync(instanceId, cancellationToken).ConfigureAwait(false);
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
        var record = JsonSerializer.SerializeToElement(next, EmailJson.Default.EmailConfig);
        return await store.CommitVersionAsync(Collection, instanceId, instanceId, record, etag, VersionCollection, VersionKey(next.Version), cancellationToken).ConfigureAwait(false)
            ? next
            : null;
    }

    private async Task<(EmailConfig Config, string? Etag)> ReadAsync(string instanceId, CancellationToken cancellationToken)
    {
        var document = await store.GetAsync(Collection, instanceId, instanceId, cancellationToken).ConfigureAwait(false);
        return document is null ? (EmailConfig.Defaults(instanceId), null) : (Read(document.Document, instanceId), document.Version);
    }

    /// <summary>
    /// A stored configuration, held to the same rules as API input, so rendering never meets a value it cannot use. Anything
    /// else is unavailable (503), and a code email then goes out with the defaults.
    /// </summary>
    private static EmailConfig Read(JsonElement document, string instanceId)
    {
        try
        {
            // The address already binds the instance; a mismatch means the record is not what it claims to be.
            if (document.Deserialize(EmailJson.Default.EmailConfig) is { Branding: not null, Templates: not null } config && config.InstanceId == instanceId
                && config.Version > 0 && EmailValidation.Branding(config.Branding) == config.Branding
                && config.Templates.All(entry => entry?.Fields is not null && AuthEmails.Kinds.TryGetValue(entry.Kind ?? string.Empty, out var kind)
                    && AuthEmails.Locales.Contains(entry.Locale) && EmailValidation.Fields(kind, entry.Fields) == entry.Fields)
                && config.Templates.DistinctBy(entry => (entry.Kind, entry.Locale)).Count() == config.Templates.Count)
            {
                return config;
            }
        }
        catch (Exception error) when (error is JsonException or ApiException { Status: StatusCodes.Status422UnprocessableEntity })
        {
            throw ApiException.Unavailable(new InvalidDataException("A stored email configuration is malformed.", error));
        }

        throw ApiException.Unavailable(new InvalidDataException("A stored email configuration is malformed."));
    }

    private static string VersionKey(long version) => version.ToString(System.Globalization.CultureInfo.InvariantCulture);
}

/// <summary>
/// Checks branding and copy from the API. Failures are 422 <c>invalid_request</c> naming the field. Copy takes the
/// kind's <c>{{ name }}</c> placeholders and nothing else, so a template has nothing to execute.
/// </summary>
public static partial class EmailValidation
{
    /// <summary>Longest field values, in characters.</summary>
    public static readonly IReadOnlyDictionary<string, int> FieldLimits = new Dictionary<string, int>(StringComparer.Ordinal)
    {
        ["subject"] = 200,
        ["preheader"] = 200,
        ["heading"] = 200,
        ["body"] = 2000,
        ["button"] = 60,
        ["footnote"] = 500,
    };

    /// <summary>Returns <paramref name="branding"/> with blank values removed, or throws 422.</summary>
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

    /// <summary>Returns <paramref name="fields"/> with blank values removed, or throws 422.</summary>
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

    /// <summary>The caller's actor, or throws 422.</summary>
    public static EmailActor Actor(EmailActor? actor) =>
        actor is null || Text("actor.id", actor.Id, 128, multiline: false) is not { } id
            ? throw Invalid("actor.id is required.")
            : new EmailActor(id, Address("actor.email", actor.Email));

    /// <summary>A recipient or support address: one plain address, at most 254 characters.</summary>
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
[JsonSerializable(typeof(EmailConfig))]
internal sealed partial class EmailJson : JsonSerializerContext;
