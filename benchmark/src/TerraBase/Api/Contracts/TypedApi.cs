// <copyright file="TypedApi.cs" company="TerraScale">
// TerraBase (SE-08). See LICENSE.md.
// </copyright>

namespace TerraBase.Api;

/// <summary>
/// The typed public API of the C# host (SE-08): every HTTP operation is
/// a typed TBEP operation on the engine through the SE-14 client, every
/// stream is a TBEP backbone subscription, and every sync session is a
/// forwarded terrascale.sync.v2 serving session. The boundary is local
/// and backend-only (PD-2026-08-26-11 D1): it does not implement external
/// authentication. The local gate checks that the backend-supplied context
/// (principal, tenant, and, for restricted routes, role) is present and
/// valid before any engine frame is sent, the engine enforces tenant
/// isolation from that context, and customer auth/PB-3042 lives exclusively
/// at the TerraScale Platform (no claim is made here).
/// </summary>
public sealed partial class TypedApi
{
    private readonly EngineSupervisor supervisor;
    private readonly BackboneHub hub;
    private readonly AuditLog audit;

    internal TypedApi(EngineSupervisor supervisor, BackboneHub hub, AuditLog audit)
    {
        this.supervisor = supervisor;
        this.hub = hub;
        this.audit = audit;
    }
}
