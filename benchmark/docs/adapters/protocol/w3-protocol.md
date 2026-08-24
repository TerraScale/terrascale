# W3-3 protocol record: typed Protocol adapter

Status: implementation record for the W3-3 Protocol packet, 2026-08-24.
Worktree branch `fleet/w3-protocol`, based on `21152ca` on `develop` (W3-1
storage/engine, W3-F families, and W3-2 query/index integrated; the public
`V1QueryText` constructor live). This record describes focused-test evidence
only. It is not a parity qualification: PB-1000 and every other parity row
remain `unresolved` with empty Proof ID lists, and this packet adds no Proof
ID to any row.

## Scope and ownership

Owned paths only: `src/adapters/protocol/**`, `tests/adapters/protocol/**`,
and this document. No file outside that set was edited. In particular
`src/adapters/platform/**`, `src/operations.rs`, `src/types.rs`, `src/lib.rs`,
`Cargo.toml`, and `parity/**` are untouched. One deliberate note:
`tests/adapters/protocol/main.rs` carries only an explanatory comment —
Cargo does not auto-discover integration targets two levels below `tests/`,
and the manifest entry that would enable it is outside this packet's write
scope (see the collision actions below).

## Design as implemented

### Opaque caller context

`PrincipalId::try_new`, `CredentialInput::try_new(kind, secret)`,
`CallerEvidence::try_new(principal, tenant, credential)`, and
`CallerContext::try_new(evidence)` implement the contract's construction
table with structural validation only:

- A principal or secret that is empty or whitespace-only is rejected with
  `CallerContextError::EmptyPrincipal` / `EmptyCredential`. Finer principal
  and secret policy is authentication, which belongs to the W5 Platform
  Adapter; the evidence stays untrusted input.
- `CallerContextError::EmptyTenant` is retained in the closed error
  vocabulary but is unreachable through a validated `TenantId` today; the
  defensive checks exist anyway. `UnsupportedCredential` is likewise
  unreachable because `CredentialKind` is a closed two-variant enum.
- `CallerContext::try_new` performs no authentication, no authorization, and
  no tenant-scope minting. Platform mints the `TenantScope` and the private
  grant in W5; this module cannot express either.

### Redaction

`CredentialInput`, `CallerEvidence`, and `CallerContext` are not `Clone`,
expose no secret accessor, and their `Debug` and `Display` forms contain
only the principal, the tenant claim, the credential kind, and a fixed-width
redacted fingerprint: FNV-1a 64-bit over the secret bytes, rendered as
sixteen hexadecimal digits behind the fixed prefix `redacted:`. The
fingerprint is not an authentication token and does not reconstruct the
secret. `caller_context_try_new_structurally_validates_and_redacts_evidence`
asserts that every `Debug` and `Display` form of the credential, the
evidence, and the context contains the redacted fingerprint and never
contains the secret substring; `credential_fingerprint_is_stable_prefixed_and_secret_free`
pins stability, width, prefix, difference across secrets, and the FNV-1a
offset basis for the empty input.

### Closed registry forwarding

`ProtocolAdapter::call` is the only public seam. It accepts the borrowed
opaque `CallerContext` — a reference is always valid, so no caller
validation happens at this seam — and one closed `PublicOperation`:

1. The operation's own deadline and cancellation are checked once, bounded,
   without a lock: an already-elapsed deadline or already-set cancellation
   signal is refused before dispatch as `ProtocolError::Deadline` /
   `Cancelled`, preserving the mutating `OperationId` when present (reads
   carry none). The deadline is checked first when both bounds are violated.
   A deadline that elapses *during* dispatch is not rechecked at this seam;
   Platform and Engine own execution-time enforcement and surface it as the
   mapped `PlatformError::Deadline` row.
2. The operation is dispatched unchanged to the private `PlatformPort`
   together with the caller reference.
3. Any `PlatformError` is mapped to its `ProtocolError` row.

