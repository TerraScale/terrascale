# Rust SDK real-service qualification

`run.sh` verifies the caller-supplied immutable SDK crate archive, extracts it
into a clean temporary consumer, and runs the public API against a real
TerraScale service. It never repackages checkout sources or uses mock
transport. Run one phase at a time:

```text
./scripts/qualification/run.sh seed|verify|exercise|cleanup
```

The required environment is `TERRASCALE_API_URL`, `TERRASCALE_AUTH_URL`,
`TERRASCALE_PROJECT_ID`, `TERRASCALE_DATABASE_ID`, `TERRASCALE_API_KEY`, and
`TERRASCALE_JOURNEY_RUN_ID`, plus the independently supplied
`TERRASCALE_SDK_CRATE`/`TERRASCALE_SDK_CRATE_SHA256` and
`TERRASCALE_SDK_DERIVE_CRATE`/`TERRASCALE_SDK_DERIVE_CRATE_SHA256` pairs.
Both regular files and hashes, archive roots, package manifests, and the exact
derive dependency are validated before extraction, Cargo, or provider access.
The derive package is extracted to an explicit temporary path and bound by
path; registry, checkout, and archive-discovery fallbacks are not allowed.
Output is bounded JSON observations and the artifact SHA-256; credentials are
never printed. Mutating journey calls use explicit, phase-scoped idempotency
keys and `exercise` asserts that an absent document returns HTTP 404. Optionally
set `TERRASCALE_DENY_API_KEY` to a key issued without access to the journey
database; `exercise` then requires a 401/403 denial. All five consumers use the
same issued `TERRASCALE_API_KEY`. Missing config, an archive hash mismatch,
unsupported calls, and zero skips fail the phase before build or network access.
