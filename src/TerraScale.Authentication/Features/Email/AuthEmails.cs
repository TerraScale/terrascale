using System.Globalization;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.RegularExpressions;
using System.Text.Unicode;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Primitives;
using Microsoft.Net.Http.Headers;
using TerraScale.Authentication.Configuration;
using TerraScale.Authentication.Features.Oidc;
using TerraScale.Authentication.Features.Platform;

namespace TerraScale.Authentication.Features.Email;

public enum AuthEmailKind
{
    SignInCode,

    MagicLink,

    VerifyEmail,

    PasswordReset,

    Invitation,

    MfaCode,

    StepUpCode,

    NewDevice,

    Lockout,

    EmailChangeCode,

    EmailChangeNotice,

    SocialLinkCode,
}

public sealed record AuthEmailValues(string Email)
{
    public string Tenant { get; init; } = "platform";

    public string? Code { get; init; }

    public Uri? Link { get; init; }

    public TimeSpan? ExpiresIn { get; init; }

    public string? Organization { get; init; }

    public string? Inviter { get; init; }

    public string? AdmissionId { get; init; }

    public string? Device { get; init; }

    public string? Location { get; init; }

    public DateTimeOffset? Time { get; init; }

    public DateTimeOffset? LockoutUntil { get; init; }

    public string? NewEmail { get; init; }

    public string? Provider { get; init; }

    public override string ToString() => nameof(AuthEmailValues);
}

public enum EmailDarkMode
{
    None,

    Media,

    Always,
}

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

    public string ButtonTextColor => Contrast(this.PrimaryColor, "#ffffff") >= Contrast(this.PrimaryColor, "#000000") ? "#ffffff" : "#000000";

    public string ButtonRadius => this.ButtonStyle switch { "pill" => "999px", "square" => "0", _ => "8px" };

    public string LinkColor => Readable(this.AccentColor, Card, "#000000");

    public string LinkDarkColor => Readable(this.AccentColor, DarkCard, "#ffffff");

    public (string Fill, string Border) CodeBox => (Mix(this.AccentColor, Card, 0.08), Mix(this.AccentColor, Card, 0.25));

    public (string Fill, string Border) CodeBoxDark => (Mix(this.AccentColor, DarkCard, 0.12), Mix(this.AccentColor, DarkCard, 0.35));

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

    private static double Luminance(string color)
    {
        static double Channel(int value) => value / 255.0 <= 0.03928 ? value / 255.0 / 12.92 : Math.Pow((value / 255.0 + 0.055) / 1.055, 2.4);
        var (r, g, b) = Rgb(color);
        return 0.2126 * Channel(r) + 0.7152 * Channel(g) + 0.0722 * Channel(b);
    }

    private static (int R, int G, int B) Rgb(string color) =>
        (Convert.ToInt32(color[1..3], 16), Convert.ToInt32(color[3..5], 16), Convert.ToInt32(color[5..7], 16));
}

public sealed partial class AuthEmails(AuthOptions options)
{
    public const string English = "en";

    public const string Portuguese = "pt-BR";

    public static readonly IReadOnlyList<string> Locales = [English, Portuguese];

    public static readonly IReadOnlyDictionary<string, AuthEmailKind> Kinds = new Dictionary<string, AuthEmailKind>(StringComparer.Ordinal)
    {
        ["sign_in_code"] = AuthEmailKind.SignInCode,
        ["social_link_code"] = AuthEmailKind.SocialLinkCode,
        ["magic_link"] = AuthEmailKind.MagicLink,
        ["verify_email"] = AuthEmailKind.VerifyEmail,
        ["password_reset"] = AuthEmailKind.PasswordReset,
        ["invitation"] = AuthEmailKind.Invitation,
        ["mfa_code"] = AuthEmailKind.MfaCode,
        ["step_up_code"] = AuthEmailKind.StepUpCode,
        ["new_device"] = AuthEmailKind.NewDevice,
        ["lockout"] = AuthEmailKind.Lockout,
        ["email_change_code"] = AuthEmailKind.EmailChangeCode,
        ["email_change_notice"] = AuthEmailKind.EmailChangeNotice,
    };

    private static readonly HtmlEncoder Encoder = HtmlEncoder.Create(UnicodeRanges.All);

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

