using TerraBase.Engine;
using TerraBase.Tenancy;

namespace TerraBase.Features.HomePush;

/// <summary>Told about each local transaction once the replica has committed it and the push stream holds it.</summary>
internal interface ILocalCommitFeed
{
    void Committed(TenantContext database, string transactionId, IReadOnlyList<CapturedRowChange> captured);
}
