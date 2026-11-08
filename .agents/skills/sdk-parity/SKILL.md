---
name: sdk-parity
description: Add or change a customer operation across the TerraScale SDKs (.NET, JS, Go, Python, Rust) so they match the producer's API contract. Use after a public endpoint is added or changed, or when an SDK is missing an operation.
---

# SDK parity

The producing service owns the contract. SDKs follow it; they never invent routes, fields, headers or behavior.

## 1. Read the contract first

From the producer endpoint (see `add-endpoint`), write down: method, `/v1` route, operation id, path/query params, request and response fields with types and nullability, required scope, error codes, idempotency and pagination. If any of these is unclear, fix the producer first.

## 2. Add it to each SDK

Read each SDK's `AGENTS.md` before editing. Copy the closest existing operation in that SDK.

| SDK | Where | Contract check | Build | Fast |
| --- | --- | --- | --- | --- |
| .NET | `sdk/dotnet` | `sdk/dotnet/contracts/` | `dotnet build` of the SDK project | `make sdk-dotnet-test-fast` |
| JS | `sdk/js/src` (ES modules + JSDoc) | `npm run check:v1-route-manifest` (`sdk-v1-route-manifest.json`) | `npm run build` | `make sdk-js-test-fast` |
| Go | `sdk/go` (`v1/`, root package) | `sdk/go/contracts/` | `go build ./...` | `make sdk-go-test-fast` |
| Python | `sdk/python` | `sdk/python/contracts/openapi-routes.json` | type check | `make sdk-python-test-fast` |
| Rust | `sdk/rust` | `sdk/rust/contracts/` | `cargo build` | `make sdk-rust-test-fast` |

Regenerate route manifests or contract fixtures with the SDK's own script when the route list changes (JS: `npm run generate:v1-route-manifest`).

## 3. Rules

- Same names and shapes across SDKs, in each language's idiom.
- Typed only: no generic request helpers, arbitrary paths, untyped JSON or custom headers.
- Retry only documented transient failures; mutations need a stable idempotency key. Never retry validation, auth or conflict errors.
- Keep credentials and server-only operations out of browser bundles (JS).
- Packages stay at `0.0.X`; don't publish or tag releases until go-live (`docs/decisions/go-live.md`).

## 4. Tests

One happy-path test per SDK against an HTTP fixture of the real contract. No per-SDK edge-case matrices.

## 5. Docs

Update the SDK README's operation list and the docs-site page for the feature, if one exists.
