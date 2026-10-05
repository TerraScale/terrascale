# 0006: Unauthenticated health probes on the client listener

- Status: accepted (TS-483 acceptance criteria, 2026-10-04)
- Scope: `src/TerraScale.TerraBase/Features/Health`, `Hosting/Listeners.cs` (`ListenerMetadata.Probe`)

## Context

The client listener serves only endpoints that require a scoped API key. Production load
balancers and the Caddy edge reach TerraBase only through that listener and cannot present a key,
and the administrative listener is loopback-only by construction.

## Decision

`GET /healthz` (liveness) and `GET /readyz` (readiness) are the only endpoints the client listener
serves without authentication. They carry `ListenerMetadata.Probe`, so they answer on the
administrative and client listeners and on no other.

## Consequences

- Their bodies are exactly `status`, `version` and the node's `region` (the TS-499 contract):
  no tenant, database or credential data. The version and region are public on every client listener.
- `/readyz` creates and removes an empty file in the data directory, off the request thread and
  outside every engine lock. At most one such probe runs at a time; concurrent callers await
  that same task instead of receiving a stale cached result while it runs. Completed results are
  cached for one second by default, configurable with `--readiness-cache-seconds` or
  `TERRABASE_READINESS_CACHE_SECONDS` (positive integer, 1 to 60). Zero is prohibited so sequential
  anonymous requests cannot multiply disk work. A probe still running after 2 seconds means a
  stalled volume, and `/readyz` answers 503. Shutdown immediately answers 503 regardless of cached
  or in-flight results; engine closure bypasses the filesystem result cache on every request.
- Any other endpoint on the client listener still requires a scoped API key, or a ts-auth access
  token on the Management API. With `auth-issuer` set, `/readyz` also answers 503 until ts-auth's
  signing keys have been fetched once ([0007](0007-ts-auth-user-tokens.md)).
