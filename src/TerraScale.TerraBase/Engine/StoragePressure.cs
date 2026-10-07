namespace TerraBase.Engine;

/// <summary>A cached physical-storage sample, independent of logical document sizes.</summary>
/// <param name="PhysicalBytes">Total tenant database, WAL and SHM file lengths; null before a complete scan or after a failed scan. Excludes the system database.</param>
/// <param name="TopTenants">At most ten organizations, largest first; empty when physical storage is unknown.</param>
public sealed record StoragePressure(long? PhysicalBytes, IReadOnlyList<StoredTenantPressure> TopTenants);

/// <summary>Physical storage attributed to one organization.</summary>
/// <param name="Org">The organization's lowercase hyphenated UUID.</param>
/// <param name="Bytes">Sum of its database, WAL and SHM file lengths.</param>
public sealed record StoredTenantPressure(string Org, long Bytes);
