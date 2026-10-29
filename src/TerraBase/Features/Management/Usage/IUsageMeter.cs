using TerraBase.Tenancy;

namespace TerraBase.Features.Management.Usage;

public interface IUsageMeter
{
    void RecordRead(TenantContext database, long units);

    void RecordWrites(TenantContext database, int writes, long units);

    void RecordOperation(TenantContext database, string kind, int count);

    void RecordRequest(TenantContext database, string method, int status, TimeSpan elapsed, long received, long sent);

    void RecordStatement(TenantContext database, string protocol, bool failed, TimeSpan elapsed);

    void RecordClientBytes(TenantContext database, long received, long sent, DateTimeOffset? transferredAt = null);

    IDisposable TrackActive(Action report);

}

public static class OperationUnits
{
    public const int Bytes = 4096;

    public static long Of(long bytes) => (bytes + Bytes - 1) / Bytes;
}
