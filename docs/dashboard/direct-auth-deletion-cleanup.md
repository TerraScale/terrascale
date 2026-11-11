# Cleanup after direct auth deletion

The dashboard polls ts-auth's durable deletion outbox every 30 seconds through
TerraScale.DotNet 0.0.11, using its existing confidential Basic client identity.
No browser receipt or dashboard deletion operation is required. Only explicit
account, organization and project proofs authorize new cleanup. A 401, 404 or
outage never authorizes deletion. Older auth deployments without the outbox route
are retried; logs contain the exception type, without response bodies or secrets.
A failing entry is logged once per error with its proof ID, scope, subject ID and
the Stripe error type and code or the storage error code, never a message text,
and is counted in `terrascale_deletion_cleanup_failing_entries` on `/metrics`
until a pass cleans it. The browser deletion logs its own failed attempt to end an
organization's billing the same way.

Before any cleanup or ACK, the dashboard creates a record keyed by the
auth erasure ULID in `deletion_cleanup`. It retains the exact proof, and in
`deletion_cleanup_targets` the opaque project/customer references discovered
before removing their aggregates ([state storage](state-storage.md#deletions)).
A failed journal write prevents cleanup and ACK. Every poll replays retained
entries before listing new proofs, including after ACK and during auth outages.
The local replay of an entry has no TTL or retirement rule; only the Stripe
calls of an organization entry end, as described below. A failed entry does not
block the others.

An account proof erases that subject's profile and identity data, sessions,
credentials and personal access tokens. The `accounts` row keeps only its key and
`erasure_id`; this terminal marker blocks create-only writes and stale
conditional writes from an already-loaded session. The browser deletion
flow uses the same markers and preserves its preflight and consent requirements.
Account proofs leave shared organizations and every other user's records intact;
sole organizations are removed only under their own organization proofs.

Sessions, credentials and access tokens carry their subject in an indexed
`user_id` column, so every replay deletes them in SQL without requiring an active
auth grant. Pending MCP authorization codes go with their credentials, and running
`account_deletions` rows of the subject become completion-only rows. Anonymous MCP registrations, anonymous auth flows
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
organization metadata, including older or unpublished customer bindings. Such a
Checkout was reserved before billing was marked `deleting` or fenced, both of
which precede the ACK, and expires `StripeBilling.CheckoutLifetime` (one hour)
after its reservation. The entry records when auth acknowledged its proof
(`acknowledged_at`, written after the ACK). An organization entry without that
time, such as one recorded before the column existed, is acknowledged again
after its next successful replay, which can only make the time later. Once a
replay that started at least one hour after `acknowledged_at` has ended the
organization's billing, the entry records `billing_settled_at` and its later
replays make no Stripe call; an entry that is never acknowledged keeps calling
Stripe. Stripe search is eventually consistent, so that last replay relies on
Stripe having indexed each such Checkout's customer, created at least 30 minutes
before the Checkout expired. Only verified terminal results permit removing
local Checkout/customer mappings and replacing billing and organization roots
with minimal erasure markers. No subscription is canceled merely because the
auth endpoint is missing.

Organization cleanup removes projected projects, saved queries, alert rules with
their pending events, log drains with their cursors, billing change receipts,
allowance alerts and notifications scoped to that organization in surviving accounts.
The `internal_billing` row is removed only while it still belongs to the proven
organization. Retained replay cleans a
late rewrite and preserves a singleton allocated to a replacement organization.
Project proofs conditionally remove only the named project's projection and owned
records within its stated organization, including invitation reservations and
scoped notifications. The aggregate retains a permanent deleted project ID to
prevent stale hydration or mutation from restoring it. Ownership mismatches fail
closed, preserving unrelated projects, organizations and notifications.

ACK follows successful scoped cleanup and verification of terminal roots and
absent account indexes. Failed conditional writes remain pending; lost ACK
responses retry idempotently. Conditional SQL guards protect each cleanup against competing mutations. Journal and enrollment replay rechecks retained references; this implementation does not claim a finite global physical-erasure barrier. Operational
evidence at 2026-10-24 18:44:45 UTC places auth and dashboard in gru for the fresh
wipe, which does not establish such a barrier for every future serving topology.

The two new tables are initialized through the existing dashboard schema.
`account_deletions` now stores `{ Key, UserId, Payload }`, with its operation still
AES-GCM encrypted and authenticated to its natural key; cleanup verifies the
sealed subject before deleting it. This is a fresh-state storage shape change,
without an old-format reader or migration. No new secret or feature switch is
introduced. SDK package provenance and locked dependency graphs are committed
alongside the qualified package.
