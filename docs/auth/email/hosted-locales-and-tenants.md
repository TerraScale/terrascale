# Hosted email locales and tenant isolation

Hosted sign-up and password-reset initiation now carry the request's negotiated
`Accept-Language` locale into queued issuance and delivery. This includes the
existing-account sign-up branch, which sends a sign-in code. Password-attempt
lockouts and reset-code lockouts also carry the hosted request's locale, matching
sign-in and sign-up code lockout notices. Supported email locales remain `en` and
`pt-BR`; an absent or unsupported language uses the existing English fallback.
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

`Identity/HostedEmailLocaleTests.cs` adds Fast integration coverage through real
hosted HTTP routes and TerraBase persistence with a capturing sender:

- New sign-up, existing-account sign-up and password reset in English and
  Portuguese, for platform and project tenants; assert message language, purpose,
  tenant and successful code redemption (plus password login after reset).
- Project password, sign-in-code, sign-up-code and reset-code lockouts, with and
  without custom project branding; assert Portuguese notices, deadline, fixed
  sender/footer, platform customization isolation, independent tenant budgets and
  no duplicate notice after repeated refusals and worker drain.

These tests send no live mail. Required CI checks, including Fast, remain the
merge gate.

The separate [queue admission change](queue-admission.md) refuses a full or
stopping authentication queue before accepting a start or committing an
invitation. Lockout notices remain best effort on their independent bounded
queue. A process crash can still lose queued work. Durable delivery requires a
separate design preserving asynchronous anti-enumeration, tenant ownership,
admission budgets and no automatic resend of an ambiguous provider outcome.
