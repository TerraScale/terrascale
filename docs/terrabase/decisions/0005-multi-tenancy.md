# Tenant isolation

A database belongs to one project and organization. Every storage operation carries the resolved tenant context; a raw name or path cannot select another tenant's file. Project credentials are restricted to their authorized project and optional immutable database ID.

Customer SQL tables live inside that database. System catalog and service-owned state remain separate. Cross-service reads use APIs, never another product's storage. The shared-process scheduler, memory budget and quota leases preserve the owning organization through queued and background work.

Authorization runs before returning existence, placement, SQL metadata or rows. Unresolved authority fails closed. See [cross-project errors](../cross-project-errors.md), [management](../management-api.md) and [shared-process tenancy](../shared-process-tenancy.md).
