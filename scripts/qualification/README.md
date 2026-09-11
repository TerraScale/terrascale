# Packaged SDK customer journey

`packaged-consumer.ts` qualifies one supplied immutable TypeScript SDK artifact
as an external consumer. It verifies the artifact digest, installs that exact
tarball into a fresh temporary npm project, and runs the selected phase without
importing checkout source or using a mock transport. Every phase must use the
same artifact path and digest.

Set all of these values before every invocation:

```text
TERRASCALE_API_URL       public API origin
TERRASCALE_AUTH_URL      Dashboard/Better Auth origin (validated and retained for coordination)
TERRASCALE_PROJECT_ID    onboarded project
TERRASCALE_DATABASE_ID   disposable database
TERRASCALE_API_KEY       one-time-issued scoped tsauth credential
TERRASCALE_JOURNEY_RUN_ID unique run identifier
TERRASCALE_SDK_TARBALL   absolute path to the packed SDK artifact
TERRASCALE_SDK_TARBALL_SHA256  SHA-256 of that artifact
```

Optionally set TERRASCALE_DENY_API_KEY to a second key issued for a
non-authorized scope. The exercise phase uses it against the same document
and requires a 401/403 denial. All five consumers must use the same
TERRASCALE_API_KEY for the shared journey.

Run one phase at a time:

```bash
node --experimental-strip-types scripts/qualification/packaged-consumer.ts --phase seed
node --experimental-strip-types scripts/qualification/packaged-consumer.ts --phase verify
node --experimental-strip-types scripts/qualification/packaged-consumer.ts --phase exercise
node --experimental-strip-types scripts/qualification/packaged-consumer.ts --phase cleanup
```

`seed` writes only `${RUN_ID}-typescript` in `sdk_journey`. `verify` reads the
four cross-SDK IDs (`typescript`, `go`, `rust`, `dotnet`) and requires the
TypeScript body exactly. `exercise` covers CRUD, stale `If-Match` denial,
idempotent replay, parameterized SQL, reactive attach/write/poll/close, a
typed missing-document error, and (when TERRASCALE_DENY_API_KEY is supplied)
denied-scope behavior. `cleanup` deletes only the TypeScript run ID.

Output is bounded JSON observations containing only labels, status, and typed
error codes; credentials and request headers are never printed. Missing
configuration or a failed required action exits nonzero. Live provider
credentials are required; there is no skip or fake-service mode.
