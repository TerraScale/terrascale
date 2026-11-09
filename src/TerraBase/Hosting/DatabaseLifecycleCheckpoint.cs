using TerraBase.Tenancy;

namespace TerraBase.Hosting;

internal delegate void DatabaseLifecycleCheckpoint(string step, TenantContext database);
