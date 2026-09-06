---
---
<!-- i18n:fallback:ar -->
> **الترجمة قيد المراجعة.** لا تتوفر بعد مراجعة بشرية لهذه الصفحة بالعربية، لذلك يظهر المحتوى الأصلي بالإنجليزية.

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
short-lived view capability. The SDK sends the capability in a request header;
never place credentials in URLs or publish them.

Use the SDK’s result view to apply ordered changes. Do not infer write success
from an optimistic local update: wait for the document API acknowledgement.
See the [quickstart](/ar/getting-started/) for document writes and live rendering.

## Resume and resets

A checkpoint resumes retained changes when its view and authorization remain
valid. Treat its bytes as opaque. Retention expiry or a change to schema,
access rules, or branch state may require a typed reset and a fresh authorized
snapshot. Let the SDK replace the old view; do not append a new snapshot onto
stale rows. Two different views do not promise a mutually consistent cut.

Close subscriptions when their component or request ends. Reconnect and
error handling must respect permission revocation and the current scope.
A disconnected client does not gain permission to keep reading new changes.

## Billing and limits

Reactive delivery, subscribers, catch-up, replay, and WebSocket delivery are
unbilled. Shared-view and connection guards are disclosed safety limits;
a guard never adds a charge. Writes and logical storage are billed under the
organization’s plan, including selected Region multiplication. See
[pricing and limits](/ar/pricing/).
