namespace TerraBase.Engine;

public sealed record StoragePressure(long? PhysicalBytes, IReadOnlyList<StoredTenantPressure> TopTenants);

public sealed record StoredTenantPressure(string Org, long Bytes);
