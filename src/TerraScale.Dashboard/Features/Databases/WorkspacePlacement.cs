using TerraScale.Dashboard.Features.Accounts;

namespace TerraScale.Dashboard.Features.Databases;

public sealed class WorkspacePlacement(IDatabaseBackend databases) : IWorkspacePlacement
{
    public async Task<IReadOnlyList<string>> RegionsAsync(CancellationToken cancellationToken) =>
        (await databases.RegionsAsync(cancellationToken)).Where(region => region.Healthy).Select(region => region.Code).ToArray();

    public async Task RegisterAsync(AccountContext context, ProjectRecord project, CancellationToken cancellationToken)
    {
        if (!context.CanManage || context.Organization is null || project.OrganizationId != context.Organization.Id)
            throw new AccountAccessException("Project management is required.");
        var catalog = await databases.RegionsAsync(cancellationToken);
        var codes = project.Regions.Count > 0 ? project.Regions : [project.Region];
        if (codes.Count == 0 || codes.Any(string.IsNullOrEmpty)) throw new AccountAccessException("Choose at least one serving region.", 422);
        var scope = new DatabaseScope(context.UserId, context.Session.AccessToken, context.Organization.Id, context.Organization.Name, project.Id, project.Name);
        foreach (var code in codes.Distinct(StringComparer.Ordinal))
        {
            // A project's stored regions keep their own region; only an id that names no TerraBase region is refused.
            var region = DatabaseRegion.Resolve(catalog, code) is { Healthy: true } serving ? serving : throw new AccountAccessException("Choose a serving region listed by discovery.", 422);
            await databases.RegisterAsync(scope, region, cancellationToken);
        }
    }
}
