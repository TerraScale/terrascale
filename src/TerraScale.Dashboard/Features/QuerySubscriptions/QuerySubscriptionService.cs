namespace TerraScale.Dashboard.Features.QuerySubscriptions;

public sealed record QuerySubscriptionView(string Id, string DatabaseId, string DatabaseName,
    string Name, string Query, string Transport, string Status, DateTimeOffset CreatedAt);
public sealed record QuerySubscriptionList(IReadOnlyList<QuerySubscriptionView> Subscriptions);

public sealed class QuerySubscriptionService(AccountService accounts)
{
    public async Task<QuerySubscriptionList> ListAsync(AccountContext context, CancellationToken cancellationToken)
    {
        var project = (await FreshAsync(context, cancellationToken)).Project!;
        var subscriptions = project.SharedResultConfigs.OrderBy(item => item.Value.Name, StringComparer.Ordinal)
            .Select(item =>
            {
                var config = item.Value;
                if (config.Id != item.Key) throw new IOException("Shared-result configuration identity is invalid.");
                var databaseName = project.Databases.TryGetValue(config.DatabaseId, out var database) && database.ProjectId == project.Id
                    ? database.Name : "Unavailable database";
                return new QuerySubscriptionView(config.Id, config.DatabaseId, databaseName, config.Name,
                    config.Query, config.Transport, config.Status, config.CreatedAt);
            }).ToArray();
        return new(subscriptions);
    }

    private Task<AccountContext> FreshAsync(AccountContext context, CancellationToken cancellationToken) =>
        AccountScope.RefreshAsync(accounts, context, cancellationToken, requireProject: true);
}
