using TerraBase.Tenancy;

namespace TerraBase.Features.Stats;
public interface IDatabaseStats
{
    void Read(TenantContext database);
    void Wrote(TenantContext database, int writes);
    void Request(TenantContext database, TimeSpan elapsed, bool failed);
    void StorageSampled(TenantContext database, long bytes);
    void CollectionRead(TenantContext database, string collection);
    void CollectionWrote(TenantContext database, string collection);
}
