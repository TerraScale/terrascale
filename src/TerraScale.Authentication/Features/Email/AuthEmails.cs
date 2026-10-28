using System.Globalization;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.RegularExpressions;
using System.Text.Unicode;
using Microsoft.Extensions.Primitives;
using Microsoft.Net.Http.Headers;
using TerraScale.Authentication.Configuration;

namespace TerraScale.Authentication.Features.Email;

/// <summary>The transactional emails ts-auth sends.</summary>
public enum AuthEmailKind
{
    /// <summary>A one-time code that signs the user in.</summary>
    SignInCode,

    /// <summary>A one-time link that signs the user in.</summary>
    MagicLink,

    /// <summary>A one-time code that confirms the user owns the address.</summary>
    VerifyEmail,

    /// <summary>A one-time code that lets the user choose a new password.</summary>
    PasswordReset,

    /// <summary>A link that accepts an organization invitation.</summary>
    Invitation,

    /// <summary>A one-time code for the second sign-in factor.</summary>
    MfaCode,

    /// <summary>An alert about a sign-in from a device not seen before.</summary>
    NewDevice,

    /// <summary>An alert about a temporary pause of an authentication method after excessive attempts.</summary>
    Lockout,

    /// <summary>A one-time code, sent to the new address, that confirms a sign-in email change.</summary>
    EmailChangeCode,

    /// <summary>An alert, sent to the current address, about a pending sign-in email change with a link that cancels it.</summary>
    EmailChangeNotice,
}

/// <summary>
/// The values one email shows. Each kind's copy uses only some of them: a code for the code kinds, a link for
/// <see cref="AuthEmailKind.MagicLink"/> and <see cref="AuthEmailKind.Invitation"/>, the device details for
/// <see cref="AuthEmailKind.NewDevice"/>, the pause deadline for <see cref="AuthEmailKind.Lockout"/>.
/// </summary>
/// <param name="Email">The recipient's address.</param>
public sealed record AuthEmailValues(string Email)
{
    /// <summary>Server-resolved organization identity for mail accounting.</summary>
    public string Tenant { get; init; } = "platform";

    /// <summary>The one-time code, shown large.</summary>
    public string? Code { get; init; }

    /// <summary>The action link behind the button.</summary>
    public Uri? Link { get; init; }

    /// <summary>How long the code, link or invitation keeps working.</summary>
    public TimeSpan? ExpiresIn { get; init; }

    /// <summary>The organization an invitation joins.</summary>
    public string? Organization { get; init; }

    /// <summary>Who sent an invitation.</summary>
    public string? Inviter { get; init; }

    /// <summary>The new device, e.g. a browser and operating system.</summary>
    public string? Device { get; init; }

    /// <summary>Where the new device signed in from.</summary>
    public string? Location { get; init; }

    /// <summary>When the new device signed in.</summary>
    public DateTimeOffset? Time { get; init; }

    /// <summary>When the authentication method's temporary pause ends, rendered in UTC.</summary>
    public DateTimeOffset? LockoutUntil { get; init; }

    /// <summary>The requested sign-in address of an email change notice.</summary>
    public string? NewEmail { get; init; }

    /// <summary>Omits every value, so formatting or logging the record never discloses a code or a link.</summary>
    public override string ToString() => nameof(AuthEmailValues);
}

/// <summary>How dark mode applies to one rendering.</summary>
public enum EmailDarkMode
{
    /// <summary>Never: the email stays light.</summary>
    None,

    /// <summary>When the reader's client is in dark mode.</summary>
    Media,

    /// <summary>Always.</summary>
    Always,
}

