using System.Globalization;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.RegularExpressions;
using System.Text.Unicode;
using Microsoft.Extensions.Primitives;
using Microsoft.Net.Http.Headers;
using TerraScale.Auth.Configuration;

namespace TerraScale.Auth.Features.Email;

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
}

/// <summary>
/// The values one email shows. Each kind's copy uses only some of them: a code for the code kinds, a link for
/// <see cref="AuthEmailKind.MagicLink"/> and <see cref="AuthEmailKind.Invitation"/>, the device details for
/// <see cref="AuthEmailKind.NewDevice"/>.
/// </summary>
/// <param name="Email">The recipient's address.</param>
public sealed record AuthEmailValues(string Email)
{
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

    /// <summary>Omits every value, so formatting or logging the record never discloses a code or a link.</summary>
    public override string ToString() => nameof(AuthEmailValues);
}

/// <summary>Who an email comes from and how it looks.</summary>
/// <param name="Name">The product name, shown as <c>{{ app_name }}</c> and as the logo's alt text.</param>
/// <param name="LogoUrl">A PNG logo for light backgrounds.</param>
/// <param name="LogoDarkUrl">The same logo for dark backgrounds.</param>
/// <param name="LogoWidth">Logo width in CSS pixels; Outlook needs it.</param>
/// <param name="LogoHeight">Logo height in CSS pixels.</param>
/// <param name="PrimaryColor">Button colour, <c>#rrggbb</c>, with white text.</param>
/// <param name="WebsiteUrl">The product's website, linked in the footer.</param>
public sealed record EmailBrand(string Name, Uri LogoUrl, Uri LogoDarkUrl, int LogoWidth, int LogoHeight, string PrimaryColor, Uri WebsiteUrl);

/// <summary>
/// Renders every ts-auth email as a responsive HTML message with a plain-text alternative, in English or Brazilian
/// Portuguese. The HTML follows the reader's light or dark mode, and works without images or styles. The copy
/// takes <c>{{ name }}</c> placeholders and nothing else: there is no logic to run, and every value is HTML-encoded
/// by the layout.
/// </summary>
public sealed partial class AuthEmails(AuthOptions options)
{
    /// <summary>English, the fallback locale.</summary>
    public const string English = "en";

    /// <summary>Brazilian Portuguese.</summary>
    public const string Portuguese = "pt-BR";

    // Keeps accented letters readable in the HTML part; markup characters are still encoded.
    private static readonly HtmlEncoder Encoder = HtmlEncoder.Create(UnicodeRanges.All);

    /// <summary>TerraScale's own brand, served from this deployment's public origin.</summary>
    public EmailBrand Brand { get; } = new(
        "TerraScale",
        new Uri(options.PublicOrigin, "/admin/email-logo.png"),
        new Uri(options.PublicOrigin, "/admin/email-logo-dark.png"),
        LogoWidth: 150,
        LogoHeight: 36,
        PrimaryColor: "#2158e8",
        new Uri("https://terrascale.tech"));

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

    /// <summary>Renders <paramref name="kind"/> for <paramref name="values"/>; an unknown locale gets English.</summary>
    public async Task<EmailMessage> RenderAsync(AuthEmailKind kind, string locale, AuthEmailValues values, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(values);
        if (!Chromes.TryGetValue(locale, out var chrome))
        {
            (locale, chrome) = (English, Chromes[English]);
        }

        var copy = Copies[(kind, locale)];
        var variables = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["app_name"] = this.Brand.Name,
            ["user.email"] = values.Email,
            ["code"] = values.Code ?? string.Empty,
            ["link"] = values.Link?.AbsoluteUri ?? string.Empty,
            ["expires_in"] = values.ExpiresIn is { } lifetime ? chrome.Duration(lifetime) : string.Empty,
            ["organization"] = values.Organization ?? string.Empty,
            ["inviter"] = values.Inviter ?? string.Empty,
            ["device"] = values.Device ?? string.Empty,
            ["location"] = values.Location ?? string.Empty,
            ["time"] = values.Time?.UtcDateTime.ToString("yyyy-MM-dd HH:mm 'UTC'", CultureInfo.InvariantCulture) ?? string.Empty,
        };
        string Fill(string template) => Placeholder().Replace(template, match => variables.GetValueOrDefault(match.Groups["name"].Value, string.Empty));