    public static string Name(AuthEmailKind kind) => Kinds.First(entry => entry.Value == kind).Key;

    public static IReadOnlyList<string> Variables(AuthEmailKind kind) =>
    [
        "app_name", "user.email", "support_email",
        .. kind switch
        {
            AuthEmailKind.MagicLink => ["link", "expires_in"],
            AuthEmailKind.SocialLinkCode => ["code", "expires_in", "provider"],
            AuthEmailKind.Invitation => ["link", "expires_in", "organization", "inviter"],
            AuthEmailKind.NewDevice => ["device", "location", "time"],
            AuthEmailKind.Lockout => ["locked_until"],
            AuthEmailKind.EmailChangeCode => ["code", "expires_in", "new_email"],
            AuthEmailKind.EmailChangeNotice => ["link", "expires_in", "new_email"],
            _ => (string[])["code", "expires_in"],
        },
    ];

    public static EmailTemplateFields Defaults(AuthEmailKind kind, string locale) => Copies[(kind, locale)];

    public static string? UiLocales(HttpRequest request)
    {
        var requested = request.Query["ui_locales"].ToString();
        if (requested.Length is 0 or > 256)
        {
            return null;
        }

        List<string> tags = [];
        var length = 0;
        foreach (var tag in requested.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var locale = Locales.FirstOrDefault(locale => locale.Equals(tag, StringComparison.OrdinalIgnoreCase));
            if (locale is null)
            {
                continue;
            }

            length += locale.Length + (tags.Count > 0 ? 1 : 0);
            if (length > 64)
            {
                break;
            }

            tags.Add(locale);
        }

        return tags.Count > 0 ? string.Join(' ', tags) : null;
    }

    public static string Locale(HttpRequest request) => UiLocales(request) is { } locales
        ? locales.Split(' ')[0]
        : Locale(request.Headers.AcceptLanguage);

