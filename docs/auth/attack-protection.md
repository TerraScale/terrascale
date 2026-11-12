# Project sign-in attack protection

Project administrators manage Security → Attack protection in the Authentication console.
These settings are customer data, scoped to the project's end-user tenant. Platform sign-in
keeps its existing policy.

The `attack_protection` object on `GET /v1/{project}/settings` contains `bot_challenge`,
`breached_passwords`, `account_lockout`, `new_device_email` and read-only
`challenge_configured`. Send the four writable booleans together in the same object on
`PUT /v1/{project}/settings`; omitting the object preserves the settings.

## Bot challenge

Bot challenges default off. Cloudflare Turnstile protects hosted password sign-in, email
sign-in requests, sign-up and password reset, including mail resends. Each protected form
sends `cf-turnstile-response`. The server calls Cloudflare siteverify with the token,
secret and remote IP. Missing, invalid and unavailable verification fails closed with a
retry message. The first password step that only asks for an email does not authenticate.

Supply `TS_AUTH_TURNSTILE_SITE_KEY` and `TS_AUTH_TURNSTILE_SECRET_KEY`. Production mounts
OCI Vault secrets through their `_FILE` companions, using the existing ts-auth secret-file
loader. Suggested vault names are `ts-auth-turnstile-site-key` and
`ts-auth-turnstile-secret-key`. The site key is public; the secret must never enter
HTML or logs. Saving settings without both keys writes `bot_challenge=false`, even when activation was requested; the console explains why.
Successful verification must match the hosted origin hostname, the form action (`sign_in`, `sign_up` or `reset`) and the project id in `cdata`.

The local journey uses Cloudflare's published always-pass keys:
site key `1x00000000000000000000AA` and secret key
`1x0000000000000000000000000000000AA`. Its isolated network stands in for Cloudflare;
production has no stand-in.

## Breached passwords

Breached-password refusal defaults on. Hosted sign-up and password reset validate the
existing 10–256-character/common-password policy before checking Have I Been Pwned.
Password replacement uses the hosted reset flow.

Only the first five uppercase hexadecimal SHA-1 characters leave the server, in the HIBP
range request, with `Add-Padding: true`. The remaining 35 characters are compared locally
against positive counts; padding rows with zero counts do not cause refusal. SHA-1 is
required by HIBP's lookup protocol; credential storage remains Argon2id.

HTTP failure or timeout logs a warning without the password, digest or prefix and retains
the existing common-password policy. This client's HTTP logging is disabled and HIBP
requests are excluded from exported HTTP traces.

## Account lockout

Account lockout defaults on. Ten consecutive failed password attempts lock password
sign-in for 30 minutes. A successful attempt resets the counter. Even a correct password
is refused during the lock, using the same response as an unknown account. IP limits
continue to apply. When lockout is off, the per-email password budget still applies.

The existing `lockout` email includes an unlock link. Its ULID token is stored only as a
SHA-256 digest and expires with the lock. `GET /v1/{project}/attack-protection/unlock`
shows a confirmation page; a same-origin form POST to that route redeems the link.
Its generic response reveals neither account existence nor token validity.
The database version check clears the lock and digest atomically, so a reused link cannot
unlock a later lock. Password attempts use the same concurrency protection and fail closed
after three failed optimistic-concurrency attempts.

## New-device notices

New-device email defaults off for new and existing projects until the owner enables it.
It uses the existing `new_device` template. Successful hosted sign-ins record the hash
of a random HttpOnly Secure device cookie per project and user. A device absent for 90 days generates a notice
with the browser, IP and sign-in time. Subsequent sign-ins refresh its last-seen time.
The cookie lasts 90 days and is isolated by project. Passwords, bearer credentials and
raw device cookies are not stored in device history.

Sign-up records the first device without a notice. The background pruner removes device
history and password-attempt records older than 90 days. Existing projects explicitly
retain breach checks and lockout on, with bot challenge and new-device notices off.
