using TerraBase.Features.Backup;

namespace TerraBase.Engine;

internal interface IWrappedKeyFiles
{
    Task RewrapAsync(KeyInventory inventory, CancellationToken cancellationToken);
}
