using TerraScale.TerraBase.Tenancy;

namespace TerraScale.TerraBase.Hosting;

/// <summary>A per-host fault injection point for integration tests; production registers a no-op.</summary>
internal delegate void DatabaseLifecycleCheckpoint(string step, TenantContext database);
