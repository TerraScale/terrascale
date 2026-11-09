# Hosted email locales and tenant isolation

Hosted sign-up and password-reset initiation now carry the request's negotiated
`Accept-Language` locale into queued issuance and delivery. This includes the
existing-account sign-up branch, which sends a sign-in code. Password-attempt
lockouts and reset-code lockouts also carry the hosted request's locale, matching
sign-in and sign-up code lockout notices. Supported email locales remain `en` and
`pt-BR`; an absent or unsupported language uses the existing English fallback. The
hosted pages resolve their language from the OIDC `ui_locales` request parameter
first (same two locales), then `Accept-Language`, then English.
There are no new HTTP fields, routes, configuration settings or storage formats.

Lockout work captures the resolved tenant before leaving the request and includes
it in the rendered message and delivery accounting. Project lockout notices use
the same isolated project branding path as project authentication codes: project
name (or its directory name), logo, theme colour and optional email message, with
fixed `TerraScale` sender display name and `Sent by TerraScale Authentication.`
footer. They do not inherit platform template overrides, sender identity or
Reply-To. Configuration-read failures retain the existing bounded timeout and
safe defaults; the message's tenant remains its project.

Lockout mail remains `Notification`, outside the authentication reserve. Existing
per-tenant password/code attempt budgets, shared sign-in/sign-up code budget,
independent reset-code budget, durable once-per-window notice claims and unknown
account response behavior are preserved. No extra completion email is introduced.

The locale and lockout matrix tests were deleted under the 2026-11-04 happy-path
test policy. Fast `EmailApiTests` keeps one case: the next sign-in code email uses
the saved branding and copy.

The separate [queue admission change](queue-admission.md) refuses a full or
stopping authentication queue before accepting a start or committing an
invitation. Lockout notices remain best effort on their independent bounded
queue. A process crash can still lose queued work. Durable delivery requires a
separate design preserving asynchronous anti-enumeration, tenant ownership,
admission budgets and no automatic resend of an ambiguous provider outcome.
