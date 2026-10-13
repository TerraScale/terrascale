namespace TerraScale.Dashboard.Shared.Accounts;

public interface IWorkspacePlacement
{
    Task<IReadOnlyList<string>> RegionsAsync(CancellationToken cancellationToken);
    Task RegisterAsync(AccountContext context, ProjectRecord project, CancellationToken cancellationToken);
}
