---
status: accepted
---

# Internal Operations Studio on Vinext

## Decision

TerraBaseV2 owns one internal Operations Studio under `studio/`. It is a
Vinext React application on the repository's pinned VitePlus `vp` toolchain.
It is not the customer Dashboard and does not own authentication, Organizations,
Projects, Users, Payments, MCP, or customer product workflows.

The Studio contains no C#/.NET host or second backend in this repository. It
uses named typed operator operations through the independently deployed
`TerraScale.Database` gateway. It never connects to a TerraBase Member, TBEP,
storage files, NATS, or object storage directly.

Operator authentication comes from Dashboard-owned Better Auth. The Database
API converts a current authorized operator grant into a short-lived engine
context. Studio access follows the same tenant boundaries, confirmation,
audit, secret handling, rate limits, and field projection rules as the
underlying operation.

The Studio may inspect documents, queries, Auto-Indexes, relationships,
replication progress, Partitions, snapshots, recovery, resource use, and
qualified metrics. Mutating and destructive operations remain individually
typed, authorization checked, confirmation gated, and auditable. There is no
raw HTTP, arbitrary TBEP, shell, query-code, or private control-plane escape
hatch.

Live views use the bounded reactive protocol. Disconnect and process loss
recover from checkpoints or a fresh authorized snapshot. No correctness or
operator action depends on one Studio process or browser session.

The Orchestrator owns deployment and routing of the emitted Vinext artifact.
Packaging must not place a Node or C# runtime inside the Rust engine process.