/// <summary>
/// Who an email comes from and how it looks: branding merged over the defaults, with the colours the layout derives from
/// it. Every colour is <c>#rrggbb</c>, so it is safe in CSS.
/// </summary>
/// <param name="Name">The product name, shown as <c>{{ app_name }}</c> and as the logo's alt text.</param>
/// <param name="LogoUrl">The logo for light backgrounds, or <see langword="null"/> to show the name.</param>
/// <param name="LogoDarkUrl">The logo for dark backgrounds.</param>
/// <param name="LogoWidth">Logo width in CSS pixels; Outlook needs it.</param>
/// <param name="LogoHeight">Logo height in CSS pixels.</param>
/// <param name="IconUrl">A square icon shown with the name when there is no logo.</param>
/// <param name="PrimaryColor">Button colour.</param>
/// <param name="AccentColor">Link and code-box colour.</param>
/// <param name="Theme"><c>auto</c>, <c>light</c> or <c>dark</c>.</param>
/// <param name="ButtonStyle"><c>rounded</c>, <c>pill</c> or <c>square</c>.</param>
/// <param name="FooterText">Footer lines, e.g. a postal address.</param>
/// <param name="SupportEmail">Support address.</param>
/// <param name="SupportUrl">Help page.</param>
/// <param name="WebsiteUrl">Website linked in the footer.</param>
/// <param name="SenderName">Display name of the From address.</param>
/// <param name="ReplyTo">Reply-To address.</param>
public sealed record EmailBrand(
    string Name,
    Uri? LogoUrl,
    Uri? LogoDarkUrl,
    int LogoWidth,
    int LogoHeight,
    Uri? IconUrl,
    string PrimaryColor,
    string AccentColor,
    string Theme,
    string ButtonStyle,
    string? FooterText,
    string? SupportEmail,
    Uri? SupportUrl,
    Uri? WebsiteUrl,
    string? SenderName,
    string? ReplyTo)
{
    private const string Card = "#ffffff";
    private const string DarkCard = "#11141c";

    /// <summary>Button text: black or white, whichever contrasts more with the button; one of them always reaches 4.5:1.</summary>
    public string ButtonTextColor => Contrast(this.PrimaryColor, "#ffffff") >= Contrast(this.PrimaryColor, "#000000") ? "#ffffff" : "#000000";

    /// <summary>Button corner radius.</summary>
    public string ButtonRadius => this.ButtonStyle switch { "pill" => "999px", "square" => "0", _ => "8px" };

    /// <summary>Links on the light card: the accent, darkened until it reaches 4.5:1.</summary>
    public string LinkColor => Readable(this.AccentColor, Card, "#000000");

    /// <summary>Links on the dark card: the accent, lightened until it reaches 4.5:1.</summary>
    public string LinkDarkColor => Readable(this.AccentColor, DarkCard, "#ffffff");

    /// <summary>Code box fill and border, tinted with the accent.</summary>
    public (string Fill, string Border) CodeBox => (Mix(this.AccentColor, Card, 0.08), Mix(this.AccentColor, Card, 0.25));

    /// <summary>The same in dark mode.</summary>
    public (string Fill, string Border) CodeBoxDark => (Mix(this.AccentColor, DarkCard, 0.12), Mix(this.AccentColor, DarkCard, 0.35));

    /// <summary>
    /// Merges <paramref name="branding"/> over <paramref name="defaults"/>. A custom logo replaces both default logos; a
    /// custom brand name or icon without a logo drops them, and a custom brand name drops the default website (another
    /// brand's logo or site would be wrong) and replaces the default sender name.
    /// </summary>
    public static EmailBrand Merge(EmailBrand defaults, EmailBranding branding)
    {
        ArgumentNullException.ThrowIfNull(defaults);
        ArgumentNullException.ThrowIfNull(branding);
        var custom = branding.LogoUrl is { } url ? new Uri(url) : null;
        var keepDefault = custom is null && branding.BrandName is null && branding.IconUrl is null;
        var name = branding.BrandName ?? defaults.Name;
        return new EmailBrand(
            name,
            custom ?? (keepDefault ? defaults.LogoUrl : null),
            custom is null ? (keepDefault ? defaults.LogoDarkUrl : null) : branding.LogoDarkUrl is { } dark ? new Uri(dark) : custom,
            custom is null ? defaults.LogoWidth : branding.LogoWidth ?? defaults.LogoWidth,
            custom is null ? defaults.LogoHeight : branding.LogoHeight ?? defaults.LogoHeight,
            branding.IconUrl is { } icon ? new Uri(icon) : defaults.IconUrl,
            branding.PrimaryColor ?? defaults.PrimaryColor,
            branding.AccentColor ?? branding.PrimaryColor ?? defaults.AccentColor,
            branding.Theme ?? defaults.Theme,
            branding.ButtonStyle ?? defaults.ButtonStyle,
            branding.FooterText ?? defaults.FooterText,
            branding.SupportEmail ?? defaults.SupportEmail,
            branding.SupportUrl is { } support ? new Uri(support) : defaults.SupportUrl,
            branding.WebsiteUrl is { } website ? new Uri(website) : branding.BrandName is null ? defaults.WebsiteUrl : null,
            branding.SenderName ?? (branding.BrandName is null ? defaults.SenderName : name),
            branding.ReplyTo ?? defaults.ReplyTo);
    }

    /// <summary>The branding fields these values correspond to, for showing the defaults.</summary>
    public EmailBranding ToBranding() => new()
    {
        BrandName = this.Name,
        LogoUrl = this.LogoUrl?.AbsoluteUri,
        LogoDarkUrl = this.LogoDarkUrl?.AbsoluteUri,
        LogoWidth = this.LogoUrl is null ? null : this.LogoWidth,
        LogoHeight = this.LogoUrl is null ? null : this.LogoHeight,
        IconUrl = this.IconUrl?.AbsoluteUri,
        PrimaryColor = this.PrimaryColor,
        AccentColor = this.AccentColor,
        Theme = this.Theme,
        ButtonStyle = this.ButtonStyle,
        FooterText = this.FooterText,
        SupportEmail = this.SupportEmail,
        SupportUrl = this.SupportUrl?.AbsoluteUri,
        WebsiteUrl = this.WebsiteUrl?.AbsoluteUri,
        SenderName = this.SenderName,
        ReplyTo = this.ReplyTo,
    };

    private static string Readable(string color, string background, string toward)
    {
        for (var step = 0; step <= 10; step++)
        {
            var candidate = Mix(toward, color, step / 10.0);
            if (Contrast(candidate, background) >= 4.5)
            {
                return candidate;
            }
        }

        return toward;
    }

    private static string Mix(string color, string other, double amount)
    {
        var (a, b) = (Rgb(color), Rgb(other));
        return string.Create(CultureInfo.InvariantCulture,
            $"#{(int)Math.Round(a.R * amount + b.R * (1 - amount)):x2}{(int)Math.Round(a.G * amount + b.G * (1 - amount)):x2}{(int)Math.Round(a.B * amount + b.B * (1 - amount)):x2}");
    }

    private static double Contrast(string one, string two)
    {
        var (a, b) = (Luminance(one), Luminance(two));
        return (Math.Max(a, b) + 0.05) / (Math.Min(a, b) + 0.05);
    }

    // WCAG 2 relative luminance.
    private static double Luminance(string color)
    {
        static double Channel(int value) => value / 255.0 <= 0.03928 ? value / 255.0 / 12.92 : Math.Pow((value / 255.0 + 0.055) / 1.055, 2.4);
        var (r, g, b) = Rgb(color);
        return 0.2126 * Channel(r) + 0.7152 * Channel(g) + 0.0722 * Channel(b);
    }

    private static (int R, int G, int B) Rgb(string color) =>
        (Convert.ToInt32(color[1..3], 16), Convert.ToInt32(color[3..5], 16), Convert.ToInt32(color[5..7], 16));
}

