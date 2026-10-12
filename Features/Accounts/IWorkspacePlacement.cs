namespace TerraScale.Dashboard.Features.Accounts;

public interface IWorkspacePlacement
{
    Task<IReadOnlyList<string>> RegionsAsync(CancellationToken cancellationToken);
    Task RegisterAsync(AccountContext context, ProjectRecord project, CancellationToken cancellationToken);
}
