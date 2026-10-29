# Installed current producer journey

`current-producer-consumer.js` verifies a supplied immutable npm tarball digest,
installs it in a fresh temporary consumer and imports only the installed package.
It requires a real isolated allocation and never substitutes an in-process server.
The packaged-consumer wrapper delegates to this same journey.

Required inputs include `TERRASCALE_SDK_TARBALL` (absolute regular tarball path),
`TERRASCALE_SDK_TARBALL_SHA256`, `TERRABASE_TENANT`, `TERRABASE_DATABASE`,
`TERRABASE_SHARD`, `TERRABASE_COLLECTION`, `TERRABASE_SCHEMA_GENERATION`,
`TERRABASE_SCHEMA_DIGEST`, `TERRABASE_API_KEY`, `TERRABASE_DENY_API_KEY`,
`TERRABASE_TEST_FIELD_ID` and `TERRABASE_TEST_FIELD_TYPE` (`utf8` or `i64`).
The default `TERRABASE_ROUTING_MODE=atlas` uses actual authenticated discovery.
Omit `TERRABASE_ENDPOINT` and supply independently allocated
`TERRABASE_EXPECTED_REGION` and `TERRASCALE_QUALIFICATION_ALLOCATION_ID`. Both
routes require actual native keys and isolated write scope.
For provisioned development/native exclusion/CAS qualification, explicitly set
`TERRABASE_ROUTING_MODE=direct` and `TERRABASE_ENDPOINT` to the allocated bare
HTTPS origin or loopback HTTP origin. TerraScale data origins use `https://<region>.api.terrascale.tech`; retired root regional hosts and the global API host are rejected.

The Node suite and browser journey share this routing admission. Completely
absent configuration remains an honest skip; partial or invalid configuration
fails. The browser journey additionally requires `TERRABASE_BROWSER_DOCUMENT`
and real browser CORS admission. Atlas mode traces actual authenticated discovery
and regional requests, asserts the closest eligible endpoint against the
allocation's expected region, and verifies construction makes no request.

```sh
npm run qualify:current-producer
```

The journey covers exact retry, changed-effect conflict, point-version update
and deletion, rejected/unknown/committed transaction status, query, finite
subscription, cancellation and credential/scope denial. Missing configuration
fails; it is never a green skip. The Atlas mode verifies actual receiver routing,
unconditional mutation/status/query/snapshot/poll/cancellation and discovery
denial; it also verifies that unavailable conditional/group/security operations
never reach the native service. Output is bounded and redacted. Document cleanup
is verified; retained tombstones still require allocation-owner cleanup.

The PostgreSQL listener and deployed authenticated account/browser flows have
separate actual runtime gates. Native package results do not qualify management,
production Better Auth security or all deployed application behavior.
