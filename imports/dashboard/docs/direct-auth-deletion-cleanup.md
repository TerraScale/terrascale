# Cleanup after direct auth deletion

The dashboard polls ts-auth's durable deletion outbox every 30 seconds through
TerraScale.DotNet 0.0.11, using its existing confidential Basic client identity.
No browser receipt or dashboard deletion operation is required. Only explicit
account, organization and project proofs authorize new cleanup. A 401, 404 or
outage never authorizes deletion. Older auth deployments without the outbox route
are retried; logs contain the exception type, without response bodies or secrets.

Before any cleanup or ACK, the dashboard creates an immutable record keyed by the
auth erasure ULID in `dashboard_deletion_cleanup`. It retains the exact proof and
opaque project/customer references discovered before removing their aggregates.
A failed journal write prevents cleanup and ACK. Every poll replays retained
entries before contacting auth, including after ACK and during auth outages.
Entries have no TTL or retirement rule. A failed entry does not block the others.

An account proof erases that subject's profile and identity data, sessions,
credentials, personal access tokens and indexes. Account and identity keys retain
only `{ ErasureId }`. These same-key terminal markers block create-only writes
and stale conditional writes from an already-loaded session. The browser deletion
flow uses the same markers and preserves its preflight and consent requirements.
Account proofs leave shared organizations and every other user's records intact;
sole organizations are removed only under their own organization proofs.

Every session, credential, session-index and PAT enrollment first writes a minimal
immutable reference to `dashboard_account_enrollments`. References contain only
subject, collection and record IDs and remain indefinitely. Replay follows these
references even after mutable indexes disappear. It decrypts sessions and
credentials to validate ownership without requiring an active auth grant. Pending
MCP authorization codes and their owned credentials, and encrypted account deletion
operations, are also removed. Anonymous MCP registrations, anonymous auth flows
and email-only audit entries are not inferred to belong to a deleted account.

An organization proof first moves Stripe billing into a terminal `deleting`
state. The live, consented sole-owner account deletion preflight uses the same
terminalization before creating its dashboard operation or auth intent. It can
resolve a reservation whose Checkout ID never persisted, rather than returning
502 forever. Provider failures retain the deleting binding, reservation identity
and customer references for an authorized retry. Confirmed live-owner
organization deletion uses this recovery after recording its deletion mark, so
an interrupted Checkout remains recoverable even while ordinary billing changes
are denied. Normal billing writes cannot
publish into this state. Cleanup reads
canonical Checkouts even when their subscription IDs were never saved locally.
It expires open Checkouts and immediately cancels completed subscriptions without
a refund, verifying organization metadata and customer identity. Cancellation or
conditional-write failures retain the proof and relevant references for retry.

A Checkout can reach Stripe after ACK. Retained replay lists Checkouts and
subscriptions for retained customers and searches Stripe customers by exact
organization metadata, including older or unpublished customer bindings. Stripe
search is eventually consistent, so this is continuing repair rather than proof
that an external operation can never appear later. Only verified terminal results
permit removing local Checkout/customer mappings and replacing billing and
organization roots with minimal erasure markers. No subscription is canceled
merely because the auth endpoint is missing.

Organization cleanup removes projected projects, saved queries, alert rules, log
drains, their exact global-index entries, billing change receipts, allowance-alert
ledgers and notifications scoped to that organization in surviving accounts.
The `dashboard_internal_billing/organization` singleton is conditionally removed
only while it still belongs to the proven organization. Retained replay cleans a
late rewrite and preserves a singleton allocated to a replacement organization.
Project proofs conditionally remove only the named project's projection and owned
records within its stated organization, including invitation reservations and
scoped notifications. The aggregate retains a permanent deleted project ID to
prevent stale hydration or mutation from restoring it. Ownership mismatches fail
closed, preserving unrelated projects, organizations and notifications.

ACK follows successful scoped cleanup and verification of terminal roots and
absent account indexes. Failed conditional writes remain pending; lost ACK
responses retry idempotently. Local CAS fences cannot exclude later regional LWW
joins. Indefinite journal and enrollment replay repairs those late values; this
implementation does not claim a finite global deletion barrier. Operational
evidence at 2026-10-24 18:44:45 UTC places auth and dashboard in gru for the fresh
wipe, which does not establish such a barrier for every future serving topology.

The two new collections are initialized through the existing dashboard schema.
`account_deletions` now stores `{ Key, UserId, Payload }`, with its operation still
AES-GCM encrypted and authenticated to its natural key; cleanup verifies the
sealed subject before deleting it. This is a fresh-state storage shape change,
without an old-format reader or migration. No new secret or feature switch is
introduced. SDK package provenance and locked dependency graphs are committed
alongside the qualified package.
