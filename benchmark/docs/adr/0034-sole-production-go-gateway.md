---
status: accepted
date: 2026-08-30
authority: direct workspace-owner instruction
---

# Sole production Go gateway

## Decision

The independently deployable `TerraScale.Database` service is the sole
production gateway between public clients and TerraBase. The gateway is
implemented in Go and uses the repository-local `terrabase-go` module.
`terrabase-go` is the only network client allowed to speak private TBEP.

Public clients never connect to a TerraBase Member, import private protobuf
bindings, or construct TBEP requests. The gateway owns the public HTTP API,
authentication, authorization, tenant binding, idempotency admission,
cancellation, request limits, safe error projection, and public lifecycle.
TerraBase remains authoritative for native data, durability, replication,
query results, operation progress, and usage facts.

The gateway cannot fabricate a successful engine outcome, convert an unknown
commit into a failure, weaken a durability refusal, or silently retry a
non-idempotent request. It carries deadlines, cancellation, principal context,
tenant context, idempotency identity, and required durability through the
typed Go client.

## Network boundary

Production gateway-to-engine traffic uses private network paths and TBEP mTLS.
Members have no public ingress. Direct TBEP, plaintext TCP, trusted headers,
test certificates, and UDS are unavailable in the public deployment.

The private protobuf schema stays in the TerraBase repository. The generated
Go bindings stay in `terrabase-go`. The gateway pins a released client module
and records its schema digest in the deployment manifest. A copied or locally
regenerated binding outside that module is not an accepted client.

## Studio and internal Rust use

The private loopback Studio may open a designated engine location through the
Rust library API. It is not a network client and is not deployed in the public
request path. Internal Rust tests and composition crates may also link the
engine. Neither exception permits another process-to-process TBEP client.

## Required evidence

Before the platform accepts users, evidence must show:

1. every supported public database operation reaches TerraBase through the Go
   gateway and `terrabase-go`;
2. generated bindings and schema digests match the released engine artifact;
3. mTLS rejects wrong roots, identities, expired certificates, and plaintext;
4. tenant isolation, authorization, idempotency, cancellation, bounded
   resources, safe errors, and engine-loss behavior pass at the gateway seam;
5. public deployment scans find no direct Member route or private protobuf
   artifact; and
6. rollback pins a known compatible engine, Go client, gateway, and schema
   digest without rebuilding any artifact.
