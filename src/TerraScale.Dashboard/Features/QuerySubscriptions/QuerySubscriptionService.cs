using TerraScale.Dashboard.Features.Accounts;

namespace TerraScale.Dashboard.Features.QuerySubscriptions;

public sealed class SharedResultConfiguration
{
    public string Id { get; set; } = "";
    public string DatabaseId { get; set; } = "";
    public string Name { get; set; } = "";
    public string Query { get; set; } = "";
    public string Transport { get; set; } = "";
    public string Status { get; set; } = "saved";
    public DateTimeOffset CreatedAt { get; set; }
}

public sealed record QuerySubscriptionView(string Id, string DatabaseId, string DatabaseName,
    string Name, string Query, string Transport, string Status, DateTimeOffset CreatedAt);
public sealed record QuerySubscriptionList(IReadOnlyList<QuerySubscriptionView> Subscriptions);

public sealed class QuerySubscriptionService(AccountService accounts)
{
    public const string Unavailable = "Live shared-result configuration is unavailable.";

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

    public async Task RefuseMutationAsync(AccountContext context, CancellationToken cancellationToken)
    {
        var fresh = await FreshAsync(context, cancellationToken);
        if (!fresh.CanWrite) throw new AccountAccessException("Your role can view shared results but not change them.");
        // The published producer/SDK contract offers no query-subscription
        // delivery or management API. A saved local record cannot provision it.
        throw new AccountAccessException(Unavailable, StatusCodes.Status503ServiceUnavailable);
    }

    private async Task<AccountContext> FreshAsync(AccountContext context, CancellationToken cancellationToken)
    {
        var fresh = await accounts.GetContextAsync(context.Session, cancellationToken);
        var organization = fresh.Organizations.SingleOrDefault(item => item.Id == context.Organization?.Id)
            ?? throw new AccountAccessException("Organization is unavailable.", 404);
        var project = organization.Projects.GetValueOrDefault(context.Project?.Id ?? "");
        if (project is null || project.OrganizationId != organization.Id)
            throw new AccountAccessException("Project is unavailable.", 404);
        return new(fresh.Session, fresh.Account, fresh.Organizations, organization, project);
    }
}
