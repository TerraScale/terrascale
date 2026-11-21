using System.Diagnostics;
using TerraScale.Dashboard.Errors;
using TerraScale.Dashboard.Platform.TsAuth;

namespace TerraScale.Dashboard.Features.AuthenticationManagement
{
    public static partial class AuthenticationConsole
    {
        public static Valid<TsAuthProjectBranding> HostedBranding(IFormCollection form)
        {
            var basic = Branding(form["name"], form["logo_url"], form["theme_color"]);
            if (basic is Validation brandingError)
            {
                return brandingError;
            }

            var mode = form["default_theme"].ToString();
            if (mode.Length == 0)
            {
                mode = "system";
            }

            if (mode is not ("light" or "dark" or "system"))
            {
                return new Validation("Choose light, dark or system for the default theme.");
            }

            var size = form["title_size"].ToString();
            var titleSize = size.Length == 0 ? 24 : int.TryParse(size, out var value) ? value : 0;
            if (titleSize is < 20 or > 40)
            {
                return new Validation("Title size must be 20 to 40 pixels.");
            }

            var links = Lines(form["footer_links"]);
            if (links.Length > 5)
            {
                return new Validation("Add up to 5 footer links.");
            }

            List<TsAuthProjectFooterLink> footer = [];
            foreach (var line in links)
            {
                var pair = line.Split('|', 2, StringSplitOptions.TrimEntries);
                if (pair.Length != 2 || pair[0].Length is < 1 or > 24 || pair[0].Any(char.IsControl) || !HttpsUrl(pair[1]))
                {
                    return new Validation("Each footer link needs a label of 1 to 24 characters and an HTTPS URL: Label | URL.");
                }

                footer.Add(new(pair[0], pair[1]));
            }

            var message = form["email_message"].ToString().Replace("\r\n", "\n", StringComparison.Ordinal);
            if (message.Length > 500 || message.Any(character => char.IsControl(character) && character != '\n'))
            {
                return new Validation("Use a plain text email message of up to 500 characters, with only newlines as control characters.");
            }

            string? backgroundColor = null;
            if (!string.IsNullOrWhiteSpace(form["background_color"]))
            {
                backgroundColor = form["background_color"].ToString().Trim().ToLowerInvariant();
                if (!Color().IsMatch(backgroundColor))
                {
                    return new Validation("Colors must look like #2158e8.");
                }
            }

            string? textColor = null;
            if (!string.IsNullOrWhiteSpace(form["text_color"]))
            {
                textColor = form["text_color"].ToString().Trim().ToLowerInvariant();
                if (!Color().IsMatch(textColor))
                {
                    return new Validation("Colors must look like #2158e8.");
                }
            }

            return basic switch
            {
                TsAuthProjectBranding branding => branding with
                {
                    TitleSize = titleSize,
                    DefaultTheme = mode,
                    BackgroundColor = backgroundColor,
                    TextColor = textColor,
                    FooterLinks = footer,
                    EmailMessage = message.Length == 0 ? null : message
                },
                Validation => throw new UnreachableException(),
            };
        }

        public static Valid<TsAuthProjectConfiguration> Configuration(string? redirects, string? lifetime)
        {
            var urls = Lines(redirects);
            if (urls.Length > 100 || urls.Any(url => !AllowedUrl(url, true)))
            {
                return new Validation("Add up to 100 exact HTTPS redirect URLs (HTTP only on localhost or loopback), without fragments or credentials.");
            }

            if (!int.TryParse(lifetime, out var seconds) || seconds is < 300 or > 2592000)
            {
                return new Validation("Session lifetime must be 300 to 2592000 seconds.");
            }

            return new TsAuthProjectConfiguration(urls, seconds);
        }

        private static string[] Lines(string? value)
        {
            return (value ?? "").Split('\n', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
        }

        private static bool HttpsUrl(string value)
        {
            return value.Length <= 2048 && Uri.TryCreate(value, UriKind.Absolute, out var uri) && uri.Scheme == Uri.UriSchemeHttps && uri.UserInfo.Length == 0;
        }

        private static bool AllowedUrl(string value, bool loopback)
        {
            return value.Length <= 2048 && !value.Contains('*') && !value.Contains('#') && Uri.TryCreate(value, UriKind.Absolute, out var uri) && uri.UserInfo.Length == 0 && uri.Host.Length > 0 &&
            (uri.Scheme == Uri.UriSchemeHttps || loopback && uri.Scheme == Uri.UriSchemeHttp &&
                uri.IsLoopback);
        }
    }
}