/// <summary>
/// Renders every ts-auth email as a responsive HTML message with a plain-text alternative, in English or Brazilian
/// Portuguese, with an instance's branding and copy overrides. The HTML follows the reader's light or dark mode, and
/// works without images or styles. The copy takes <c>{{ name }}</c> placeholders and nothing else: there is no logic to
/// run, and every value is HTML-encoded by the layout.
/// </summary>
public sealed partial class AuthEmails(AuthOptions options)
{
    /// <summary>English, the fallback locale.</summary>
    public const string English = "en";

    /// <summary>Brazilian Portuguese.</summary>
    public const string Portuguese = "pt-BR";

    /// <summary>Every supported locale.</summary>
    public static readonly IReadOnlyList<string> Locales = [English, Portuguese];

    /// <summary>The API name of each kind.</summary>
    public static readonly IReadOnlyDictionary<string, AuthEmailKind> Kinds = new Dictionary<string, AuthEmailKind>(StringComparer.Ordinal)
    {
        ["sign_in_code"] = AuthEmailKind.SignInCode,
        ["magic_link"] = AuthEmailKind.MagicLink,
        ["verify_email"] = AuthEmailKind.VerifyEmail,
        ["password_reset"] = AuthEmailKind.PasswordReset,
        ["invitation"] = AuthEmailKind.Invitation,
        ["mfa_code"] = AuthEmailKind.MfaCode,
        ["new_device"] = AuthEmailKind.NewDevice,
        ["lockout"] = AuthEmailKind.Lockout,
        ["email_change_code"] = AuthEmailKind.EmailChangeCode,
        ["email_change_notice"] = AuthEmailKind.EmailChangeNotice,
    };

    // Keeps accented letters readable in the HTML part; markup characters are still encoded.
    private static readonly HtmlEncoder Encoder = HtmlEncoder.Create(UnicodeRanges.All);

