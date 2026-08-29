---
status: superseded
superseded_by: ADR-0019
---

# Separate engine, Platform, and control plane

> Superseded on 2026-08-29 by the workspace `GOAL.md` and ADR 0019.
> `TerraScale.Database` is now the sole C#/.NET TBEP gateway, Dashboard owns
> public product identity and authorization grants, and the former Platform
> Adapter is not an active product boundary. The original text remains below
> as historical evidence.

The V2 Engine owns database behavior. The approved typed Platform Adapter owns
the client-facing integration and its authorization, tenant isolation,
confirmation, audit, and secret-handling rules. The control plane owns
provisioning, lifecycle, billing, routing, tenant onboarding, and external
authentication policy. This separation prevents a database process from
silently becoming a private control-plane interface and keeps each contract
verifiable at its own seam.
