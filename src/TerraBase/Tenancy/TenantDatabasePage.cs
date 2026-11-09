namespace TerraBase.Tenancy;

public sealed record TenantDatabasePage(IReadOnlyList<TenantContext> Databases, TenantContext? Next);
