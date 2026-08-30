---
status: accepted
date: 2026-08-29
authority: ../../../GOAL.md
---

# Sole production gateway and retirement of the repository-local host

## Context

The repository-local `host/` tree contains a useful C# API and TBEP
conformance lane, but it is documented as migration source rather than the
product host. Its HTTP boundary relies on trusted local `X-TB-Principal` and
`X-TB-Tenant` context, binds to loopback, and supervises a local engine process.
Those properties are appropriate for a development and cross-language lane;
they are not a customer authentication or authorization boundary.

The workspace charter, repository guidance, and
`docs/design/host-retirement-decision.md` identify the independently deployable
Database service as the sole production gateway. `TerraScale.Internals` is the
permanent source owner of that service. The standalone `TerraScale.Database`
repository remains a transitional source and deployment lane while provenance,
parity, and cutover evidence are completed. This ADR records runtime
authority; repository migration and retirement timing remain delivery work.
The qualification sweep still records gateway migration, authorization
integration, and cutover as cross-repository work.

## Decision

The `TerraScale.Internals`-owned, independently deployable TerraScale.Database
service is the sole production gateway between public clients and the
TerraBaseV2 engine. The standalone `TerraScale.Database` repository is
transitional and may be retired only after the provenance, generated-binding
parity, supported-operation cutover, and rollback gates in this ADR and the
workspace release plan are accepted. The repository-local `host/` lane remains
available only for migration work, development, and cross-language
conformance until its deletion packet is accepted. A public deployment must
not expose its trusted header gate, loopback assumptions, direct TBEP mode, UDS
binding, or local engine-process supervisor.

The gateway migration preserves the typed operation and error contracts that
are in scope, but re-owns authentication, authorization, tenant binding,
idempotency, cancellation, observability, and production lifecycle behavior
at the product boundary. The engine remains the authority for native data and
durability outcomes; the gateway must not fabricate an engine result or
downgrade a typed refusal.

The repository-local CLI and Studio may remain conformance or operator tools
while migration is underway. Their production transport, authentication, and
support evidence is owned by their respective repositories; neither tool is
authorized by this record to bypass the gateway.

## Consequences

- Public route ownership, grant verification, key management, and customer
  audit policy stay with the product gateway and control-plane owners.
- TerraBaseV2 must maintain a stable, bounded, typed private TBEP contract and
  provide the vectors, drift checks, hostile-decode checks, and failure
  semantics needed by that gateway.
- The local host can continue to provide real-engine and conformance evidence,
  but its tests cannot be cited as proof that production authentication or
  public deployment is complete.
- Deleting `host/` is a separate change. It requires the preconditions in
  `docs/design/host-retirement-decision.md`, including gateway operation
  coverage, registry ownership and drift enforcement, CLI/Studio disposition,
  CI rewiring, and replacement evidence for every retained contract.

## Adoption and retirement evidence

This accepted runtime decision does not claim that migration or retirement is
complete. Before the production cutover and any deletion of `host/`, release
evidence must show:

1. The `TerraScale.Internals`-owned, independently deployable TerraScale.Database
   service serves every operation declared in the supported public contract
   through authenticated, authorized typed routes.
2. The gateway's registry and generated bindings are drift-checked against the
   schema owner and pass the shared valid/error and hostile-input vectors.
3. Tenant isolation, idempotency, cancellation, bounded resource behavior,
   audit redaction, and engine-loss behavior are proven at the gateway seam.
4. The trusted-header host and direct TBEP paths are unreachable from the
   public deployment and are either retired or explicitly retained only in an
   isolated development/conformance profile.
5. A replacement matrix covers all host capabilities retained by the product,
   with missing capabilities recorded as an accepted non-goal rather than
   silently dropped.

Before retiring the standalone `TerraScale.Database` repository, an
owner-approved cutover receipt must also bind the Internals source revision to
the deployed artifact, record generated-code and contract parity, identify the
rollback candidate and window, and show that CI, clients, release manifests,
and operational documentation no longer use the standalone repository as a
source authority. A passing local build or a copied file is not sufficient.

Until those facts are evidenced, the repository status remains pre-production;
the runtime boundary itself is already accepted.
