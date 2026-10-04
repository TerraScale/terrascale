---
---
<!-- i18n:fallback:de -->
> **Übersetzung ausstehend.** Diese Seite wurde noch nicht auf Deutsch geprüft und zeigt den englischen Originalinhalt.

# Live queries

Subscribe to an authorized SQL result to receive its initial contents and
later changes. A bounded query such as “the latest 20 posts” keeps the result
manageable. Collection sync provides an authorized outer subset that the
TanStack adapter can filter locally. No durable offline writes or full local
replica ships at launch.

## Delivery and access

HTTP delivery is the default; WebSocket is optional. Subscribers with the
same authorized query can share immutable responses. A subscription returns
its view identifier, snapshot and log URLs, an opaque checkpoint, and a
short-lived view capability. The SDK sends it in the canonical
`X-TerraScale-Capability` request header; never place credentials in URLs or
publish them. The capability is issued in ten-minute buckets and is accepted
for at most 1,200 seconds from issuance while its view, generation, Region,
and authorization scope remain valid. Attach a fresh view after it expires.

Use the SDK’s result view to apply ordered changes. Do not infer write success
from an optimistic local update: wait for the document API acknowledgement.
See the [quickstart](/de/getting-started/) for document writes and live rendering.

## Resume and resets

A checkpoint resumes retained changes when its view and authorization remain
valid. Treat its bytes as opaque. Retention expiry or a change to schema,
access rules, or branch state may require a typed reset and a fresh authorized
snapshot. Let the SDK replace the old view; do not append a new snapshot onto
stale rows. Two different views do not promise a mutually consistent cut.

Close subscriptions when their component or request ends. Reconnect and
error handling must respect permission revocation and the current scope.
Revoking an API key stops new authenticated API operations and future view
attachments; an already issued reactive capability can remain usable until
its expiry (at most 1,200 seconds), subject to the service's authorization
checks. A disconnected client does not gain permission to keep reading new
changes.

## Billing and limits

Fan-out to subscribers does not multiply a write: authorization-equivalent
subscribers share one view. Shared-view and connection guards are disclosed
safety limits; a guard never adds a charge. Reads and writes consume
operation units, and logical storage counts in every selected Region, under
the organization’s plan. See
[pricing and limits](/de/pricing/).
