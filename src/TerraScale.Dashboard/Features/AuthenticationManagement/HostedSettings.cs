using TerraScale.Dashboard.Features.Accounts;
using TerraScale.DotNet;

namespace TerraScale.Dashboard.Features.AuthenticationManagement;

public static partial class AuthenticationConsole
{
    public static TerraScaleAuthProjectBranding HostedBranding(IFormCollection form)
    {
        var basic = Branding(form["name"], form["logo_url"], form["theme_color"]);
        var mode = form["default_theme"].ToString();
        if (mode.Length == 0) mode = "system";
        if (mode is not ("light" or "dark" or "system")) throw new AccountAccessException("Choose light, dark or system for the default theme.", 422);
        var size = form["title_size"].ToString();
        var titleSize = size.Length == 0 ? 24 : int.TryParse(size, out var value) ? value : 0;
        if (titleSize is < 20 or > 40) throw new AccountAccessException("Title size must be 20 to 40 pixels.", 422);
        var links = Lines(form["footer_links"]);
        if (links.Length > 5) throw new AccountAccessException("Add up to 5 footer links.", 422);
        var footer = links.Select(line =>
        {
            var pair = line.Split('|', 2, StringSplitOptions.TrimEntries);
            if (pair.Length != 2 || pair[0].Length is < 1 or > 24 || pair[0].Any(char.IsControl) || !HttpsUrl(pair[1]))
                throw new AccountAccessException("Each footer link needs a label of 1 to 24 characters and an HTTPS URL: Label | URL.", 422);
            return new TerraScaleAuthProjectFooterLink(pair[0], pair[1]);
        }).ToArray();
        var message = form["email_message"].ToString().Replace("\r\n", "\n", StringComparison.Ordinal);
        if (message.Length > 500 || message.Any(character => char.IsControl(character) && character != '\n'))
            throw new AccountAccessException("Use a plain text email message of up to 500 characters, with only newlines as control characters.", 422);
        return basic with
        {
            TitleSize = titleSize,
            DefaultTheme = mode,
            BackgroundColor = OptionalColor(form["background_color"]),
            TextColor = OptionalColor(form["text_color"]),
            FooterLinks = footer,
            EmailMessage = message.Length == 0 ? null : message
        };
    }

    public static TerraScaleAuthProjectConfiguration Configuration(string? redirects, string? lifetime)
    {
        var urls = Lines(redirects);
        if (urls.Length > 100 || urls.Any(url => !AllowedUrl(url, true)))
            throw new AccountAccessException("Add up to 100 exact HTTPS redirect URLs (HTTP only on localhost or loopback), without fragments or credentials.", 422);
        if (!int.TryParse(lifetime, out var seconds) || seconds is < 300 or > 2592000)
            throw new AccountAccessException("Session lifetime must be 300 to 2592000 seconds.", 422);
        return new(urls, seconds);
    }

    private static string? OptionalColor(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return null;
        var color = value.Trim().ToLowerInvariant();
        return Color().IsMatch(color) ? color : throw new AccountAccessException("Colors must look like #2158e8.", 422);
    }

    private static string[] Lines(string? value) => (value ?? "").Split('\n', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);

    private static bool HttpsUrl(string value) => value.Length <= 2048 && Uri.TryCreate(value, UriKind.Absolute, out var uri) && uri.Scheme == Uri.UriSchemeHttps && uri.UserInfo.Length == 0;

    private static bool AllowedUrl(string value, bool loopback) =>
        value.Length <= 2048 && !value.Contains('*') && !value.Contains('#') && Uri.TryCreate(value, UriKind.Absolute, out var uri) && uri.UserInfo.Length == 0 && uri.Host.Length > 0 &&
        (uri.Scheme == Uri.UriSchemeHttps || loopback && uri.Scheme == Uri.UriSchemeHttp &&
            uri.IsLoopback);
}
