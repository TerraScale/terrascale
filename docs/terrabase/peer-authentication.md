# Internal peer credential rollout

TerraBase sends `X-TerraBase-Peer-Credential` from `TERRABASE_PEER_SECRET` on
replication Push streams, Digest and ReadBucket repair calls, and catalog trust
probes. Both ts-auth storage transports and operator scripts send it too.

Every request on the internal listener (8081) verifies exactly one header against
the secret before endpoint execution. Verification outcomes are recorded in
`terrabase_peer_authentication_requests_total{credential="matched|unmatched"}`.
Labels contain only those two outcomes, never credentials or request identities.
Public and metrics listeners do not record peer verification outcomes.

This first rollout phase records unmatched requests without refusing them. C1
remains open until the second PR enforces refusal. There is no flag or switch:
refusal is a separate code change, merged only after T2 deployment works and this
release runs in gru, sjc and fra. Before PR2, the unmatched counter must stay flat
on every TerraBase machine in those regions for 24 hours after PR1 is live on all
machines. Senders reach all regions before refusal starts.

The secret is 32 cryptographically random bytes encoded as 64 hexadecimal
characters, read only from the environment. Equal-length valid headers are
compared with fixed-time comparison. Missing or malformed secrets refuse startup
on Fly before storage opens. Off Fly, an absent secret records internal requests
as unmatched. TerraBase still needs the secret to call its peers off Fly; without
it, outbound peer calls fail. There is no generated production default.

## Provisioning and rotation

Store the same secret on `ts-terrabase` and `ts-auth` using Fly secrets. Each PR
preview generates an independent secret once and maps it into both containers,
alongside auth and backup secrets. Generate the value into a mode-0600 temporary
file outside Git, pass that file through stdin to
`fly secrets set --stage --app APP TERRABASE_PEER_SECRET=-`, and delete it.
Never print the value or include it in arguments, logs, source or PR descriptions.
Staging does not deploy or change machines.

After enforcement, missing, wrong, malformed or repeated credentials will return
HTTP 401 before endpoint execution; gRPC clients will observe `Unauthenticated`.
Rotation will accept one credential, so mismatched peers will refuse one another.
Rotate before go-live or during a reviewed maintenance window, staging both apps
and completing their normal quality-gated rollout. Public APIs retain their API
key and token authorization, public health remains available for Fly probes, and
the separate metrics listener serves metrics only.

Transport remains HTTP/2 without TLS within Fly's encrypted private network.
The shared credential does not provide mutual TLS or bind identity to a machine.
Once refusal is enforced, anyone holding it has peer and operator privileges.

`scripts/e2e/install-ci-credential.sh` reads the environment secret and supplies
curl with a mode-0600 temporary header file, deleted on exit. Fly does not expose
stored secret values. Never copy credentials into persistent local configuration
or retrieve them through shell output. Benchmark adoption is tracked in
[issue #217](https://github.com/TerraScale/terrascale/issues/217).
