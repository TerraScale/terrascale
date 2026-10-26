---
title: Errors and safe retries
---

<!-- i18n:fallback:pt-br -->
> **Tradução pendente.** Esta página ainda não passou por revisão humana em português e mostra o conteúdo original em inglês.

<div lang="en" dir="ltr">

# Errors and safe retries

Decide recovery from the typed status/code and whether an operation may have committed. A timeout does not prove a write failed.

## Handle typed errors

JavaScript exposes `TerraBaseV1Error`, `V1PreconditionFailedError`, `V1InvalidKeyError`, `V1ForbiddenScopeError` and `V1NotFoundError`. The base error has `status`, `code` and, where supplied, `retryAfter` (seconds) and `op` (a transaction operation index).

```js
import { TerraBaseV1Error, V1PreconditionFailedError } from '@terrascale/sdk'

try {
  await posts.patch(id, { merge: { title: 'Updated' } }, { ifMatch: version, signal })
} catch (error) {
  if (error instanceof V1PreconditionFailedError) {
    // Refetch and resolve the newer value before making another write.
  } else if (error instanceof TerraBaseV1Error) {
    // Classify error.status and error.code; keep credentials and bodies out of logs.
  } else {
    throw error // A transport failure can leave a mutation's outcome uncertain.
  }
}
```

The snippet assumes a collection handle, ID, observed version and deadline signal. It shows classification; the application must implement its own user-facing recovery.

| Response | Recovery |
| --- | --- |
| `400` / `413` / `415` | Correct the filter, payload, size or media type. A document id that is not a canonical ULID answers `400 invalid_document_id`. |
| `401` / `403` | Obtain valid authorized access; do not auto-retry |
| `404` | Check the selected resource; absence differs from denied access |
| `409` / `412` | Resolve the deterministic conflict or stale version |
| `421 wrong_region` / `421 misdirected_request` | Check placement, home region and any explicit region pin; see the routing guidance below |
| `429` / `503` | Respect `Retry-After` where supplied; repeat only safe operations within a deadline |

C# errors derive from `TerraBaseException` and expose `StatusCode`, `ErrorCode` and `OperationIndex`; named subclasses include authentication, forbidden, not found, conflict, precondition failed and unavailable. C# `GetDocumentAsync` returns `null` for a missing document, whereas JavaScript `get` throws a typed not-found error. Go exposes `APIError`; check the SDK's operation-specific result and error semantics.

## Region routing errors (421)

`wrong_region` means the database is served or managed elsewhere. When supplied, `X-TerraBase-Database-Home` names its home and `X-TerraBase-Database-Regions` is a comma-separated list of placed regions. Data access may use a placed region; home-only management operations, such as placement changes and branching, must use home. `X-TerraBase-Region` identifies the region that answered this request.

`misdirected_request` can mean an explicit `X-TerraScale-Region-Pin` could not be satisfied, or a pin is empty, duplicated or inconsistent with the regional entrypoint. This refusal may contain only the code, without database placement headers. Missing headers do not establish that the database is absent.

Refresh the public [`GET /v1/regions` catalog](/pt-br/concepts/replication#discover-and-route-to-regions) and the database's authorized placement metadata. Choose an available placed region, or home for a home-only operation, and make the hostname and pin agree. Keep a required residency pin. Use the unpinned global endpoint only when execution in any permitted placed region is acceptable. If a valid pin still returns 421, stop after bounded attempts and retain the request ID and safe response headers for support.

Do not turn every 421 into an automatic mutation retry. An explicit rejection is different from a timeout, disconnected response or uncertain write; reconcile those outcomes as described below.

## Retry behavior by SDK

JavaScript and C# send requests once. Go applies a bounded retry policy to replay-safe requests, including eligible overload responses; it does not replay an uncertain mutation. SDK retry policies are not interchangeable.

For a repeatable read/query, wait as directed by the server, bound attempts and honor cancellation. Validation, authorization and deterministic conflict failures need a fix before another attempt. Do not hide those failures in an infinite retry loop.

## Idempotency and uncertain writes

The current Documents contract does **not** provide mutation deduplication and does not support `Idempotency-Key`. Do not add one and assume it makes a write safe. Generated-ID creates, increments and transaction replays can duplicate work after a lost response.

Read the known document ID to reconcile a lost response when possible. A caller-selected ID with a create-only precondition, or a write conditioned on a previously observed version, can prevent an additional conflicting write; neither is a general deduplication receipt. Do not turn a conditional-write rejection into a new unconditional mutation.

For a server-generated create whose response was lost, the ID may be unknown. Keep an application correlation field and a deliberate recovery process; a query is not proof of global uniqueness or permission to delete other documents.

</div>
