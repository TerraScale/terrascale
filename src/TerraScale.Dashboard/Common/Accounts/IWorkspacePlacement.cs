namespace TerraScale.Dashboard.Common.Accounts
{
    public interface IWorkspacePlacement
    {
        Task<IReadOnlyList<string>> RegionsAsync(CancellationToken cancellationToken);
    }
}
