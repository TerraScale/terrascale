
namespace TerraScale.Dashboard.Features.Databases;

public sealed class WorkspacePlacement(IDatabaseBackend databases) : IWorkspacePlacement
{
    public async Task<IReadOnlyList<string>> RegionsAsync(CancellationToken cancellationToken) =>
        (await databases.RegionsAsync(cancellationToken)).Where(region => region.Healthy).Select(region => region.Code).ToArray();

}
