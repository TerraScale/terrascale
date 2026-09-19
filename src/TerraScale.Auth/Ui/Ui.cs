namespace TerraScale.Auth.Ui;

/// <summary>Server-rendered hosted UI chrome shared by all ts-auth pages.</summary>
public static class Ui
{
    /// <summary>Renders a full HTML page with the shared stylesheet and optional instance branding.</summary>
    public static IResult Page(string title, string body, string? extraHead = null, int status = 200, string? themeColor = null, string? logoUrl = null)
    {
        var brandOverride = (themeColor is not null || logoUrl is not null)
            ? "<style>:root{--accent:" + Esc(themeColor ?? "#6c5ce7") + "}</style>"
            : null;
        var logo = logoUrl is not null
            ? $"""<img src="{Esc(logoUrl)}" alt="" class="logo-img">"""
            : """<span class="logo">◇</span>""";
        return Results.Text(
            $$"""
            <!doctype html>
            <html lang="en">
            <head>
            <meta charset="utf-8">
            <meta name="viewport" content="width=device-width, initial-scale=1">
            <title>{{System.Net.WebUtility.HtmlEncode(title)}} · ts-auth</title>
            {{extraHead}}
            <style>{{Css}}</style>
            {{brandOverride}}
            </head>
            <body>
            <main class="card">
            <div class="brand">{{logo}} ts-auth</div>
            {{body}}
            </main>
            </body>
            </html>
            """,
            "text/html; charset=utf-8",
            statusCode: status);
    }

    /// <summary>HTML-escapes text for interpolation into pages.</summary>
    public static string Esc(string? value) => System.Net.WebUtility.HtmlEncode(value ?? "");

    private const string Css = """
        :root { color-scheme: light dark; --accent: #6c5ce7; }
        * { box-sizing: border-box; }
        body { font: 16px/1.5 system-ui, sans-serif; margin: 0; display: grid; place-items: center; min-height: 100vh; background: #f6f7f9; color: #16191d; }
        @media (prefers-color-scheme: dark) { body { background: #101418; color: #e8eaed; } }
        .card { background: #fff; border: 1px solid #dfe3e8; border-radius: 12px; padding: 32px; width: min(400px, 92vw); box-shadow: 0 8px 30px rgba(0,0,0,.06); }
        @media (prefers-color-scheme: dark) { .card { background: #171c21; border-color: #2a3138; } }
        .brand { display: flex; align-items: center; gap: 8px; font-weight: 700; margin-bottom: 20px; }
        .logo { color: var(--accent); }
        .logo-img { height: 28px; width: auto; }
        h1 { font-size: 20px; margin: 0 0 16px; }
        label { display: block; font-size: 14px; margin: 12px 0 4px; color: #5c6672; }
        @media (prefers-color-scheme: dark) { label { color: #9aa4af; } }
        input, select { width: 100%; padding: 10px 12px; border: 1px solid #dfe3e8; border-radius: 8px; font: inherit; background: transparent; color: inherit; }
        @media (prefers-color-scheme: dark) { input, select { border-color: #2a3138; } }
        input:focus { outline: 2px solid var(--accent); border-color: transparent; }
        button { width: 100%; margin-top: 16px; padding: 10px 12px; border: 0; border-radius: 8px; background: var(--accent); color: #fff; font: inherit; font-weight: 600; cursor: pointer; }
        button:hover { filter: brightness(.92); }
        button.secondary { background: transparent; border: 1px solid #dfe3e8; color: inherit; }
        @media (prefers-color-scheme: dark) { button.secondary { border-color: #2a3138; } }
        .error { background: #fdecec; color: #9d2235; border-radius: 8px; padding: 10px 12px; margin-bottom: 8px; font-size: 14px; }
        .success { background: #e8f6ee; color: #1c7c46; border-radius: 8px; padding: 10px 12px; margin-bottom: 8px; font-size: 14px; }
        @media (prefers-color-scheme: dark) { .error { background: #3a1d23; } .success { background: #17301f; } }
        .hint { font-size: 13px; color: #5c6672; margin-top: 14px; text-align: center; }
        @media (prefers-color-scheme: dark) { .hint { color: #9aa4af; } }
        .hint a { color: var(--accent); }
        .list { display: grid; gap: 8px; margin: 12px 0; }
        .row { display: flex; justify-content: space-between; align-items: center; gap: 12px; padding: 10px 12px; border: 1px solid #dfe3e8; border-radius: 8px; font-size: 14px; }
        @media (prefers-color-scheme: dark) { .row { border-color: #2a3138; } }
        .muted { color: #5c6672; font-size: 13px; }
        @media (prefers-color-scheme: dark) { .muted { color: #9aa4af; } }
        form.inline { display: contents; }
        """;
}

