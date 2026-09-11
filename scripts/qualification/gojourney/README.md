# Real Go SDK journey

This is a private qualification consumer, not part of the SDK module. It has
no `replace` directive, mock transport, or local checkout fallback. Resolve
`terrascale.tech/sdk` to an immutable released version before running. For
this source revision, run `./prepare-file-proxy.sh ../../../
71e10be4f6db77d3da40ed0361fc111a55aa8218`; use its printed
`GOPROXY=file://... GOSUMDB=off` prefix. The generated
`file-proxy/QUALIFICATION_SOURCE` and ZIP SHA-256 retain exact artifact identity.

Required environment: `TERRASCALE_API_URL`, `TERRASCALE_PROJECT_ID`,
`TERRASCALE_DATABASE_ID`, `TERRASCALE_API_KEY`, and
`TERRASCALE_JOURNEY_RUN_ID`. `TERRASCALE_COLLECTION` is optional and defaults to
`sdk_journey`. `TERRASCALE_DENY_API_KEY` is optional; when supplied, exercise verifies a
401/403 against the same scope. The SDK API URL is the only endpoint used by this
consumer; authentication is supplied through the API key.

Run exactly one phase: `go run . --phase seed|verify|exercise|cleanup`.
For the all-SDK coordinator, build with `go build -mod=readonly -o /absolute/path/gojourney .`
using the approved immutable proxy, then supply that binary and its SHA-256.
The binary embeds `main.go`, `go.mod`, `go.sum`, and their checksum manifest;
startup verifies these embedded inputs independently of cwd. `gojourney --preflight`
performs only that verification, without credentials or provider calls. Source
changes require refreshing the exact manifest entry before building; mismatches
remain fatal. Do not change `go.sum` to accept a broken proxy ZIP.
Output is bounded JSON observations and never includes credentials. Missing
configuration, unsupported operations, authorization failures, and timeouts
are failures. Cleanup only addresses documents named
`${TERRASCALE_JOURNEY_RUN_ID}-go` and
`${TERRASCALE_JOURNEY_RUN_ID}-go-exercise` in the configured database and collection.

This journey does not invent a producer route for durable export/import
admission. Those published SDK operations are covered by contract tests; a real
run should exercise them only when the authorized key is explicitly granted
those scopes.