    /// <summary>TerraScale's own brand, served from this deployment's public origin.</summary>
    public EmailBrand Brand { get; } = new(
        "TerraScale",
        new Uri(options.PublicOrigin, "/admin/email-logo.png"),
        new Uri(options.PublicOrigin, "/admin/email-logo-dark.png"),
        LogoWidth: 150,
        LogoHeight: 36,
        IconUrl: null,
        PrimaryColor: "#2158e8",
        AccentColor: "#2158e8",
        Theme: "auto",
        ButtonStyle: "rounded",
        FooterText: null,
        SupportEmail: null,
        SupportUrl: null,
        new Uri("https://terrascale.tech"),
        SenderName: "TerraScale",
        ReplyTo: null);

    /// <summary>The API name of <paramref name="kind"/>.</summary>
    public static string Name(AuthEmailKind kind) => Kinds.First(entry => entry.Value == kind).Key;

    /// <summary>The placeholders <paramref name="kind"/>'s copy may use.</summary>
    public static IReadOnlyList<string> Variables(AuthEmailKind kind) =>
    [
        "app_name", "user.email", "support_email",
        .. kind switch
        {
            AuthEmailKind.MagicLink => ["link", "expires_in"],
            AuthEmailKind.Invitation => ["link", "expires_in", "organization", "inviter"],
            AuthEmailKind.NewDevice => ["device", "location", "time"],
            AuthEmailKind.Lockout => ["locked_until"],
            AuthEmailKind.EmailChangeCode => ["code", "expires_in", "new_email"],
            AuthEmailKind.EmailChangeNotice => ["link", "expires_in", "new_email"],
            _ => (string[])["code", "expires_in"],
        },
    ];

    /// <summary>The default copy of <paramref name="kind"/> in <paramref name="locale"/>.</summary>
    public static EmailTemplateFields Defaults(AuthEmailKind kind, string locale) => Copies[(kind, locale)];

    /// <summary>
    /// The locale for a request's <c>Accept-Language</c>: <see cref="Portuguese"/> when Portuguese is preferred over
    /// English, otherwise <see cref="English"/>.
    /// </summary>
    public static string Locale(StringValues acceptLanguage)
    {
        if (!StringWithQualityHeaderValue.TryParseList(acceptLanguage, out var ranges))
        {
            return English;
        }

        // Stable: equal weights keep the browser's order.
        foreach (var range in ranges.OrderByDescending(range => range.Quality ?? 1))
        {
            var tag = range.Value.Value ?? string.Empty;
            if (range.Quality == 0)
            {
                break;
            }

            if (tag.Equals("pt", StringComparison.OrdinalIgnoreCase) || tag.StartsWith("pt-", StringComparison.OrdinalIgnoreCase))
            {
                return Portuguese;
            }

            if (tag is "*" || tag.Equals("en", StringComparison.OrdinalIgnoreCase) || tag.StartsWith("en-", StringComparison.OrdinalIgnoreCase))
            {
                return English;
            }
        }

        return English;
    }

    /// <summary>
    /// Renders <paramref name="kind"/> for <paramref name="values"/> with <paramref name="config"/>'s branding and copy
    /// (the defaults when <see langword="null"/>); an unknown locale gets English. <paramref name="colorScheme"/>
    /// <c>light</c> or <c>dark</c> pins an <c>auto</c> theme for previews.
    /// </summary>
    public async Task<EmailMessage> RenderAsync(AuthEmailKind kind, string locale, AuthEmailValues values, EmailConfig? config = null, string? colorScheme = null, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(values);
        if (!Chromes.TryGetValue(locale, out var chrome))
        {
            (locale, chrome) = (English, Chromes[English]);
        }

        var brand = config is null ? this.Brand : EmailBrand.Merge(this.Brand, config.Branding);
        var defaults = Copies[(kind, locale)];
        var custom = config?.Override(Name(kind), locale);
        var copy = custom is null ? defaults : new EmailTemplateFields(
            custom.Subject ?? defaults.Subject, custom.Preheader ?? defaults.Preheader, custom.Heading ?? defaults.Heading,
            custom.Body ?? defaults.Body, custom.Button ?? defaults.Button, custom.Footnote ?? defaults.Footnote);
        var variables = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["app_name"] = brand.Name,
            ["user.email"] = values.Email,
            ["support_email"] = brand.SupportEmail ?? string.Empty,
            ["code"] = values.Code ?? string.Empty,
            ["link"] = values.Link?.AbsoluteUri ?? string.Empty,
            ["expires_in"] = values.ExpiresIn is { } lifetime ? chrome.Duration(lifetime) : string.Empty,
            ["organization"] = values.Organization ?? string.Empty,
            ["inviter"] = values.Inviter ?? string.Empty,
            ["device"] = values.Device ?? string.Empty,
            ["location"] = values.Location ?? string.Empty,
            ["time"] = values.Time?.UtcDateTime.ToString("yyyy-MM-dd HH:mm 'UTC'", CultureInfo.InvariantCulture) ?? string.Empty,
            ["new_email"] = values.NewEmail ?? string.Empty,
            ["locked_until"] = values.LockoutUntil?.UtcDateTime.ToString("yyyy-MM-dd HH:mm:ss 'UTC'", CultureInfo.InvariantCulture) ?? string.Empty,
        };
        string Fill(string? template) => Placeholder().Replace(template ?? string.Empty, match => variables.GetValueOrDefault(match.Groups["name"].Value, string.Empty));

