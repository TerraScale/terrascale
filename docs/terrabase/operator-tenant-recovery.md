# Authenticated tenant recovery retry

Use `POST /v1/management/organizations/{organization}/projects/{project}/databases/{database}/recovery/retry`
on the affected node's **internal HTTP/2 listener**. Every route component is the
canonical catalog ULID, including the database ID rather than its name.

The authorized operator is the already authorized platform service holding the
existing peer credential, acting as one existing owner or admin of the target
organization through `X-TerraBase-User-Id`. The shared peer credential identifies
the service; the audit's operator ID is its asserted acting user, not proof of an
individual human. An incident record must link the service invocation to the
human approving it. Network location, a public bearer token, ordinary membership
and another tenant's membership confer no recovery authority. This operation
creates no credential, scope, membership, permission or persistent grant.

The operation only retries an **existing durable certified tenant rebuild** for
the supplied `faultedIncarnation`. It cannot create a fault, choose a historical
snapshot, register a deleted database, rebuild `_system`, clear a fault, change an
anchor or reset catalog freshness. Catalog gate `open` and `trusted` must both be
true. Existing anti-entropy proofs must be younger than 30 seconds for every
listed peer and other recorded region; checked before authorization reads and
again immediately before dispatch. Missing or stale evidence refuses recovery.

## Recovery procedure

1. Record the approved incident, acting owner/admin ID, node, organization,
   project, database ID and faulted incarnation from existing authorized
   diagnostics. Confirm the exact database is still live and placed on that
   node. If identity, target or incarnation cannot be established, stop.
2. Inspect `/v1/catalog-gate` through the existing authorized administrative
   channel. If untrusted/closed, repair the underlying discovery/peer/anchor
   issue through reviewed infrastructure recovery. This endpoint cannot recover
   stale SYSTEM state or make a restored peer trustworthy. Do not delete or
   replace anchors, fault records, side files or freshness evidence.
3. Submit the request below once with an approved incident reference as reason.
   Supply the existing peer header through the approved credential-delivery
   workflow, using a protected header file with curl's `--header @<path>` and
   `--http2-prior-knowledge`. Keep credentials out of arguments, shell history,
   terminal output and incident records; do not provision or copy credentials.
4. Record the HTTP outcome and sanitized `Tenant recovery:` audit events. `204`
   means the existing engine certified the replacement; verify normal reads,
   retained deletions, revoked-key denial and replication for that exact target.
   `409 recovery_pending` retains the refusal and side copy; repair the intact
   source or integrity failure before a deliberate retry. `409 recovery_busy`
   means another host rebuild runs. `409 no_matching_rebuild` means no pending
   record matches this incarnation, including a completed request replay.
5. If a retry fails or is interrupted, leave its durable record and original
   side files intact. Existing background recovery can resume. Never force
   certification, restore old catalog files, reissue revoked keys or revive
   deleted databases. Any additional access or infrastructure action needs its
   own approval.

```json
{
  "faultedIncarnation": "<canonical ULID of the pending fault>",
  "reason": "<approved incident reference, 1 to 256 characters without controls>"
}
```

The engine still performs pinned intact-source refill, verified salvage and the
FULL completion barrier. It refuses ambiguous collected history and preserves
the original side copy until certified. Retained deletion state remains in force;
this is not the snapshot restore API, whose purpose is historical data recovery.

## Audit, revocation and rollback

Started and terminal application logs contain canonical acting user and tenant
IDs, fixed action `retry_certified_rebuild`, a fixed outcome and SHA-256 of the
trimmed reason. Store the full reason only in the authorized incident record and
compare its hash when auditing. Free-form reasons, peer diagnostics, document
contents, credentials and filesystem paths are not included in these events.
Requests refused by listener/authentication/catalog middleware do not reach this
audit; retain the existing authentication telemetry as well.

Each request authenticates again and rechecks current membership/freshness. No
recovery credential is issued, so there is no recovery token expiry or replay
cache. Repeated pending requests resume the same pinned rebuild; a completed
request cannot start another one. Revoking existing operator access or membership
uses its existing approved process and stops subsequent requests, not an already
admitted engine operation. In-flight cancellation leaves durable recovery work;
it is not an undo operation. Rollback preserves the pending record/side files and
repairs the underlying cause; it never copies the old side file over a certified
replacement or restores revoked authority. The endpoint grants no permission to
mutate production during a code change.

## Evidence boundary

On 2026-10-23, audited `origin/main` was
`49e32e99ca20c94e581bd080f61f3ca7668e715b`; it had automatic certified recovery
and a fail-closed catalog gate but no explicit authenticated retry endpoint.
Read-only public `https://api.terrascale.tech/readyz` returned 200 for `gru`,
revision `888ea413c06f6452cccceab57c735fa35b4d850d`. Readiness does not prove an
authenticated production recovery. No production recovery was attempted.

Fast integration coverage exercises real listener authorization, exact tenant
and deleted-target denial, bounded reasons and sanitized logs, stale/expiring
proofs, stale restored catalogs with revoked keys, and real peer-certified retry
with retained deletion and completed-request replay. CI results must be attached
to the parent PR; source coverage alone is not a passing result or live proof.
