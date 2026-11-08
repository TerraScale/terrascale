---
name: add-feature
description: Plan and build a new TerraScale product feature end to end in the monorepo (owning service, API contract, storage, SDKs, dashboard UI, docs, tests). Use when asked to add or extend a feature, endpoint, SDK operation or screen.
---

# Add a feature

Read the root `AGENTS.md` and the owning area's `AGENTS.md` first. This skill is the order of work; those files are the rules.

## 1. Find the owner

Each product owns its data and its API. Put the feature in the product that owns the data:

| Data or behavior | Owner | Code |
| --- | --- | --- |
| Users, sessions, MFA, SSO, OAuth clients, organizations, projects, memberships, API credentials | ts-auth | `src/TerraScale.Authentication*` |
| Databases, SQL tables, queries, transactions, backups, indexes, schema, usage metering | TerraBase | `src/TerraBase` |
| Platform UI/API/MCP, plans, subscriptions, billing, spending caps | Dashboard | `src/TerraScale.Dashboard` |
| Placement, scaling, rollout | ts-orch | separate `ts-orchestrator` repo |
| Customer libraries | SDKs | `sdk/{dotnet,js,go,python,rust}` |

Another product's data is reached only through its API (Dashboard → TerraBase over MagicOnion via `src/TerraScale.TerraBase.Contracts`; services → ts-auth over HTTP). Never read another product's storage.

## 2. Find a sibling to copy

Look for the closest existing feature and copy its shape, naming and tests:

```sh
ls src/TerraBase/Features/                 # one folder per feature
git grep -n 'Name("' src/TerraBase/Features/<Similar>/
ls tests/TerraBase.Fast/<Similar>/
```

A feature folder holds its endpoints, records, services, options, storage and registration together (`Features/<Feature>/`).

## 3. Build in this order

1. **Contract.** Request/response records, route under `/v1`, error bodies, required scope. See the `add-endpoint` skill.
2. **Storage.** Application data lives in TerraBase SQL tables: one table per entity type, one row per record; IDs are ULIDs. TerraBase internals go through `Engine/` interfaces. No in-memory or local-file stores.
3. **Endpoint and service.** Behavior behind DI interfaces; the endpoint stays thin.
4. **SDKs.** After the producer contract exists, add the operation to every SDK that exposes the area. See the `sdk-parity` skill.
5. **UI.** Dashboard or auth screens follow `.agents/skills/simple-ui/SKILL.md`. Take before/after screenshots.
6. **Docs.** Update the API, storage or auth doc for the behavior you changed. New docs get a new file listed in their area index.

## 4. Rules that catch most reviews

- Always on: no feature flags, enable booleans, opt-ins, kill switches or tuning knobs. One correct value in code.
- Region, node and peers come from `TS_REGION`, `TS_NODE`, `TS_PEERS_FILE`; add no operator settings.
- Greenfield: no compatibility shims or migrations unless asked. Breaking a public API needs an explicit decision.
- Fail closed on auth, tenant scope and validation. A submitted ID proves nothing; check ownership.
- Async and cancellation-aware; bound every queue, cache and buffer by count and bytes.
- .NET: register every request/response type in the service's `System.Text.Json` source-generated context (TerraBase: `src/TerraBase/Hosting/TerraBaseJsonContext.cs`). Warnings are errors; no suppressions.

## 5. Tests: few and fast

- One happy-path test and, if the feature has a critical failure mode (auth, isolation, data loss), one critical-failure test. Nothing else.
- Tests drive the real app over real listeners with their own database/tenant and synthetic data (`tests/<Project>.Fast/<Feature>/`). No fixed ports, sleeps or `NotInParallel`.
- Each test runs in under 10 s.

## 6. Ship

Build only what you changed (`make <area>-build`, `npm run typecheck`, `go build ./...`), commit on a branch from fresh `origin/main` and open a PR to `main`. CI runs the tests on it; it squash-merges when checks are green. If your merge turns main red, fix or revert within about 30 minutes. See `docs/dev/shipping.md`.