        var expiry = values.ExpiresIn is null ? null : Fill(kind switch
        {
            AuthEmailKind.MagicLink or AuthEmailKind.EmailChangeNotice => chrome.LinkExpiry,
            AuthEmailKind.Invitation => chrome.InvitationExpiry,
            AuthEmailKind.NewDevice or AuthEmailKind.Lockout => string.Empty,
            _ => chrome.CodeExpiry,
        });
        var view = new AuthEmailView(
            locale,
            Fill(copy.Subject),
            Fill(copy.Preheader),
            Fill(copy.Heading),
            [.. Fill(copy.Body).Split("\n\n").Select(paragraph => paragraph.Split('\n'))],
            kind == AuthEmailKind.Lockout ? null : values.Code,
            string.IsNullOrEmpty(expiry) ? null : expiry,
            kind == AuthEmailKind.Lockout || values.Link is null || copy.Button is null ? null : Fill(copy.Button),
            kind == AuthEmailKind.Lockout ? null : values.Link?.AbsoluteUri,
            Fill(chrome.LinkFallback),
            Fill(copy.Footnote),
            Fill(chrome.SentTo),
            brand.FooterText?.Split('\n') ?? [],
            brand.SupportEmail is not null || brand.SupportUrl is not null ? chrome.Support : null,
            brand,
            brand.Theme switch
            {
                "light" => EmailDarkMode.None,
                "dark" => EmailDarkMode.Always,
                _ => colorScheme switch { "light" => EmailDarkMode.None, "dark" => EmailDarkMode.Always, _ => EmailDarkMode.Media },
            });

