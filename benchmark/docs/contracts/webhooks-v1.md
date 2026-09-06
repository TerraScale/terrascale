# Webhook signing and secret lifecycle

The authenticated database API serves webhook creation, listing, deletion,
delivery history, replay, and signing-secret rotation. Database-scoped read
permission admits listing and delivery history; database-scoped admin permission
admits mutations. A collection-scoped credential cannot administer a webhook
that reads the database change stream.

`POST /api/v1/databases/{name}/webhooks` accepts `{"endpoint":"https://…"}`.
Its response contains `id`, `endpoint`, `created_at`, `deleted`, and `secret`.
Decode `secret` as unpadded base64url to obtain the 32-byte signing key. Creation
and rotation responses carry `Cache-Control: no-store`. Lists and delivery logs
never include signing material.

An optional `Idempotency-Key` creation header must contain 1–128 bytes and no
control characters. A retry under the same database scope and key returns the
same endpoint and original signing secret. Reusing the key with another endpoint
URL, or after deleting its endpoint, returns `409 webhook_conflict`. Without an
idempotency key, a client must not automatically repeat an ambiguous creation.
An authorized retry can recover the original creation secret after rotation;
that does not extend its delivery-signing overlap window.

`POST /api/v1/databases/{name}/webhooks/{id}/rotate-secret` accepts
`{"overlap_seconds":300}`. The default is 300 seconds; the permitted range is
0–3600. Its response has the same shape as creation and discloses the new key.
Rotation is not idempotent: do not automatically retry an ambiguous rotation.
Only the immediately preceding key participates in overlap; another rotation
replaces that preceding key. At the deadline, the old signature stops appearing.
A zero-second window switches immediately.

The request body is a versioned MessagePack map with `version: 1`, `sequence`,
`write_index`, `operation_id`, `collection`, `document_id`, `mutation` (`put` or
`delete`), and `value`. A delete carries a null value. Retained revisions keep
all overwritten values and deletes; a paging checkpoint includes the write
index so a large commit can span bounded pages without losing events.

Deliveries carry these headers:

- `x-terrascale-webhook-id`: endpoint identity.
- `x-terrascale-webhook-event`: retained publication identity.
- `x-terrascale-webhook-timestamp`: Unix seconds as decimal text.
- `x-terrascale-webhook-signature`: `v1=<hex MAC>`, or
  `v1=<current MAC>,v1=<previous MAC>` during overlap.
- `idempotency-key`: stable endpoint/publication delivery identity.

Compute `key = BLAKE3(decoded_secret)`, then compute keyed BLAKE3 with that
32-byte key over the timestamp header bytes, one ASCII period, and the exact
request-body bytes. Compare the resulting lowercase hexadecimal MAC using a
constant-time comparison against a supported `v1` value. Receivers should reject
stale timestamps and deduplicate the stable delivery identity before producing
side effects. A timeout can cause redelivery after the receiver has committed
its side effect; delivery is at least once.

Signing keys, their original creation recovery copy, and the bounded previous
key are stored only as encrypted metadata. Every decrypt or rotation requires a
fresh authorized database-key lease in an admitted Region. Endpoint deletion
stops delivery before destroying the stored signing-key record. If key deletion
fails, deletion responds with an error and can be retried.

Webhook metadata and dispatch checkpoints use private SQLite records rather
than customer document writes. The dispatcher consumes the retained revision
stream, checkpoints complete pages, and recovers due deliveries after restart.
Local tests do not qualify cross-Region metadata replication, dispatcher
ownership, real OCI authorization, or public release readiness.
