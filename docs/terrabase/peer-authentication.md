# Internal peer credential rollout

TerraBase sends `X-TerraBase-Peer-Credential` from `TERRABASE_PEER_SECRET` on
replication Push streams, Digest and ReadBucket repair calls, and catalog trust
probes. Both ts-auth storage transports and operator scripts send it too.

Every request on the internal listener (8081) verifies exactly one header against
the secret before endpoint execution. Verification outcomes are recorded in
`terrabase_peer_authentication_requests_total{credential="matched|unmatched"}`.
Labels contain only those two outcomes, never credentials or request identities.
Public and metrics listeners do not record peer verification outcomes.

Unmatched internal requests are refused with HTTP 401 before endpoint execution;
gRPC clients observe `Unauthenticated`. This enforcement release follows the
sender-and-observation release. It merges only after T2 deployment works, PR1 is
live in gru, sjc and fra, and every TerraBase machine has recorded zero new
missing or invalid credentials for one continuous hour. Matched traffic and
continuous scrapes must be present throughout that hour; restarts or scrape gaps
restart the window. There is no flag or switch.

## Provisioning and rotation

The peer secret is 32 cryptographically random bytes encoded as 64 hexadecimal
characters. Equal-length valid headers use fixed-time comparison. There is no
generated production default; missing credentials must not grant internal access.
The same credential must reach TerraBase and ts-auth through OCI Vault with scoped
instance-principal grants. Each isolated test or preview owns its credential.

Use the verified [ts-orch secret lifecycle](https://github.com/TerraScale/ts-orchestrator/blob/main/docs/secrets.md)
and its documented implementation limits. No bucket, app-staging or deployment CLI
syntax is implied by OCI Vault. Never print secret values or include them in
arguments, logs, source, PR descriptions or persistent local configuration.

Missing, wrong, malformed or repeated credentials return HTTP 401.
Rotation accepts one credential, so mismatched peers refuse one another.
Rotate before go-live or during a reviewed maintenance window, staging both apps
and completing their normal quality-gated rollout. Public APIs retain their API
key and token authorization, public health remains available for health probes, and
the separate metrics listener serves metrics only.

Transport remains HTTP/2 without TLS within the private service network.
The shared credential does not provide mutual TLS or bind identity to a machine.
Anyone holding it has peer and operator privileges.
