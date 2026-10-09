using TerraBase.Tenancy;

namespace TerraBase.Engine.Actors;

/// <summary>A tenant-qualified document identity. The tenant's plan does not affect placement.</summary>
/// <param name="Database">The organization, project and database.</param>
/// <param name="Collection">The collection name.</param>
/// <param name="Id">The document id.</param>
public readonly record struct ActorKey(TenantContext Database, string Collection, string Id);
