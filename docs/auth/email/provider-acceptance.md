# Email provider acceptance

All ts-auth email flows share one SMTP provider boundary: OCI Email Delivery
(`smtp.email.sa-saopaulo-1.oci.oraclecloud.com`, port 587) reached with
STARTTLS and the tenant's SMTP credentials. The From address is fixed in code:
`no-reply@terrascale.tech`, with the display name and Reply-To of the rendered
message.

The sender classifies the provider's answer from the SMTP reply: a 2xx after
DATA is Accepted. A 4xx reply code on any command is Retryable: nothing was
accepted for delivery, so the outbox may try again later. A 5xx reply code is
Rejected: the provider permanently refused this message. Connect, authentication,
protocol, I/O and timeout failures are Unknown, and must not be automatically
retried, because the message may already be in transit.

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

Outside Development, startup requires `TS_AUTH_SMTP_HOST`,
`TS_AUTH_SMTP_USERNAME` and `TS_AUTH_SMTP_PASSWORD` together, supplied through
OCI Vault and ts-orch; `TS_AUTH_SMTP_PORT` is optional (587). Missing email
configuration fails before the service starts accepting traffic. Development
keeps the logging sender, which never confirms provider acceptance.

Acceptance does not prove inbox delivery. This change adds no durable queue or
provider retry. [Queue admission](queue-admission.md) refuses known-full or
stopping authentication queues before accepting work. A process crash or an
expired shutdown timeout can still lose pending work.
The live delivery check (`.github/workflows/auth-mail-delivery.yml`) signs up a
real account into a disposable inbox daily and is provider-neutral.
