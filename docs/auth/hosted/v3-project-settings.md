# Hosted authentication v3 project settings

`GET /v1/{project}/settings` and `PUT /v1/{project}/settings` use the existing
platform access token and project viewer/admin authorization. Settings remain
one TerraBase `project_auth_settings` document per project. These additions do
not change TerraBase's storage format. No provider secrets are returned.

A PUT replaces each supplied `branding` or `configuration` object; omitted
objects remain unchanged. Methods retain their existing partial-update behavior.
Existing stored documents use the default appearance and fourteen-day session
policy until an administrator changes them.

```json
{
  "branding": {
    "name": "Paperwork",
    "logo_url": "https://assets.example.test/logo.svg",
    "theme_color": "#2158e8",
    "title_size": 30,
    "default_theme": "system",
    "background_color": null,
    "text_color": null,
    "footer_links": [
      { "label": "Privacy", "url": "https://example.test/privacy" }
    ]
  },
  "configuration": {
    "redirect_urls": ["https://app.example.test/callback"],
    "session_lifetime_seconds": 3600
  }
}
```

Names have 1–64 characters; logo/footer URLs must be HTTPS without userinfo and
at most 2048 characters. Colours are exactly `#rrggbb`; title size is 20–40 px.
The default theme is `light`, `dark` or `system`. At most five footer links are
accepted, with labels of 1–24 characters and no control characters. Null branding
fields restore defaults; an empty footer list intentionally removes project links.
Razor encodes names, URLs and labels; only validated colours enter CSS. Hosted
pages load the current project settings on each page request.

Session lifetime is an absolute duration of 300–2592000 seconds (five minutes to
thirty days), default 1209600. Project policy is isolated from the TerraScale
platform tenant. A reduced policy takes effect at the next session or grant
check. Once observed, a shortened browser session expiry is persisted; an
expired OAuth authorization is revoked, so increasing the policy cannot revive
that sign-in. Existing access tokens retain their issued short expiry. Redirects are exact absolute HTTPS URLs (HTTP is allowed only
for loopback development), without fragments or userinfo; at most 100. A nonempty
project list restricts registered OAuth client redirects further. An empty list
adds no project restriction: it never permits unregistered OAuth redirects.
Changing branding does not change a user's credentials, provider configuration
or project access. Only configured, implemented authentication methods belong on
the hosted page; unavailable providers require their own credential setup.
