# Service caller authentication

The internal HTTP/2 listener accepts a signed assertion in
`X-TerraScale-Caller`. REST and gRPC replay use the same route admission rules.

Set `TERRABASE_CALLERS_FILE` to a version 1 JSON registry containing a `callers`
array. Each entry has `kid`, `caller`, `kind`, a public P-256 `jwk`, `grants`,
and a UTC `notAfter`. A missing, blank or malformed file and malformed entries
stop startup with exit code 2. A valid registry whose `callers` array is empty
starts and rejects every caller token.
Expired entries are validated and skipped; their tokens are rejected as unknown
keys. Registry changes take effect when TerraBase restarts. An unset registry permits existing
peer authentication to continue.

Service caller names are `ts-auth`, `dashboard`, and `edge`. Key ids use
`<caller>.<cell>.<n>`, for example `dashboard.gru.1`.

Assertions use ES256, type `ts-caller+jwt`, and the registry key id. Their issuer
matches the caller name, their audience is `terrabase`, and they include
`iat`, `exp`, and `jti`. Service assertions last at most five minutes.
Operator assertions last at most fifteen minutes and include a `cap` array
within the registered grants and a `reason` of 1–500 characters.
Time validation permits thirty seconds of clock skew.

A `data:<project>` grant creates a SQL identity bound to that project and its
organization, with SQL read and write scopes. It does not include database
management scopes. Route metadata admits `closure`, `plans`, `usage.read`,
`catalog.read`, `rollout`, and `admin` where applicable. Unclassified internal
routes remain peer routes. A caller assertion cannot authorize a peer forwarding
request or a region-scoped plan or closure fan-out request.

A valid assertion takes precedence over an accompanying peer credential.
A request without a valid assertion can still authenticate using the existing
peer credential. An invalid assertion without a valid peer credential answers
401; an authenticated caller outside its grants answers 403.
A user bearer on a client or management route retains its own authorization.
The gRPC bridge sends bearer calls through the client listener without the caller
header, preserving usage metering, placement and regional forwarding. Forwarding
never copies the caller header. Authentication counts each gRPC call once, at
the outer request; the replay still checks route grants.

Metrics use the `TerraScale.TerraBase` meter:

- `terrabase.internal.authentication.requests`, with `credential` and `caller`.
- `terrabase.operator.legacy_grants`, with `kind=data|act_as`.
- `terrabase.caller.keys.expired`: expired registry entries, including keys that
  expire after startup.
- `terrabase.caller.keys.soonest_expiry`: earliest live key expiry as Unix seconds,
  or zero when no live keys remain. Alert before this timestamp and on expired entries.

Verification outcomes distinguish `peer`, `caller`, `legacy`,
`caller_rejected`, and `unmatched`. Caller labels come from the registry,
and tokens and private keys are never metric labels.
