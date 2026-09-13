// <copyright file="AuditDtos.cs" company="TerraScale">
// TerraBase (SE-08). See LICENSE.md.
// </copyright>

namespace TerraBase.Api;

/// <summary>The audit answer.</summary>
public sealed record AuditResponse(IReadOnlyList<AuditRecord> Records);
