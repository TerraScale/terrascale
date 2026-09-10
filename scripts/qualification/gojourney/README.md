# Real Go SDK journey

This is a private qualification consumer, not part of the SDK module. It has
no `replace` directive, mock transport, or local checkout fallback. Resolve
`terrascale.tech/sdk` to an immutable released version before running. For
this source revision, run `./prepare-file-proxy.sh ../../../
032e579b645e02edac45b09c94e7ea7c1abc9346`; use its printed
`GOPROXY=file://... GOSUMDB=off` prefix. The generated
`file-proxy/QUALIFICATION_SOURCE` and ZIP SHA-256 retain exact artifact identity.

Required environment: `TERRASCALE_API_URL`, `TERRASCALE_AUTH_URL`,
`TERRASCALE_PROJECT_ID`, `TERRASCALE_DATABASE_ID`, `TERRASCALE_API_KEY`, and
`TERRASCALE_JOURNEY_RUN_ID`. `TERRASCALE_COLLECTION` is optional and defaults to
`sdk_journey`. `TERRASCALE_DENY_API_KEY` is optional; when supplied, exercise verifies a
401/403 against the same scope. `TERRASCALE_AUTH_URL` is required as an explicit authority input and must
be distinct from `TERRASCALE_API_URL`; the SDK API URL remains the only endpoint used by
this consumer.

Run exactly one phase: `go run . --phase seed|verify|exercise|cleanup`.
Output is bounded JSON observations and never includes credentials. Missing
configuration, unsupported operations, authorization failures, and timeouts
are failures. Cleanup only addresses documents named
`${TERRASCALE_JOURNEY_RUN_ID}-go` and
`${TERRASCALE_JOURNEY_RUN_ID}-go-exercise` in the configured database and collection.
