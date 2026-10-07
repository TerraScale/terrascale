using TerraBase.Tenancy;

namespace TerraBase.Features.Management.Usage;

/// <summary>Counts document operations per database (by tenant) for usage reporting.</summary>
public interface IUsageMeter
{
    /// <summary>Counts one document read (a get, or a list or query page).</summary>
    void RecordRead(TenantContext database);

    /// <summary>Counts <paramref name="writes"/> committed document writes (puts, patches and deletes).</summary>
    void RecordWrites(TenantContext database, int writes);
}
