# Native database lifecycle control

Production public branch creation uses the durable provisioning authority. The
request to `POST /api/v1/databases/{parent}/branches` includes `name` and a stable
`operation_id`; the parent must be an admitted, accessible canonical database.
The authority allocates the child's database and key identities and inherits the
parent's placement and residency. HTTP 202 reports accepted provisioning, never
readiness or a completed snapshot restore. Admission records retain
`parent_database_id`. Child storage must pass the separate authenticated restore
and activation gates before serving.

Production `DELETE /api/v1/databases/{database}` requires `Idempotency-Key` using
letters, digits, `_` or `-`. Native scope checks precede the authority's live
owner authorization. The original bearer passes only in transient authenticated
transport headers; it is not stored in the deletion operation. The response is
HTTP 202 and points `Location` at
`/api/v1/databases/{database}/deletions/{operation}`. This authenticated status
route reads the durable authoritative operation after local data access is
revoked. States are `preparing`, `stopping`, and `cleanup_pending`.

The pinned-UID native admission socket serves two deletion commands:

- `POST /internal/v1/databases/delete/prepare`: exact environment, organization,
  project, database, optional parent, operation, region, and member identities.
  The member checks deletion protection, durably binds the preparation, closes
  its origin epoch, and retires local reactive views. It returns the same tuple,
  `prepared:true`, and its minimum `last_accounted_at` cutoff.
- `POST /internal/v1/databases/stop`: the same tuple plus authority-selected
  common `cutoff` and optional `retain_until`. The member persists the immutable
  deletion intent and stops customer storage/write charging. It returns the
  same tuple with local `access_revoked:true` and `usage_stopped:true` only after
  its native implementation succeeds.

Cutoffs are RFC3339 timestamps with exact millisecond precision. The authority
must prepare every serving replica and drain inbound replication before choosing
its common cutoff. A stale cutoff is refused; billing history is not rewritten.
Protection changes cannot race an accepted preparation. Retry uses the original
operation and immutable boundaries. Missing callbacks refuse the private calls.

An absent retention boundary grants no physical destruction permission. Logical
access and usage can stop while cleanup remains pending. These receipts do not
claim backup removal, key destruction, CDN cache purge, or completion on a
sibling member. The regional/global workflow must collect those actual effects
separately. Native view revocation cannot retract already delivered bytes or
immutable bytes already cached by a CDN; delivery-layer revocation/purge policy
must supply its own evidence and propagation bound.
