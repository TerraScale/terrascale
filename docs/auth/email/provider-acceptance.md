# Email provider acceptance

All ts-auth email flows share two provider boundaries. The primary is OCI
Email Delivery over SMTP (`smtp.email.sa-saopaulo-1.oci.oraclecloud.com`,
port 587) reached with STARTTLS and the tenant's SMTP credentials. The
fallback is Cloudflare Email Sending
(`POST /accounts/{account_id}/email/sending/send`) over HTTPS with the
account's token. The From address is fixed in code:
`no-reply@terrascale.tech`, with the display name and Reply-To of the rendered
message. One admission is spent per message, not per provider.

The SMTP sender classifies the provider's answer from the SMTP reply: a 2xx
after DATA is Accepted. A 4xx reply code on any command is Retryable: nothing
was accepted for delivery, so the outbox may try again later. A 5xx reply code
is Rejected: the provider permanently refused this message. Connect,
authentication, protocol, I/O and timeout failures are Unknown, and must not be
automatically retried, because the message may already be in transit.

The Cloudflare sender accepts only when the response confirms this recipient:
HTTP 2xx with `success` explicitly `true`, `result` an object, and the intended
address exactly in a `result.queued` or `result.delivered` string array.
Explicit `success: false`, or a nonempty `permanent_bounces` or
`suppressed_recipients` list, is Rejected. HTTP 429 is Retryable with
`Retry-After`; other 4xx are Rejected; invalid JSON, duplicate properties,
malformed lists, confirmation of another recipient and transport failures are
Unknown.

Failover: any non-Accepted primary outcome (Retryable, Rejected or Unknown)
tries Cloudflare for the same message; a duplicate code email is better than
none. If every configured provider returns Retryable, the message pauses
admission with the latest provider Reset. Caller cancellation stops failover.

The private, authenticated `POST /v1/{slug}/email/test` API returns 202 only
for Accepted. Rejected returns 502 with `error.code = email_rejected`; Unknown
returns 502 with `error.code = email_acceptance_unknown`. Existing 429 responses
with `Retry-After` cover admission and explicit provider rate limits. This is an
intentional failure-status compatibility change: callers must handle 502 and
must not automatically retry Unknown, because provider idempotency is unproven.
The served OpenAPI contract describes the 502 `ErrorBody` response.

Public signup, sign-in and recovery responses retain their anti-enumeration
behavior. Allowance, alert and support ledgers retain Rejected or Unknown and
do not resend those recorded attempts. Logs retain fixed outcome labels and a
recipient digest, without provider bodies or plaintext addresses.

Outside Development, startup requires both provider sets:
`TS_AUTH_SMTP_HOST`, `TS_AUTH_SMTP_USERNAME` and `TS_AUTH_SMTP_PASSWORD`
(`TS_AUTH_SMTP_PORT` is optional, 587) together with `CLOUDFLARE_ACCOUNT_ID`
and `CLOUDFLARE_EMAIL_TOKEN`, supplied through OCI Vault and ts-orch as
`<NAME>_FILE` mounts. A missing fallback must fail the deploy loudly, not the
next outage. Development keeps the logging sender, which never confirms
provider acceptance.

Acceptance does not prove inbox delivery. This change adds no durable queue or
provider retry. [Queue admission](queue-admission.md) refuses known-full or
stopping authentication queues before accepting work. A process crash or an
expired shutdown timeout can still lose pending work.
The live delivery check (`.github/workflows/auth-mail-delivery.yml`) signs up a
real account into a disposable inbox daily and is provider-neutral.
