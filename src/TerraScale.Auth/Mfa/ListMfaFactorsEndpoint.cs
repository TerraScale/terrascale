namespace TerraScale.Auth.Mfa;

using TerraScale.Auth.Api;
using TerraScale.Auth.Configuration;
using TerraScale.Auth.Crypto;
using TerraScale.Auth.Keys;
using TerraScale.Auth.Store;
using TerraScale.Auth.Ui;
using TerraScale.Auth.Users;
using TerraScale.Endpoints;
using static TerraScale.Auth.Mfa.MfaSupport;

internal sealed class ListMfaFactorsEndpoint : IEndpoint
{
    public RouteHandlerBuilder Map(IEndpointRouteBuilder routes) =>
        routes.MapGet("/v1/{slug}/users/{userId}/mfa-factors", HandleAsync);

    private static async Task<IResult> HandleAsync(string slug, string userId, HttpRequest request, InstanceResolver resolver, AuthStore store, CancellationToken ct)
        => await WithScopeAsync(resolver, request, store, slug, userId, async context =>
            {
                var factors = await store.ListMfaFactorsAsync(userId, ct);
                return Results.Ok(new { data = factors.Select(f => new { f.Id, f.Type, f.Confirmed, f.CreatedAt }) });
            }, ct);
}