`ProtocolAdapter` stores `&'platform dyn PlatformPort`. `new` is
crate-private over that port, `new_with_platform_port` is the `cfg(test)`
double constructor, and `from_platform(&PlatformAdapter<'_>)` is the public
production acquisition path. The bridge `impl PlatformPort for
PlatformAdapter<'_>` lives in this module (same crate) because the platform
files belong to the W5 packet; it forwards to the public
`PlatformAdapter::dispatch` unchanged, and W5 may move it into the platform
wiring without behavior change. `PlatformAdapter::new` and `dispatch` are
still `unimplemented!()` W5 declarations, so `from_platform` proves the
acquisition shape compiles and hides the port, and executing operations
through it lands with W5.

Refusals that the design lists as returned errors are carried by the closed
type system instead: `PublicOperation` has exactly four variants, none of
which accepts a route, raw body, generic bytes, arbitrary SQL, a storage or
session handle, a permission claim, or a private control-plane operation.
`PrivateOperation` and `GenericAccessRejected` are therefore unreachable at
this seam and keep their place in the vocabulary for adapters that accept
wider input; the tests prove absence by exhaustive matches with no wildcard
arm (adding such a variant breaks the test file's compilation) plus
constructor-signature shape assertions. `InvalidOperation` is reachable only
as the mapping of `PlatformError::InvalidRequest`.

### Forwarding order proof

The in-process `ScriptedPlatform` double implements the crate-private
`PlatformPort`, records the collaborator order and everything it receives
(the caller by reference identity, principal, tenant claim, credential kind,
and the forwarded operation), and answers from a fixed script.
`protocol_forwarding_calls_platform_port_before_engine` asserts the recorded
order is exactly `["platform.dispatch"]`: the double is the only observable
collaborator. This module imports no engine, storage, or family-store
symbol, so nothing can reach the Engine before, instead of, or after the
platform dispatch; `approved_typed_operation_with_stable_identity_is_forwarded_and_answered`
asserts the platform receives the operation unchanged with its stable
identity and that the scripted response is returned.

### Error mapping: provisional versus W5-owned

W5 owns the pure `map_engine_error` / `map_platform_error` functions and
their exhaustive red tests. W3-3 uses a private
`forward_platform_error` inside the protocol module — the minimal mapping
`call` needs to return its typed error — whose rows follow the contract's
Platform-to-Protocol table exactly: every denial (`CallerDenied`,
`TenantDenied`, `PermissionDenied`, `Expired`, `ConfirmationRequired`,
`AuditFailure`, `SecretPolicyViolation`) collapses to `PlatformDenied`; a
malformed request becomes `InvalidOperation`; a conflict surfaces as
`EngineFailure` carrying the safe identity; and `Deadline`, `Cancelled`, and
`EngineFailure` preserve their optional identity. The match is exhaustive
with no wildcard arm, so a new `PlatformError` variant breaks compilation
rather than being silently mis-mapped. `platform_error_forwarding_follows_the_contract_rows`
is the provisional evidence; W5 supersedes or absorbs it.

## Evidence

In-crate tracer tests at `src/adapters/protocol/tests.rs` (the five named
tests verbatim, plus the unnamed required behaviors):

| Test | Proves |
| --- | --- |
| `caller_context_try_new_structurally_validates_and_redacts_evidence` | Structural validation of principal/credential emptiness and redaction of every observable form (no secret substring, fingerprint present) |
| `public_operation_constructors_reject_generic_payloads_and_permissions` | Typed rejections at the reachable constructors (V1 text grammar, family payloads), closed registry match, constructor signatures carry no route/body/handle/permission, evidence states no permission |
| `protocol_call_accepts_only_opaque_caller_context` | The call seam's signature accepts only the opaque borrowed context and the closed operation; the exact caller reference reaches the platform unchanged |
| `protocol_forwarding_calls_platform_port_before_engine` | Exactly one platform dispatch per call, recorded as the only collaborator action, with the scripted response returned |
| `protocol_rejects_generic_route_body_and_storage_handle` | Everything the platform receives stays one of the four typed variants; absence proven by exhaustive match and constructor shapes |
| `protocol_rejects_private_control_plane_operations` | The registry holds no private control-plane variant; the refusal vocabulary stays distinct |
| `approved_typed_operation_with_stable_identity_is_forwarded_and_answered` | A mutating family operation forwards unchanged, identity preserved, response returned |
| `malformed_or_unknown_operations_are_rejected_without_generic_forwarding` | An elapsed deadline is refused before dispatch, reads reporting no identity, with no platform access |
| `deadline_and_cancellation_errors_preserve_operation_identity` | Deadline refusal end-to-end with identity preserved; the cancellation refusal decision at both bounds, deadline first |
| `protocol_adapter_supports_in_process_and_production_transports` | The `cfg(test)` port double path runs; `from_platform` compiles as the production-shaped acquisition path |
| `platform_error_forwarding_follows_the_contract_rows` | Every provisional mapping row, each after exactly one platform dispatch |
| `credential_fingerprint_is_stable_prefixed_and_secret_free` | Fingerprint stability, fixed prefix and width, difference across secrets, empty-input basis |

Checks (all from the worktree root, exit code 0): `cargo fmt --all --
--check`; `cargo check --workspace --locked`; `cargo test --workspace
--locked --release` (171 passed, 0 failed — 159 pre-existing plus the 12
above); `cargo clippy --workspace --all-targets --all-features --locked --
-D warnings`; `cargo doc --workspace --no-deps --locked`; `python3
scripts/validate_parity.py --check`.

## Non-claims and ownership boundaries

- No authorization, tenant-isolation, confirmation, audit, secret-handling,
  or grant-opacity claim: PB-1001 and PB-3042 belong to the W5 Platform
  Adapter packet. `ProtocolError::PlatformDenied` is produced only by
  mapping Platform's own refusals; this adapter never decides.
- No grant creation, storage, minting, or exposure anywhere in this module.
- No Engine access from this module: no import, no trait object, no
  `cfg(test)` engine path.
- No parity claim: PB-1000 stays `unresolved` like PB-1003..PB-1022. This
  packet provides typed-forwarding contract evidence only; a Proof ID
  requires the integrator's overlay update naming the exact test, source
  identity, and scenario.
- No claim that cancellation proves rejection: a `Cancelled` refusal only
  reports the signal state.

## Limits and assumptions

- Only family operations are constructible at this base
  (`DocumentReadRequest::try_new`, `DocumentWriteRequest::try_new`, and
  `QueryOperation::try_new` in `src/operations.rs` are still declaration
  only), so the forwarding evidence rides family read and write operations.
  The forwarding path is variant-independent — `call` does not branch on the
  registry variant beyond the exhaustive bounds and identity matches — and
  the document and query variants are covered structurally by those
  exhaustive matches.
- The cancellation pre-dispatch refusal cannot be exercised end-to-end at
  this base: every public operation constructor rejects an already-cancelled
  signal, and `Cancellation` clones are independent snapshots by design, so
  no cancel handle survives construction into a request. The deadline half
  is proven end-to-end; the cancellation half is proven at the pure
  `bound_refusal` decision and through the scripted
  `PlatformError::Cancelled` mapping row. This unreachability is itself a
  safety property: a cancelled signal cannot be smuggled into dispatch.
- `from_platform` cannot be executed until W5 implements
  `PlatformAdapter::new`; the proof is the compiled acquisition shape plus
  the documented bridge impl.
- A future async Protocol Adapter must keep bounded blocking capacity,
  deadlines, cancellation, and a typed overload error per the core contract;
  nothing here may be read as changing commit outcome semantics.

## Collision actions proposed (not taken)

1. `Cargo.toml` (read-only for this packet): to make
   `tests/adapters/protocol/main.rs` a compiled integration target, add a
   `[[test]]` entry naming that path. Not added because the manifest is
   outside the write scope; the note in that file records the same.
2. `src/adapters/platform/mod.rs` (W5-owned): W5 may move the
   `impl PlatformPort for PlatformAdapter<'_>` bridge from this module into
   the platform wiring. No behavior change is required either way.
