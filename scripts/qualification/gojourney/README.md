# Real Go SDK journey

This is a private qualification consumer, not part of the SDK module. It has
no `replace` directive, mock transport, or local checkout fallback. Resolve
`terrascale.tech/sdk` to an immutable released version before running. For
this source revision, run `./prepare-file-proxy.sh ../../../
658d897f69a529ae5b3e7668510723ae7eb0df7e`; use its printed
`GOPROXY=file://... GOSUMDB=off` prefix. The generated
`file-proxy/QUALIFICATION_SOURCE` and ZIP SHA-256 retain exact artifact identity.

Required environment: `TERRASCALE_API_URL`, `TERRASCALE_PROJECT_ID`,
`TERRASCALE_DATABASE_ID`, `TERRASCALE_API_KEY`, and
`TERRASCALE_JOURNEY_RUN_ID`. `TERRASCALE_COLLECTION` is optional and defaults to
`sdk_journey`. `TERRASCALE_DENY_API_KEY` is optional; when supplied, exercise verifies a
401/403 against the same scope. The SDK API URL is the only endpoint used by this
consumer; authentication is supplied through the API key.

Run exactly one phase: `go run . --phase seed|verify|exercise|cleanup`.
Output is bounded JSON observations and never includes credentials. Missing
configuration, unsupported operations, authorization failures, and timeouts
are failures. Cleanup only addresses documents named
`${TERRASCALE_JOURNEY_RUN_ID}-go` and
`${TERRASCALE_JOURNEY_RUN_ID}-go-exercise` in the configured database and collection.

This journey does not invent a producer route for durable export/import
admission. Those published SDK operations are covered by contract tests; a real
run should exercise them only when the authorized key is explicitly granted
those scopes.