        await using var html = new StringWriter(CultureInfo.InvariantCulture);
        await AuthEmailLayout.Create(view).RenderAsync(html, Encoder, cancellationToken).ConfigureAwait(false);
        return new EmailMessage(view.Subject, Text(view), html.ToString())
        {
            FromName = brand.SenderName,
            ReplyTo = brand.ReplyTo,
            Tenant = values.Tenant,
            Purpose = kind is AuthEmailKind.SignInCode or AuthEmailKind.MagicLink or AuthEmailKind.VerifyEmail or AuthEmailKind.PasswordReset or AuthEmailKind.MfaCode
                ? EmailPurpose.Authentication : kind == AuthEmailKind.Invitation ? EmailPurpose.Invitation : EmailPurpose.Notification
        };
    }

    /// <summary>The plain-text alternative: the same content in reading order.</summary>
    private static string Text(AuthEmailView view)
    {
        var text = new StringBuilder();
        text.Append(view.Heading).Append("\n\n");
        foreach (var paragraph in view.Paragraphs)
        {
            text.AppendJoin('\n', paragraph).Append("\n\n");
        }

        if (view.Code is { } code)
        {
            text.Append("    ").Append(code).Append("\n\n");
        }

        if (view.Button is { } button && view.Link is { } link)
        {
            text.Append(button).Append(":\n").Append(link).Append("\n\n");
        }

        if (view.Expiry is { } expiry)
        {
            text.Append(expiry).Append("\n\n");
        }

        text.Append(view.Footnote).Append("\n\n-- \n").Append(view.SentTo).Append('\n');
        foreach (var line in view.FooterLines)
        {
            text.Append(line).Append('\n');
        }

        if (view.Support is { } support)
        {
            text.Append(support).Append(' ').Append(view.Brand.SupportEmail ?? view.Brand.SupportUrl!.AbsoluteUri).Append('\n');
        }

        text.Append(view.Brand.Name);
        if (view.Brand.WebsiteUrl is { } website)
        {
            text.Append(" · ").Append(website.Host);
        }

        return text.Append('\n').ToString();
    }

    [GeneratedRegex(@"\{\{\s*(?<name>[a-z_]+(?:\.[a-z_]+)?)\s*\}\}")]
    private static partial Regex Placeholder();

    /// <summary>The layout's own lines in one locale.</summary>
    private sealed record Chrome(string CodeExpiry, string LinkExpiry, string InvitationExpiry, string LinkFallback, string SentTo, string Support, Func<TimeSpan, string> Duration);

    private static readonly Dictionary<string, Chrome> Chromes = new(StringComparer.Ordinal)
    {
        [English] = new(
            "This code expires in {{ expires_in }} and works only once.",
            "This link expires in {{ expires_in }} and works only once.",
            "This invitation expires in {{ expires_in }}.",
            "Button not working? Paste this link into your browser:",
            "This email was sent to {{ user.email }}.",
            "Need help?",
            lifetime => Duration(lifetime, ("minute", "minutes"), ("hour", "hours"), ("day", "days"))),
        [Portuguese] = new(
            "Este código expira em {{ expires_in }} e só pode ser usado uma vez.",
            "Este link expira em {{ expires_in }} e só pode ser usado uma vez.",
            "Este convite expira em {{ expires_in }}.",
            "O botão não funciona? Cole este link no seu navegador:",
            "Este e-mail foi enviado para {{ user.email }}.",
            "Precisa de ajuda?",
            lifetime => Duration(lifetime, ("minuto", "minutos"), ("hora", "horas"), ("dia", "dias"))),
    };

    private static string Duration(TimeSpan lifetime, (string One, string Many) minutes, (string One, string Many) hours, (string One, string Many) days)
    {
        var (count, unit) = lifetime.TotalDays >= 2 && lifetime.TotalDays % 1 == 0 ? ((int)lifetime.TotalDays, days)
            : lifetime.TotalHours >= 1 && lifetime.TotalHours % 1 == 0 ? ((int)lifetime.TotalHours, hours)
            : ((int)Math.Ceiling(lifetime.TotalMinutes), minutes);
        return string.Create(CultureInfo.InvariantCulture, $"{count} {(count == 1 ? unit.One : unit.Many)}");
    }

    private static readonly Dictionary<(AuthEmailKind, string), EmailTemplateFields> Copies = new()
    {
        [(AuthEmailKind.SignInCode, English)] = new(
            "{{ code }} is your {{ app_name }} sign-in code",
            "Use it to sign in. It expires in {{ expires_in }}.",
            "Your sign-in code",
            "Enter this code to sign in to {{ app_name }} as {{ user.email }}.",
            null,
            "Didn't try to sign in? You can safely ignore this email. Never share this code with anyone."),
        [(AuthEmailKind.SignInCode, Portuguese)] = new(
            "{{ code }} é o seu código de acesso para {{ app_name }}",
            "Use-o para entrar. Ele expira em {{ expires_in }}.",
            "Seu código de acesso",
            "Digite este código para entrar em {{ app_name }} como {{ user.email }}.",
            null,
            "Não tentou entrar? Pode ignorar este e-mail com segurança. Nunca compartilhe este código com ninguém."),
        [(AuthEmailKind.MagicLink, English)] = new(
            "Sign in to {{ app_name }}",
            "Your sign-in link expires in {{ expires_in }}.",
            "Sign in to {{ app_name }}",
            "Click the button below to sign in as {{ user.email }}.",
            "Sign in",
            "Didn't try to sign in? You can safely ignore this email. Never forward this link to anyone."),
        [(AuthEmailKind.MagicLink, Portuguese)] = new(
            "Seu link de acesso para {{ app_name }}",
            "Seu link de acesso expira em {{ expires_in }}.",
            "Entre em {{ app_name }}",
            "Clique no botão abaixo para entrar como {{ user.email }}.",
            "Entrar",
            "Não tentou entrar? Pode ignorar este e-mail com segurança. Nunca encaminhe este link para ninguém."),
        [(AuthEmailKind.VerifyEmail, English)] = new(
            "{{ code }} is your {{ app_name }} verification code",
            "Confirm your email address to finish setting up your account.",
            "Verify your email address",
            "Enter this code in {{ app_name }} to confirm that {{ user.email }} belongs to you.",
            null,
            "Didn't sign up for {{ app_name }}? You can safely ignore this email."),
        [(AuthEmailKind.VerifyEmail, Portuguese)] = new(
            "{{ code }} é o seu código de verificação para {{ app_name }}",
            "Confirme seu endereço de e-mail para concluir o cadastro.",
            "Confirme seu e-mail",
            "Digite este código em {{ app_name }} para confirmar que {{ user.email }} pertence a você.",
            null,
            "Não se cadastrou em {{ app_name }}? Pode ignorar este e-mail com segurança."),
        [(AuthEmailKind.PasswordReset, English)] = new(
            "{{ code }} is your {{ app_name }} password reset code",
            "Use this code to choose a new password.",
            "Reset your password",
            "We received a request to reset the password for {{ user.email }}. Enter this code in {{ app_name }} to choose a new one.",
            null,
            "Didn't ask to reset your password? You can ignore this email; your password stays the same. Never share this code with anyone."),
        [(AuthEmailKind.PasswordReset, Portuguese)] = new(
            "{{ code }} é o seu código para redefinir a senha de {{ app_name }}",
            "Use este código para escolher uma nova senha.",
            "Redefina sua senha",
            "Recebemos um pedido para redefinir a senha de {{ user.email }}. Digite este código em {{ app_name }} para escolher uma nova.",
            null,
            "Não pediu para redefinir sua senha? Pode ignorar este e-mail; sua senha continua a mesma. Nunca compartilhe este código com ninguém."),
        [(AuthEmailKind.Invitation, English)] = new(
            "{{ inviter }} invited you to {{ organization }} on {{ app_name }}",
            "Accept the invitation to get started.",
            "Join {{ organization }}",
            "{{ inviter }} invited {{ user.email }} to join {{ organization }} on {{ app_name }}.",
            "Accept invitation",
            "Not expecting this invitation? You can safely ignore this email."),
        [(AuthEmailKind.Invitation, Portuguese)] = new(
            "{{ inviter }} convidou você para {{ organization }} em {{ app_name }}",
            "Aceite o convite para começar.",
            "Participe de {{ organization }}",
            "{{ inviter }} convidou {{ user.email }} para participar de {{ organization }} em {{ app_name }}.",
            "Aceitar convite",
            "Não esperava este convite? Pode ignorar este e-mail com segurança."),
        [(AuthEmailKind.MfaCode, English)] = new(
            "{{ code }} is your {{ app_name }} security code",
            "Use it to finish signing in. It expires in {{ expires_in }}.",
            "Confirm it's you",
            "Enter this code to finish signing in to {{ app_name }} as {{ user.email }}.",
            null,
            "Didn't try to sign in? Someone may know your password: change it now. Never share this code with anyone."),
        [(AuthEmailKind.MfaCode, Portuguese)] = new(
            "{{ code }} é o seu código de segurança para {{ app_name }}",
            "Use-o para terminar de entrar. Ele expira em {{ expires_in }}.",
            "Confirme que é você",
            "Digite este código para terminar de entrar em {{ app_name }} como {{ user.email }}.",
            null,
            "Não tentou entrar? Alguém pode saber sua senha: troque-a agora. Nunca compartilhe este código com ninguém."),
        [(AuthEmailKind.NewDevice, English)] = new(
            "New sign-in to {{ app_name }}",
            "Your account was used on a new device.",
            "New sign-in to your account",
            "{{ user.email }} just signed in to {{ app_name }} on a new device.\n\nDevice: {{ device }}\nLocation: {{ location }}\nTime: {{ time }}",
            null,
            "If this was you, there is nothing to do. If not, reset your password and sign out of your other sessions now."),
        [(AuthEmailKind.NewDevice, Portuguese)] = new(
            "Novo acesso à sua conta {{ app_name }}",
            "Sua conta foi usada em um novo dispositivo.",
            "Novo acesso à sua conta",
            "{{ user.email }} acabou de entrar em {{ app_name }} em um novo dispositivo.\n\nDispositivo: {{ device }}\nLocal: {{ location }}\nHorário: {{ time }}",
            null,
            "Se foi você, não é preciso fazer nada. Se não foi, redefina sua senha e encerre suas outras sessões agora."),
        [(AuthEmailKind.Lockout, English)] = new(
            "Temporary authentication pause for {{ app_name }}",
            "An authentication method is paused until {{ locked_until }}.",
            "An authentication method is temporarily paused",
            "Too many attempts temporarily paused an authentication method for {{ user.email }} on {{ app_name }} until {{ locked_until }}.\n\nOther sign-in or recovery methods may still be available.",
            null,
            "If you didn't make these attempts, use an available method to review your account security or contact support. If password reset is paused, try it again after the pause ends."),
        [(AuthEmailKind.Lockout, Portuguese)] = new(
            "Pausa temporária de autenticação em {{ app_name }}",
            "Um método de autenticação está pausado até {{ locked_until }}.",
            "Um método de autenticação está temporariamente pausado",
            "Muitas tentativas pausaram temporariamente um método de autenticação para {{ user.email }} em {{ app_name }} até {{ locked_until }}.\n\nOutros métodos de acesso ou recuperação podem continuar disponíveis.",
            null,
            "Se não foi você, use um método disponível para revisar a segurança da sua conta ou entre em contato com o suporte. Se a redefinição de senha estiver pausada, tente novamente após o fim da pausa."),
        [(AuthEmailKind.EmailChangeCode, English)] = new(
            "{{ code }} confirms your new {{ app_name }} sign-in email",
            "Confirm {{ new_email }} as your new sign-in email.",
            "Confirm your new sign-in email",
            "Enter this code in {{ app_name }} to make {{ new_email }} your sign-in email.",
            null,
            "Didn't ask for this? You can safely ignore this email; nothing changes without the code. Never share this code with anyone."),
        [(AuthEmailKind.EmailChangeCode, Portuguese)] = new(
            "{{ code }} confirma seu novo e-mail de acesso a {{ app_name }}",
            "Confirme {{ new_email }} como seu novo e-mail de acesso.",
            "Confirme seu novo e-mail de acesso",
            "Digite este código em {{ app_name }} para tornar {{ new_email }} seu e-mail de acesso.",
            null,
            "Não pediu isso? Pode ignorar este e-mail com segurança; nada muda sem o código. Nunca compartilhe este código com ninguém."),
        [(AuthEmailKind.EmailChangeNotice, English)] = new(
            "Your {{ app_name }} sign-in email is changing",
            "A change of your sign-in email to {{ new_email }} was requested.",
            "Your sign-in email is changing",
            "Someone who just verified your credentials asked to change the sign-in email of {{ user.email }} on {{ app_name }} to {{ new_email }}. It changes once that address confirms its code.",
            "This wasn't me",
            "If this wasn't you, use the button to cancel the change and sign out everywhere, then reset your password."),
        [(AuthEmailKind.EmailChangeNotice, Portuguese)] = new(
            "Seu e-mail de acesso a {{ app_name }} vai mudar",
            "Foi pedida a troca do seu e-mail de acesso para {{ new_email }}.",
            "Seu e-mail de acesso vai mudar",
            "Alguém que acabou de verificar suas credenciais pediu para trocar o e-mail de acesso de {{ user.email }} em {{ app_name }} para {{ new_email }}. A troca acontece quando esse endereço confirmar o código.",
            "Não fui eu",
            "Se não foi você, use o botão para cancelar a troca e encerrar todas as sessões, depois redefina sua senha."),
    };
}

