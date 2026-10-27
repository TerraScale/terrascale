# Email provider acceptance

All ts-auth email flows share Cloudflare Email Sending's
`POST /accounts/{account_id}/email/sending/send` provider boundary.
An HTTP 2xx response confirms acceptance only when `success` is explicitly
`true`, `result` is an object, and the intended address appears exactly in a
`result.queued` or `result.delivered` string array. Present recipient lists must
be arrays of strings. A nonempty `permanent_bounces` or `suppressed_recipients`
list, or explicit `success: false`, is Rejected. Invalid JSON, null, missing
confirmation, duplicate properties, malformed lists, and confirmation of another
recipient are Unknown. Neither state counts as Accepted.

The response fields match the ts-orchestrator relay's acceptance checks in
[`internal/alertdelivery/relay.go`](https://github.com/TerraScale/ts-orchestrator/blob/7840006579fa2c8bddabdf70c5e41e6581ba91f4/internal/alertdelivery/relay.go).
The installed `cf` command schema identifies the structured send route and
request fields; it does not include response schemas.

The private, authenticated `POST /v1/{slug}/email/test` API now returns 202 only
for Accepted. Rejected returns 502 with `error.code = email_rejected`; Unknown
returns 502 with `error.code = email_acceptance_unknown`. Existing 429 responses
with `Retry-After` cover admission and explicit provider rate limits. This is an
intentional failure-status compatibility change: callers must handle 502 and
must not automatically retry Unknown, because provider idempotency is unproven.
The served OpenAPI contract documents the 502 `ErrorBody` response.

Public signup, sign-in and recovery responses retain their anti-enumeration
behavior. Allowance, alert and support ledgers retain Rejected or Unknown and
do not resend those recorded attempts. Logs retain fixed outcome labels and a
recipient digest, without provider bodies or plaintext addresses.

Outside Development, startup requires both `CLOUDFLARE_ACCOUNT_ID` and
`CLOUDFLARE_EMAIL_TOKEN`, supplied through OCI Vault and ts-orch. Missing email
configuration fails before the service starts accepting traffic. Development
keeps the logging sender, which never confirms provider acceptance.

Acceptance does not prove inbox delivery. This change adds no durable queue or
provider retry. [Queue admission](queue-admission.md) refuses known-full or
stopping authentication queues before accepting work. A process crash or an
expired shutdown timeout can still lose pending work.
The cause of the original signup incident remains unproven.