    public static string Locale(StringValues acceptLanguage)
    {
        if (!StringWithQualityHeaderValue.TryParseList(acceptLanguage, out var ranges))
        {
            return English;
        }

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
            ["provider"] = values.Provider ?? string.Empty,
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
            values.Link is null ? null : kind == AuthEmailKind.Lockout ? (locale == Portuguese ? "Desbloquear conta" : "Unlock account") : copy.Button is null ? null : Fill(copy.Button),
            values.Link?.AbsoluteUri,
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
        await AuthEmailLayout.Create(view).RenderAsync(html, Encoder, cancellationToken);
        return new EmailMessage(view.Subject, Text(view), html.ToString())
        {
            FromName = kind == AuthEmailKind.Invitation && values.Tenant != OidcIssuer.InstanceId && PlatformDirectory.Name(values.Organization) is { } organization
                && PlatformDirectory.Name(brand.Name, brand: true) is { } brandName
                ? organization + " (" + brandName + ")" : brand.SenderName,
            ReplyTo = brand.ReplyTo,
            Tenant = values.Tenant,
            AdmissionId = values.AdmissionId,
            Purpose = kind is AuthEmailKind.SignInCode or AuthEmailKind.SocialLinkCode or AuthEmailKind.MagicLink or AuthEmailKind.VerifyEmail or AuthEmailKind.PasswordReset or AuthEmailKind.MfaCode or AuthEmailKind.StepUpCode
                ? EmailPurpose.Authentication : kind == AuthEmailKind.Invitation ? EmailPurpose.Invitation : EmailPurpose.Notification
        };
    }

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
            "Didn't try to sign in? Ignore this email. Never share this code."),
        [(AuthEmailKind.SignInCode, Portuguese)] = new(
            "{{ code }} é o seu código de acesso para {{ app_name }}",
            "Use-o para entrar. Ele expira em {{ expires_in }}.",
            "Seu código de acesso",
            "Digite este código para entrar em {{ app_name }} como {{ user.email }}.",
            null,
            "Não tentou entrar? Ignore este e-mail. Nunca compartilhe este código."),
        [(AuthEmailKind.SocialLinkCode, English)] = new(
            "{{ code }} connects {{ provider }} to your {{ app_name }} account",
            "Connect {{ provider }}. This code expires in {{ expires_in }}.",
            "Connect {{ provider }}",
            "Enter this code to connect {{ provider }} to your {{ app_name }} account.",
            null,
            "If you didn't try to sign in with {{ provider }}, ignore this email."),
        [(AuthEmailKind.SocialLinkCode, Portuguese)] = new(
            "{{ code }} conecta {{ provider }} à sua conta em {{ app_name }}",
            "Conecte {{ provider }}. Este código expira em {{ expires_in }}.",
            "Conectar {{ provider }}",
            "Digite este código para conectar {{ provider }} à sua conta em {{ app_name }}.",
            null,
            "Se você não tentou entrar com {{ provider }}, ignore este e-mail."),
        [(AuthEmailKind.MagicLink, English)] = new(
            "Sign in to {{ app_name }}",
            "Your sign-in link expires in {{ expires_in }}.",
            "Sign in to {{ app_name }}",
            "Sign in as {{ user.email }}. If you opened the link in another browser, enter the code below.",
            "Sign in",
            "Didn't try to sign in? Ignore this email. Never forward this link."),
        [(AuthEmailKind.MagicLink, Portuguese)] = new(
            "Seu link de acesso para {{ app_name }}",
            "Seu link de acesso expira em {{ expires_in }}.",
            "Entre em {{ app_name }}",
            "Entre como {{ user.email }}. Se abriu o link em outro navegador, digite o código abaixo.",
            "Entrar",
            "Não tentou entrar? Ignore este e-mail. Nunca encaminhe este link."),
        [(AuthEmailKind.VerifyEmail, English)] = new(
            "{{ code }} is your {{ app_name }} verification code",
            "Use it to verify your email. It expires in {{ expires_in }}.",
            "Verify your email address",
            "Enter this code in {{ app_name }} to confirm that {{ user.email }} belongs to you.",
            null,
            "Didn't sign up for {{ app_name }}? Ignore this email."),
        [(AuthEmailKind.VerifyEmail, Portuguese)] = new(
            "{{ code }} é o seu código de verificação para {{ app_name }}",
            "Use-o para confirmar seu e-mail. Ele expira em {{ expires_in }}.",
            "Confirme seu e-mail",
            "Digite este código em {{ app_name }} para confirmar que {{ user.email }} pertence a você.",
            null,
            "Não se cadastrou em {{ app_name }}? Ignore este e-mail."),
        [(AuthEmailKind.PasswordReset, English)] = new(
            "{{ code }} is your {{ app_name }} password reset code",
            "Use it to choose a new password. It expires in {{ expires_in }}.",
            "Reset your password",
            "Enter this code in {{ app_name }} to choose a new password for {{ user.email }}.",
            null,
            "Didn't ask to reset your password? Ignore this email; your password stays the same. Never share this code."),
        [(AuthEmailKind.PasswordReset, Portuguese)] = new(
            "{{ code }} é o seu código para redefinir a senha de {{ app_name }}",
            "Use-o para escolher uma nova senha. Ele expira em {{ expires_in }}.",
            "Redefina sua senha",
            "Digite este código em {{ app_name }} para escolher uma nova senha para {{ user.email }}.",
            null,
            "Não pediu para redefinir sua senha? Ignore este e-mail; sua senha continua a mesma. Nunca compartilhe este código."),
        [(AuthEmailKind.Invitation, English)] = new(
            "{{ inviter }} invited you to {{ organization }} on {{ app_name }}",
            "This invitation expires in {{ expires_in }}.",
            "Join {{ organization }}",
            "{{ inviter }} invited {{ user.email }} to join {{ organization }} on {{ app_name }}.",
            "Accept invitation",
            "Not expecting this invitation? Ignore this email."),
        [(AuthEmailKind.Invitation, Portuguese)] = new(
            "{{ inviter }} convidou você para {{ organization }} em {{ app_name }}",
            "Este convite expira em {{ expires_in }}.",
            "Participe de {{ organization }}",
            "{{ inviter }} convidou {{ user.email }} para participar de {{ organization }} em {{ app_name }}.",
            "Aceitar convite",
            "Não esperava este convite? Ignore este e-mail."),
        [(AuthEmailKind.MfaCode, English)] = new(
            "{{ code }} is your {{ app_name }} security code",
            "Use it to finish signing in. It expires in {{ expires_in }}.",
            "Confirm it's you",
            "Enter this code to finish signing in to {{ app_name }} as {{ user.email }}.",
            null,
            "Didn't try to sign in? Someone may know your password: change it now. Never share this code."),
        [(AuthEmailKind.MfaCode, Portuguese)] = new(
            "{{ code }} é o seu código de segurança para {{ app_name }}",
            "Use-o para terminar de entrar. Ele expira em {{ expires_in }}.",
            "Confirme que é você",
            "Digite este código para terminar de entrar em {{ app_name }} como {{ user.email }}.",
            null,
            "Não tentou entrar? Alguém pode saber sua senha: troque-a agora. Nunca compartilhe este código."),
        [(AuthEmailKind.StepUpCode, English)] = new(
            "{{ code }} is your {{ app_name }} verification code",
            "Use it to confirm it's you. It expires in {{ expires_in }}.",
            "Confirm it's you",
            "Enter this code to confirm it's you in {{ app_name }} as {{ user.email }}.",
            null,
            "Didn't ask for this? Ignore this email. Never share this code."),
        [(AuthEmailKind.StepUpCode, Portuguese)] = new(
            "{{ code }} é o seu código de verificação para {{ app_name }}",
            "Use-o para confirmar que é você. Ele expira em {{ expires_in }}.",
            "Confirme que é você",
            "Digite este código para confirmar que é você em {{ app_name }} como {{ user.email }}.",
            null,
            "Não pediu isso? Ignore este e-mail. Nunca compartilhe este código."),
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
            "Too many attempts temporarily paused an authentication method for {{ user.email }} on {{ app_name }} until {{ locked_until }}.",
            null,
            "Didn't make these attempts? Review your account security or contact support."),
        [(AuthEmailKind.Lockout, Portuguese)] = new(
            "Pausa temporária de autenticação em {{ app_name }}",
            "Um método de autenticação está pausado até {{ locked_until }}.",
            "Um método de autenticação está temporariamente pausado",
            "Muitas tentativas pausaram temporariamente um método de autenticação para {{ user.email }} em {{ app_name }} até {{ locked_until }}.",
            null,
            "Não foi você? Revise a segurança da sua conta ou fale com o suporte."),
        [(AuthEmailKind.EmailChangeCode, English)] = new(
            "{{ code }} confirms your new {{ app_name }} sign-in email",
            "Confirm {{ new_email }} as your new sign-in email.",
            "Confirm your new sign-in email",
            "Enter this code in {{ app_name }} to make {{ new_email }} your sign-in email.",
            null,
            "Didn't ask for this? Ignore this email; nothing changes without the code. Never share this code."),
        [(AuthEmailKind.EmailChangeCode, Portuguese)] = new(
            "{{ code }} confirma seu novo e-mail de acesso a {{ app_name }}",
            "Confirme {{ new_email }} como seu novo e-mail de acesso.",
            "Confirme seu novo e-mail de acesso",
            "Digite este código em {{ app_name }} para tornar {{ new_email }} seu e-mail de acesso.",
            null,
            "Não pediu isso? Ignore este e-mail; nada muda sem o código. Nunca compartilhe este código."),
        [(AuthEmailKind.EmailChangeNotice, English)] = new(
            "Your {{ app_name }} sign-in email is changing",
            "A change of your sign-in email to {{ new_email }} was requested.",
            "Your sign-in email is changing",
            "Someone who just verified your credentials asked to change the sign-in email of {{ user.email }} on {{ app_name }} to {{ new_email }}. It changes once that address confirms its code.",
            "This wasn't me",
            "If this wasn't you, use the button to cancel the change and sign out of every session, then reset your password."),
        [(AuthEmailKind.EmailChangeNotice, Portuguese)] = new(
            "Seu e-mail de acesso a {{ app_name }} vai mudar",
            "Foi pedida a troca do seu e-mail de acesso para {{ new_email }}.",
            "Seu e-mail de acesso vai mudar",
            "Alguém que acabou de verificar suas credenciais pediu para trocar o e-mail de acesso de {{ user.email }} em {{ app_name }} para {{ new_email }}. A troca acontece quando esse endereço confirmar o código.",
            "Não fui eu",
            "Se não foi você, use o botão para cancelar a troca e encerrar todas as sessões, depois redefina sua senha."),
    };
}

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
