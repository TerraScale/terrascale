// <copyright file="TypedApi.Audit.cs" company="TerraScale">
// TerraBase (SE-08). See LICENSE.md.
// </copyright>

namespace TerraBase.Api;

public sealed partial class TypedApi
{
    internal IResult Audit(HttpContext context, int limit = 100)
    {
        if (this.Gate(context, "observer", "operator") is { } refused)
        {
            return refused;
        }

        var records = this.audit
            .Recent(Math.Clamp(limit, 1, AuditLog.Capacity))
            .Select(PublicAudit)
            .ToArray();
        return Results.Json(new AuditResponse(records));
    }
}
