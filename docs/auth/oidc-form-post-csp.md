# OIDC `response_mode=form_post` CSP

`/{tenant}/oauth/authorize?response_mode=form_post` answers 200 with OpenIddict's
autosubmit page, which posts `code`+`state` (or `error`+`state`) to the client's
redirect URI. The generic hosted CSP (`default-src 'none'; form-action 'self'`)
blocks both its inline script and the cross-origin post, so this one response
gets its own policy from `FormPostSecurityPolicy`:

```
default-src 'none'; script-src 'sha256-<OpenIddict autosubmit script>';
form-action <callback origin>; base-uri 'none'; object-src 'none'; frame-ancestors 'none'
```

- It runs after OpenIddict's client validation and the project redirect
  allowlist (`ProjectRedirectPolicy`), so the target is always a registered,
  allowed callback. A disallowed URI never reaches this page.
- `script-src` allows only the pinned hash of OpenIddict's fixed
  `document.form.submit();`. An OpenIddict upgrade that changes that script fails
  the Fast test `The_pinned_script_hash_matches_the_page_OpenIddict_renders`.
- `form-action` is the callback's origin (IDN hosts as punycode, non-default
  port kept). CSP has no syntax for IPv6 literals or native-app hosts, so those
  callbacks get their scheme (`https:`, `com.example.app:`).
- Browsers also apply `form-action` to redirects after the post: a callback that
  redirects to another origin is blocked. Answer the post on the callback's own
  origin.
- Every other hosted page keeps its generic CSP unchanged.
