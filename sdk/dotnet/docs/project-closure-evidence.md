# Project closure evidence during rollout

`TerraBaseProjectClosure` adds optional `int EvidenceVersion = 0`, read from the
additive JSON field `evidenceVersion` through the existing source-generated
TerraBase context. Responses from older engines that omit the field deserialize
with version 0. New engine responses identify the revised evidence with version 2.
Existing three-argument constructor calls still compile; `Accepted` continues to
mean only `Status == "accepted"` for existing SDK consumers.
The positional record gains a fourth constructor/deconstruction parameter;
recompile consumers and update any positional deconstruction to four fields.

Destructive consumers such as ts-auth must explicitly require both `Accepted`
and `EvidenceVersion == 2` before removing a project after either
`CloseProjectAsync` (request version 1) or `EraseProjectAsync` (request version 2).
Request version and response evidence version are separate. A legacy accepted
response with missing evidence remains readable, but cannot authorize that
cleanup; auth must retain it as pending until qualifying evidence arrives.
Unknown evidence versions also fail this exact version check.

This permits auth and engine upgrades at different times without treating an old
engine's accepted response as the revised evidence. The SDK does not alter
existing acceptance semantics or perform cleanup itself. HTTP-handler coverage
checks accepted and pending responses, with omitted evidence and version 2, for
both request versions. Producer integration and auth enforcement belong to the
parent change; these SDK tests do not prove live erasure. This ships with the
outbox client as package 0.0.11, a new immutable version for dashboard vendoring.
No storage migration is introduced; see [the package handoff](deletion-outbox.md).