/// <summary>Session cookie conventions for the hosted UI.</summary>
public static class SessionCookies
{
    /// <summary>The hosted-UI session cookie name.</summary>
    public const string Name = "tsauth_session";

    /// <summary>Options for the session cookie: host-only, lax, http-only.</summary>
    public static CookieOptions Options => new()
    {
        HttpOnly = true,
        SameSite = SameSiteMode.Lax,
        Secure = true,
        Path = "/",
        IsEssential = true,
    };

    // CSRF POSTURE (there are no anti-forgery tokens; this is the complete protection model)
    // ------------------------------------------------------------------------------------------------
    // The session cookie is SameSite=Lax: a cross-site form POST (the classic CSRF vector) is sent
    // WITHOUT the cookie, so every session-gated state-changing route below is protected by the
    // browser, not by handler code. HostedUiSecurityTests pins the cookie flags and the rejection.
    //
    // Session-gated POSTs (SameSite=Lax protected; also 405 on GET, pinned per module):
    //   Users     /ui/{slug}/sign-out, /account/password (also re-checks the current password),
    //             /account/sessions/revoke-all, /account/sessions/{id}/revoke
    //   MFA       /ui/{slug}/mfa/enroll/start, /mfa/enroll/confirm, /mfa/recovery/consume
    //   Passkeys  /ui/{slug}/passkeys/register, /passkeys/register/options, /manage/{id}/delete
    //   Admin     /ui/{slug}/admin/connections, /admin/connections/{id}/enable|disable
    //             (org-admin role additionally required)
    //
    // Pre-auth POSTs (CSRF-immune: no ambient authority — they cannot read or act on an existing
    // session, only mint a new one from submitted credentials/secret, or send an email):
    //   /sign-up, /sign-in, /magic-link, /forgot-password, /passcode/request, /passcode/verify,
    //   /passcode/mfa + /mfa/verify (pending-MFA token secret required), /reset-password
    //   (single-use secret token required), /connections/{id}/saml/acs (signed response + state).
    //   Known residual, accepted: sign-in POST can overwrite an existing session cookie with one
    //   for the submitted credentials ("login CSRF" — the attacker gains nothing but noise).
    //
    // GETs that mutate — all gated by a SINGLE-USE SECRET in the URL (email-link flows; the secret
    // is the authority, not the cookie, so a forged cross-site link cannot trigger them):
    //   /magic-link/consume, /verify-email, /invitations/{id}/accept (session + email match too).
    //
    // GETs that mutate on the session cookie — deliberate, spec-inherent exceptions:
    //   /{slug}/oauth/authorize mints a code from the session on top-level navigation (OAuth is a
    //   front-channel GET protocol; cross-client code injection is the RP's `state` to stop, and
    //   v1 has no consent screen by design). /{slug}/oauth/logout revokes the session on GET
    //   (OIDC RP-initiated logout reaches the browser as a redirect; worst case is forced logout).
    // All other GET routes render only and mutate nothing.
    // ------------------------------------------------------------------------------------------------
}
