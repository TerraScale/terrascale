using TerraScale.Dashboard.Shared.UI;

namespace TerraScale.Dashboard.Features.QuerySubscriptions;

public sealed record QuerySubscriptionPage(ShellModel Shell, QuerySubscriptionList Configurations);

public static class QuerySubscriptionEndpoints
{
    public static IServiceCollection AddQuerySubscriptions(this IServiceCollection services) => services.AddSingleton<QuerySubscriptionService>();

    public static IEndpointRouteBuilder MapQuerySubscriptions(this IEndpointRouteBuilder endpoints)
    {
        endpoints.MapMethods("/subscriptions", ["GET", "HEAD"], async (HttpContext http, DashboardSessions sessions, AccountService accounts, QuerySubscriptionService service) =>
        {
            var context = await AccountRequest.RequireAsync(http, sessions, accounts);
            if (context is null) return AccountRequest.LoginRedirect(http);
            try
            {
                var configurations = await service.ListAsync(context, http.RequestAborted);
                return (IResult)QuerySubscriptionViewPage.Create(new(ShellModel.For(http, context, "Query subscriptions"), configurations));
            }
            catch (AccountAccessException error) { return Results.Text(error.Message + "\n", statusCode: error.StatusCode); }
        });
        return endpoints;
    }
}
