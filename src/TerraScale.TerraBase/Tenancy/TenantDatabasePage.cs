namespace TerraBase.Tenancy;

/// <summary>A bounded page of database identities from a snapshot of the management catalog.</summary>
/// <param name="Databases">At most 100 identities, each containing three UUIDs.</param>
/// <param name="Next">The last identity to resume after, or null once the scan ended.</param>
public sealed record TenantDatabasePage(IReadOnlyList<TenantContext> Databases, TenantContext? Next);