        var expiry = values.ExpiresIn is null ? null : Fill(kind switch
        {
            AuthEmailKind.MagicLink => chrome.LinkExpiry,
            AuthEmailKind.Invitation => chrome.InvitationExpiry,
            AuthEmailKind.NewDevice => string.Empty,
            _ => chrome.CodeExpiry,
        });
        var view = new AuthEmailView(
            locale,
            Fill(copy.Subject),
            Fill(copy.Preheader),
            Fill(copy.Heading),
            [.. Fill(copy.Body).Split("\n\n").Select(paragraph => paragraph.Split('\n'))],
            values.Code,
            string.IsNullOrEmpty(expiry) ? null : expiry,
            values.Link is null ? null : copy.Button is null ? null : Fill(copy.Button),
            values.Link?.AbsoluteUri,
            Fill(chrome.LinkFallback),
            Fill(copy.Footnote),
            Fill(chrome.SentTo),
            this.Brand);

        await using var html = new StringWriter(CultureInfo.InvariantCulture);
        await AuthEmailLayout.Create(view).RenderAsync(html, Encoder, cancellationToken).ConfigureAwait(false);
        return new EmailMessage(view.Subject, Text(view), html.ToString());
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

        text.Append(view.Footnote).Append("\n\n-- \n").Append(view.SentTo).Append('\n')
            .Append(view.Brand.Name).Append(" · ").Append(view.Brand.WebsiteUrl.Host).Append('\n');
        return text.ToString();
    }

    [GeneratedRegex(@"\{\{\s*(?<name>[a-z_]+(?:\.[a-z_]+)?)\s*\}\}")]
    private static partial Regex Placeholder();

    /// <summary>The editable copy of one kind in one locale.</summary>
    private sealed record Copy(string Subject, string Preheader, string Heading, string Body, string? Button, string Footnote);

    /// <summary>The layout's own lines in one locale.</summary>
    private sealed record Chrome(string CodeExpiry, string LinkExpiry, string InvitationExpiry, string LinkFallback, string SentTo, Func<TimeSpan, string> Duration);

    private static readonly Dictionary<string, Chrome> Chromes = new(StringComparer.Ordinal)
    {
        [English] = new(
            "This code expires in {{ expires_in }} and works only once.",
            "This link expires in {{ expires_in }} and works only once.",
            "This invitation expires in {{ expires_in }}.",
            "Button not working? Paste this link into your browser:",
            "This email was sent to {{ user.email }}.",
            lifetime => Duration(lifetime, ("minute", "minutes"), ("hour", "hours"), ("day", "days"))),
        [Portuguese] = new(
            "Este código expira em {{ expires_in }} e só pode ser usado uma vez.",
            "Este link expira em {{ expires_in }} e só pode ser usado uma vez.",
            "Este convite expira em {{ expires_in }}.",
            "O botão não funciona? Cole este link no seu navegador:",
            "Este e-mail foi enviado para {{ user.email }}.",
            lifetime => Duration(lifetime, ("minuto", "minutos"), ("hora", "horas"), ("dia", "dias"))),
    };

    private static string Duration(TimeSpan lifetime, (string One, string Many) minutes, (string One, string Many) hours, (string One, string Many) days)
    {
        var (count, unit) = lifetime.TotalDays >= 2 && lifetime.TotalDays % 1 == 0 ? ((int)lifetime.TotalDays, days)
            : lifetime.TotalHours >= 1 && lifetime.TotalHours % 1 == 0 ? ((int)lifetime.TotalHours, hours)
            : ((int)Math.Ceiling(lifetime.TotalMinutes), minutes);
        return string.Create(CultureInfo.InvariantCulture, $"{count} {(count == 1 ? unit.One : unit.Many)}");
    }

    private static readonly Dictionary<(AuthEmailKind, string), Copy> Copies = new()
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
/// <param name="Brand">The sender's brand.</param>
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
    EmailBrand Brand);
