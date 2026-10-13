using Microsoft.AspNetCore.Http.Features;
using TerraScale.Dashboard.Features.Accounts;
using TerraScale.Dashboard.UI;

namespace TerraScale.Dashboard.Features.QuerySubscriptions;

public sealed record QuerySubscriptionPage(ShellModel Shell, QuerySubscriptionList Configurations);

public static class QuerySubscriptionEndpoints
{
    public static IServiceCollection AddQuerySubscriptions(this IServiceCollection services) => services.AddSingleton<QuerySubscriptionService>();

    public static IEndpointRouteBuilder MapQuerySubscriptions(this IEndpointRouteBuilder endpoints)
    {
        endpoints.MapMethods("/subscriptions", ["GET", "HEAD"], async (HttpContext http, DashboardSessions sessions, AccountService accounts, QuerySubscriptionService service) =>
        {
            var context = await AccountEndpoints.RequireAsync(http, sessions, accounts);
            if (context is null) return AccountEndpoints.LoginRedirect(http);
            try
            {
                var configurations = await service.ListAsync(context, http.RequestAborted);
                return (IResult)QuerySubscriptionViewPage.Create(new(ShellModel.For(http, context, "Query subscriptions"), configurations));
            }
            catch (AccountAccessException error) { return Results.Text(error.Message + "\n", statusCode: error.StatusCode); }
        });
        foreach (var action in new[] { "attach", "pause", "resume", "detach" })
            endpoints.MapPost("/api/v1/dashboard/subscriptions/" + action, RefuseAsync);
        return endpoints;
    }

    private static async Task<IResult> RefuseAsync(HttpContext http, DashboardSessions sessions, AccountService accounts, QuerySubscriptionService service)
    {
        var context = await AccountEndpoints.RequireAsync(http, sessions, accounts);
        if (context is null) return Results.Unauthorized();
        if (http.Features.Get<IHttpMaxRequestBodySizeFeature>() is { IsReadOnly: false } body) body.MaxRequestBodySize = 16 * 1024;
        var form = await http.Request.ReadFormAsync(http.RequestAborted);
        if (!Csrf.Validate(http, form)) return Results.BadRequest("Invalid form token.");
        try { await service.RefuseMutationAsync(context, http.RequestAborted); }
        catch (AccountAccessException error) { return Results.Text(error.Message + "\n", statusCode: error.StatusCode); }
        throw new InvalidOperationException("An unavailable producer mutation cannot complete.");
    }
}
