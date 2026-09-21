namespace TerraScale.Auth.Billing;

using System.Globalization;
using System.Text.Json.Serialization;
using TerraScale.Auth.Api;
using TerraScale.Auth.Configuration;
using TerraScale.Auth.Crypto;
using TerraScale.Auth.Store;
using TerraScale.Auth.Ui;
using TerraScale.DotNet;
using TerraScale.Endpoints;
using static TerraScale.Auth.Billing.BillingSupport;

internal sealed class CheckQuotaEndpoint : IEndpoint
{
    public RouteHandlerBuilder Map(IEndpointRouteBuilder routes) =>
        routes.MapPost("/v1/{slug}/billing/enforce-check", HandleAsync);

    private static async Task<IResult> HandleAsync(
            string slug, HttpRequest request, InstanceResolver resolver, AuthStore store, CancellationToken ct)
    {
        if (await resolver.ResolveAsync(request, ct) is not { } context || context.Instance.Slug != slug)
            return ApiResults.Unauthorized();

        var (body, failure) = await ReadJsonAsync<EnforceCheckBody>(request, ct);
        if (failure is not null)
            return failure;
        if (body?.CurrentUsers is not { } currentUsers || currentUsers < 0)
            return ApiResults.BadRequest("invalid_request", "current_users must be a non-negative integer");

        var state = await BillingState.CurrentAsync(store, context.Instance.Id, ct);
        return Results.Ok(new { allowed = currentUsers < state.Quota, plan = state.Plan, quota = state.Quota });
    }

    private sealed record EnforceCheckBody([property: JsonPropertyName("current_users")] int? CurrentUsers);
}
