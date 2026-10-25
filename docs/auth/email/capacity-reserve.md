# Shared transactional email capacity

The Cloudflare account has finite capacity: admission is not an inbox-delivery guarantee.
All ts-auth mail and the ts-orch alert relay reserve an attempt in `email_admission`
before contacting Cloudflare. Dashboard mail already uses ts-auth. Deployment must
install both consumers before protection covers every sender; previously spent
provider quota cannot be recovered by a new ledger.

A single conditional-write record on GRU retains at most 1,000 reservations for 25
hours. This conservative window includes provider request/grant duration and avoids
assuming a midnight reset. It stays below TerraBase's 1 MiB document limit. Every
instance shares it; production Cloudflare delivery refuses startup outside GRU,
matching the current GRU-only auth decision. Regional serving requires a different
coordination design. Storage errors fail closed. No process-local lock is authority.

Non-authentication mail can spend at most 500 attempts, each purpose at most 100,
template tests at most 20, and each noncritical tenant at most 50. The remaining 500
are reserved for sign-in, verification, recovery and MFA. Invitations, lockout notices,
alerts, allowance and support messages are noncritical. Classification and tenant
identity come from server code; rendering a sign-in template for a test never grants
critical priority. Refusals return retryable; test sends return 429. Background
invitations/notices are dropped on refusal, counted in metrics, with no automatic resend or stored body.

Reservations are never refunded, including ambiguous provider acceptance. Existing
product delivery receipts prevent accepted/unknown retries. A provider 429 records
`ts_auth_emails_total{outcome="provider_exhausted"}` and a durable pause until
Retry-After (bounded to 25 hours); absent a reset hint, wait one minute before allowing another attempt.
Authentication responses stay independent of account existence and mail outcomes.
`ts_auth_email_admission_total{purpose,outcome}` counts admission/refusal, while
`ts_auth_email_remaining{pool="total|auth_reserve"}` exposes the latest observed
remaining capacity on each instance. Gauges update on admission checks; add across
neither replicas nor time. Tenant IDs never become metric labels.

The relay calls `POST /v1/terrascale/email/admission` over HTTPS with a fresh ULID
`attemptId`. Its bearer credential is lowercase-hex HMAC-SHA256 using the existing
Cloudflare token as key and UTF-8 `terrascale:email-admission:v1:ts-alerts` as input.
It authorizes only infrastructure alert admission, no recipient, content or priority.
A 200 `{"status":"admitted","sendBy":"..."}` grants one attempt within ten seconds;
duplicate IDs, timeouts, expired grants and other responses suppress email, preserving
push. IDs must be no older than five minutes; receipts outlive this replay window.
The relay retains its durable hourly cap and deduplication. A holder of the vendor
token still has direct provider authority: this protects cooperating product senders,
not a compromised provider credential. Rotate the Cloudflare token on both consumers
together. Never log either credential. A malformed ledger stops all mail; recovery
requires reviewed repair preserving live reservations, never deletion to reset quota.
