# Collection change webhooks

This is the producer contract for SDK and dashboard integrations. All routes use
`/v1/projects/{project}/databases/{database}/collections/{collection}/webhooks`.
`project` and webhook/delivery IDs are canonical uppercase ULIDs; `database` and
`collection` are lowercase names. Database identity is resolved from the signed
project token, never supplied organization identity. Other project/database routes
return 404. These are public client-listener routes using a ts-auth project access
token (`Authorization: Bearer …`), with the scopes below.

## Routes and JSON

- `POST` base route, scopes `documents:read` **and** `documents:write`, body `{"url":"https://receiver.example/events"}`.
  Returns 201 with `{"id":"<ULID>","url":"https://receiver.example/events","createdAt":"<RFC3339>","status":"active","secret":"<base64url>"}`
  and Location of the webhook, with `Cache-Control: no-store`. The generated 32-byte signing secret appears **only**
  in this response. Store it now; deletion and recreation are required if lost.
- `GET` base route, scope `documents:read`. Returns 200 with a direct array of
  `{"id":"<ULID>","url":"https://receiver.example/events","createdAt":"<RFC3339>","status":"active"}`.
  `status` is `active` or `feed_gap`. Never includes a secret.
- `DELETE /{webhook}`, scopes `documents:read` **and** `documents:write`. Returns 204; unknown webhook 404.
  Stops future attempts; an already in-flight request may still complete.
- `GET /{webhook}/deliveries`, scope `documents:read`. Returns 200 with a direct array
  of `{"id":"<ULID>","createdAt":"<RFC3339>","status":"pending","attempts":1,"lastAttemptAt":"<RFC3339 or null>","nextAttemptAt":"<RFC3339 or null>","httpStatus":503,"errorCode":"http_error"}`.
  Status is `pending` or `delivered`; nullable fields are JSON null when absent.
  `errorCode` is null, `http_error`, `transport_error`, or `target_rejected`.
  Receiver bodies, exception text, signing headers and secrets are never logged.

Both list routes accept `after=<ULID>` and `limit=1..100` (default 50), return IDs
in ascending order, and return a direct array. Continue with the final item's ID;
an empty array ends pagination. Only the latest 100 delivery records per webhook
are retained, including a pending record. There are at most 32 webhooks per
collection. Common errors: 400 invalid request/URL/pagination, 401 missing or
invalid token, 403 missing scope, 404 unknown database/collection/webhook or scope
mismatch, 409 webhook limit, 503 storage temporarily unavailable. Error bodies use
the standard TerraBase `{error:{code,message}}` shape. Create requires an existing collection.

## Receiver protocol

Each request is HTTPS POST, `Content-Type: application/json`. The UTF-8 body is:

```json
{"id":"<delivery ULID>","webhookId":"<ULID>","projectId":"<ULID>","database":"orders","collection":"items","events":[{"origin":"<opaque origin>","version":"<opaque version>","id":"document-key","deleted":false}]}
```

Events contain change identities, **not document bodies**. Fetch current state
through the document API when needed; an intermediate value may already have
changed. Deletions set `deleted:true`. Collection/index changes and collection
drops do not emit document events. Each bounded batch contains whole source
transactions (up to 1024 events, at most 512 KiB encoded); delivery order follows the
local durable feed, with no global order across origins.

Headers:

- `X-TerraScale-Delivery: <delivery ULID>` (stable across retries).
- `X-TerraScale-Timestamp: <Unix seconds>` (fresh for each attempt).
- `X-TerraScale-Signature: v1=<lowercase hex HMAC-SHA256>`.

Decode the creation secret as base64url bytes. Sign the exact bytes
`<timestamp>.<raw request body>` using that key. Compare in constant time, enforce
a timestamp tolerance (recommended 5 minutes), and deduplicate by delivery ID
before applying effects. Feed replay may also repeat event identities: deduplicate
`(origin, version, collection, id)` where necessary. No signature covers reserialized JSON.

Any 2xx acknowledges the batch. Every other status, DNS/TLS/connect error, timeout,
or unsafe DNS answer retries the **same persisted body and delivery ID** with
backoff of 1, 2, 4, … seconds, capped at 1 hour, without a retry count limit.
Requests time out after 10 seconds. Redirects and proxies are disabled. A single
webhook has at most one pending batch and one in-flight attempt. Different hooks
are independent. Delivery records, body, retry schedule and feed cursor are
TerraBase documents; recording the batch and advancing its cursor is one durable
transaction before sending. A crash after receiver success but before recording
acknowledgment causes a duplicate, as required by at-least-once delivery.

## Retention, restart and network boundaries

Registration starts at the local feed head; it does not backfill existing data.
The creating machine owns delivery and resumes after restart with its stable node
identity. There is no automatic ownership transfer to a different machine.
The engine retains local change history for 7 days. If downtime/backlog exceeds
retention, a prefix gap is detected, a source transaction exceeds the conservative 512 KiB encoded batch bound, or the database/collection is recreated, the
webhook enters `feed_gap` and stops scanning. Already captured pending batches
still retry. This state is visible in list results; no silent cursor reset or
claim of lossless delivery beyond retained history. Reconcile current data, delete
the webhook and register a new one to resume from the current head.

Only HTTPS URLs on port 443, with no credentials or fragment, and at most 2048
characters are accepted. Destinations must resolve entirely to public IPv4/IPv6
unicast addresses. Loopback, private, unspecified, multicast, reserved,
link-local/metadata, IPv4-mapped private IPv6 and transition address ranges are
rejected. Every connection resolves and validates all DNS answers, then connects
directly to a validated IP while preserving normal hostname TLS verification;
DNS cannot change the connected address between validation and connection.
