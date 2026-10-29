---
title: Errors and safe retries
---

<!-- i18n:fallback:es -->
> **Traducción pendiente.** Esta página aún no tiene revisión humana en español y muestra el contenido original en inglés.

<div lang="en" dir="ltr">

# Errors and safe retries

Decide recovery from the typed status and code, and from whether an operation may have committed. A timeout does not prove a write failed.

## Typed status codes

| Response | Recovery |
| --- | --- |
| `400` / `413` / `415` | Correct the request, payload, size or media type. |
| `401` / `403` | Obtain valid authorized access; do not auto-retry. `403` means the resource is yours but the key lacks a scope (`forbidden_scope`) or the client is outside its IP allowlist (`ip_not_allowed`). |
| `404` | The resource does not exist **or your credential cannot reach it**; both answer the same, so another project's resources stay invisible. For `unknown_database`, check the project ID and database name and the key's project and database restriction. |
| `409` / `412` | Resolve the conflict or the stale version before writing again. |
| `429` / `503` | Respect `Retry-After` where supplied; repeat only safe operations within a deadline. |

## Retry behavior

SDKs retry only replay-safe reads after transient refusals, such as `429`, a gateway `502` or `504`, or a transient `503`. They wait at least the server's `Retry-After`, otherwise use a jittered backoff, and stop before the call's deadline or cancellation. No SDK resends a write the server may have received, and validation, authorization, not-found and conflict failures are never retried. The exact codes and attempt limits differ by SDK; see each SDK's reference.

For a repeatable read, wait as directed by the server, bound attempts and honor cancellation. Validation, authorization and conflict failures need a fix before another attempt. Do not hide those failures in an infinite retry loop.

## Uncertain writes

A write whose response was lost may have committed. Read the data that the write would have produced before you repeat it. A conditional write, such as one that fails when a key already exists or when a row has changed, can prevent a duplicate; do not turn a rejected conditional write into an unconditional one.

</div>
