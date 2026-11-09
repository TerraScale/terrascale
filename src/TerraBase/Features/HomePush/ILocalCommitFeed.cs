using TerraBase.Engine;
using TerraBase.Tenancy;

namespace TerraBase.Features.HomePush;

internal interface ILocalCommitFeed
{
    void Committed(TenantContext database, string transactionId, IReadOnlyList<CapturedRowChange> captured);
}
