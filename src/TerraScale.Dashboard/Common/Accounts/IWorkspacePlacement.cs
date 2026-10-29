namespace TerraScale.Dashboard.Common.Accounts
{
    // Serving regions offered for a project's preferred placement. Projects are
    // ts-auth's; TerraBase needs no project registration before its databases.
    public interface IWorkspacePlacement
    {
        Task<IReadOnlyList<string>> RegionsAsync(CancellationToken cancellationToken);
    }
}
