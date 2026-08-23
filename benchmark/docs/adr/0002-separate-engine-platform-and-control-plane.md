---
status: accepted
---

# Separate engine, Platform, and control plane

The V2 Engine owns database behavior. The approved typed Platform Adapter owns
the client-facing integration and its authorization, tenant isolation,
confirmation, audit, and secret-handling rules. The control plane owns
provisioning, lifecycle, billing, routing, tenant onboarding, and external
authentication policy. This separation prevents a database process from
silently becoming a private control-plane interface and keeps each contract
verifiable at its own seam.
