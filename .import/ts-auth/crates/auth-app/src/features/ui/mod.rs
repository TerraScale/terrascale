//! Shared server-rendered HTML chrome and hosted-session cookie helpers.

use axum::{
    http::{HeaderValue, StatusCode, header::SET_COOKIE},
    response::{IntoResponse, Response},
};

/// Cookie name used by every hosted UI session route.
pub const SESSION_COOKIE_NAME: &str = "tsauth_session";

/// Escape data for interpolation into HTML text or quoted attribute contexts.
///
/// Callers must still keep executable markup separate from data: escaping a
/// value does not make it appropriate to insert into JavaScript or CSS.
pub fn escape_html(value: &str) -> String {
    let mut escaped = String::with_capacity(value.len());
    for ch in value.chars() {
        match ch {
            '&' => escaped.push_str("&amp;"),
            '<' => escaped.push_str("&lt;"),
            '>' => escaped.push_str("&gt;"),
            '"' => escaped.push_str("&quot;"),
            '\'' => escaped.push_str("&#39;"),
            _ => escaped.push(ch),
        }
    }
    escaped
}

/// Render a simple hosted page using the shared ts-auth card and branding.
/// `body` and `extra_head` are trusted HTML fragments; interpolate user data
/// through [`escape_html`] before constructing them.
pub fn page(
    title: &str,
    body: &str,
    extra_head: Option<&str>,
    status: StatusCode,
    theme_color: Option<&str>,
    logo_url: Option<&str>,
) -> Response {
    let html = render_page(title, body, extra_head, theme_color, logo_url);
    let mut response = axum::response::Html(html).into_response();
    *response.status_mut() = status;
    response
}

fn render_page(
    title: &str,
    body: &str,
    extra_head: Option<&str>,
    theme_color: Option<&str>,
    logo_url: Option<&str>,
) -> String {
    let brand_override = if theme_color.is_some() || logo_url.is_some() {
        let accent = theme_color
            .filter(|color| is_hex_color(color))
            .unwrap_or("#6c5ce7");
        format!("<style>:root{{--accent:{accent}}}</style>")
    } else {
        String::new()
    };
    let logo = match logo_url {
        Some(url) => format!(
            "<img src=\"{}\" alt=\"\" class=\"logo-img\">",
            escape_html(url)
        ),
        None => "<span class=\"logo\">◇</span>".to_owned(),
    };
    format!(
        "<!doctype html>\n<html lang=\"en\">\n<head>\n<meta charset=\"utf-8\">\n<meta name=\"viewport\" content=\"width=device-width, initial-scale=1\">\n<title>{} · ts-auth</title>\n{}\n<style>{PAGE_CSS}</style>\n{}\n</head>\n<body>\n<main class=\"card\">\n<div class=\"brand\">{} ts-auth</div>\n{}\n</main>\n</body>\n</html>",
        escape_html(title),
        extra_head.unwrap_or_default(),
        brand_override,
        logo,
        body,
    )
}

fn is_hex_color(color: &str) -> bool {
    color.len() == 7
        && color.starts_with('#')
        && color[1..].bytes().all(|byte| byte.is_ascii_hexdigit())
}

/// Build the hosted session cookie with the source service's CSRF and browser
/// protections. Session tokens are generated as URL-safe random strings.
pub fn session_cookie(value: &str) -> HeaderValue {
    let cookie = format!("{SESSION_COOKIE_NAME}={value}; Path=/; Secure; HttpOnly; SameSite=Lax");
    HeaderValue::from_str(&cookie).expect("session tokens are valid HTTP header values")
}

/// Expire the hosted session cookie using the same path and security flags.
pub fn clear_session_cookie() -> HeaderValue {
    HeaderValue::from_static(
        "tsauth_session=; Path=/; Secure; HttpOnly; SameSite=Lax; Max-Age=0; Expires=Thu, 01 Jan 1970 00:00:00 GMT",
    )
}

/// Attach a session cookie to an Axum response.
pub fn set_session_cookie(response: &mut Response, value: &str) {
    response
        .headers_mut()
        .append(SET_COOKIE, session_cookie(value));
}

/// Attach the session-cookie deletion header to an Axum response.
pub fn delete_session_cookie(response: &mut Response) {
    response
        .headers_mut()
        .append(SET_COOKIE, clear_session_cookie());
}

const PAGE_CSS: &str = r#"
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
"#;

#[cfg(test)]
mod tests {
    use super::*;
    use axum::http::header::SET_COOKIE;

    #[test]
    fn escapes_html_text_and_attribute_metacharacters() {
        let escaped = escape_html("<script>\"&'x</script>");
        assert_eq!(escaped, "&lt;script&gt;&quot;&amp;&#39;x&lt;/script&gt;");
    }

    #[test]
    fn renders_brand_values_as_escaped_markup_and_keeps_status() {
        let html = render_page(
            "<title>",
            "<h1>Allowed fragment</h1>",
            Some("<meta name=\"test\" content=\"ok\">"),
            Some("#123456"),
            Some("https://example.test/logo?a=1&b=2\" onerror=\"alert(1)"),
        );
        assert!(html.contains("&lt;title&gt; · ts-auth"));
        assert!(html.contains(
            "src=\"https://example.test/logo?a=1&amp;b=2&quot; onerror=&quot;alert(1)\""
        ));
        assert!(html.contains("<h1>Allowed fragment</h1>"));

        let response = page(
            "<title>",
            "<h1>Allowed fragment</h1>",
            Some("<meta name=\"test\" content=\"ok\">"),
            StatusCode::FORBIDDEN,
            Some("#123456"),
            Some("https://example.test/logo?a=1&b=2"),
        );
        assert_eq!(response.status(), StatusCode::FORBIDDEN);
    }

    #[test]
    fn theme_color_accepts_only_six_hex_digits_before_css_interpolation() {
        let valid = render_page("Sign in", "<p>Welcome</p>", None, Some("#12aBcD"), None);
        assert!(valid.contains("<style>:root{--accent:#12aBcD}</style>"));

        for invalid in [
            "#112233;}body{display:none",
            "red;background:url(https://attacker.example/pixel)",
            "#12é456",
            "#12345",
        ] {
            let html = render_page("Sign in", "<p>Welcome</p>", None, Some(invalid), None);
            assert!(html.contains("<style>:root{--accent:#6c5ce7}</style>"));
            assert!(!html.contains(invalid));
        }
    }

    #[test]
    fn session_cookies_keep_the_lax_secure_http_only_contract() {
        let cookie = session_cookie("safe-token");
        let value = cookie.to_str().expect("ASCII cookie");
        assert!(value.starts_with("tsauth_session=safe-token;"));
        assert!(value.contains("Path=/"));
        assert!(value.contains("Secure"));
        assert!(value.contains("HttpOnly"));
        assert!(value.contains("SameSite=Lax"));
        assert!(!value.contains("Max-Age"));

        let mut response = axum::response::Response::new(axum::body::Body::empty());
        delete_session_cookie(&mut response);
        let deletion = response
            .headers()
            .get(SET_COOKIE)
            .unwrap()
            .to_str()
            .unwrap();
        assert!(deletion.contains("tsauth_session=;"));
        assert!(deletion.contains("Max-Age=0"));
        assert!(deletion.contains("SameSite=Lax"));
    }
}
