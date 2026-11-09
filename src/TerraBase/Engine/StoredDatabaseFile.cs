using TerraBase.Tenancy;

namespace TerraBase.Engine;

public sealed record StoredDatabaseFile(string Path, TenantContext? Database);
