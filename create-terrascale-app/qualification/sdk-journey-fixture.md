# Cross-SDK fixture journey

`qualification/sdk-journey-fixture.mjs` is a deterministic, credential-free
test harness for composing the TypeScript, Go, Rust, .NET, and CLI consumers
against one local HTTP server. It models the ordering of signup, email
verification, onboarding, and synthetic project-key issuance, then runs one
caller-supplied driver for each consumer with the same fixture key.

This is explicitly fixture evidence. The server is not Dashboard, Better Auth,
TerraBase, or a cloud API, and a passing report sets `cloudQualification` to
`false`. It must not be used to claim production, OCI, Cloudflare, billing, or
provider qualification. The harness fails closed if any of the five drivers is
missing or exits nonzero.

Set these variables to shell commands that exercise the packaged clients:

```sh
export TERRASCALE_FIXTURE_DRIVER_TYPESCRIPT='...'
export TERRASCALE_FIXTURE_DRIVER_GO='...'
export TERRASCALE_FIXTURE_DRIVER_RUST='...'
export TERRASCALE_FIXTURE_DRIVER_DOTNET='...'
export TERRASCALE_FIXTURE_DRIVER_CLI='...'
export TERRASCALE_FIXTURE_RUN_ID="sdk-fixture-$(date +%s)"
export TERRASCALE_FIXTURE_RESULT=/tmp/terrascale-sdk-fixture.json
npm run qualify:sdk-fixture
```

Drivers receive `TERRASCALE_API_URL`, `TERRASCALE_API_KEY`,
`TERRASCALE_PROJECT_ID`, `TERRASCALE_DATABASE_ID`, and
`TERRASCALE_JOURNEY_RUN_ID` from the runner. The key is synthetic and scoped
to the in-process fixture; it is never a real credential. Driver output is
bounded by the runner and is not included in the report.