/// <summary>Model for <c>AuthEmailLayout.cshtml</c>: rendered text only, which Razor encodes.</summary>
/// <param name="Lang">The BCP 47 language of the copy.</param>
/// <param name="Subject">The subject, also the document title.</param>
/// <param name="Preheader">The inbox preview line.</param>
/// <param name="Heading">The heading.</param>
/// <param name="Paragraphs">Body paragraphs, each a list of lines.</param>
/// <param name="Code">The one-time code, shown large.</param>
/// <param name="Expiry">When the code or link stops working.</param>
/// <param name="Button">The button label; shown only with <paramref name="Link"/>.</param>
/// <param name="Link">The action link.</param>
/// <param name="LinkFallback">The line above the link written out in full.</param>
/// <param name="Footnote">The security note at the end of the card.</param>
/// <param name="SentTo">Who the email was sent to.</param>
/// <param name="FooterLines">The brand's footer text, line by line.</param>
/// <param name="Support">The label before the support address or page, when the brand has one.</param>
/// <param name="Brand">The sender's brand.</param>
/// <param name="Dark">When the dark styles apply.</param>
public sealed record AuthEmailView(
    string Lang,
    string Subject,
    string Preheader,
    string Heading,
    IReadOnlyList<IReadOnlyList<string>> Paragraphs,
    string? Code,
    string? Expiry,
    string? Button,
    string? Link,
    string LinkFallback,
    string Footnote,
    string SentTo,
    IReadOnlyList<string> FooterLines,
    string? Support,
    EmailBrand Brand,
    EmailDarkMode Dark);
