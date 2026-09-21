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

internal sealed class DeleteMfaFactorEndpoint : IEndpoint
{
    public RouteHandlerBuilder Map(IEndpointRouteBuilder routes) =>
        routes.MapDelete("/v1/{slug}/users/{userId}/mfa-factors/{id}", HandleAsync);

    private static async Task<IResult> HandleAsync(string slug, string userId, string id, HttpRequest request, InstanceResolver resolver, AuthStore store, CancellationToken ct)
        => await WithScopeAsync(resolver, request, store, slug, userId, async context =>
            {
                var factor = await store.GetMfaFactorAsync(id, ct);
                if (factor is null || factor.InstanceId != context.Instance.Id || factor.UserId != userId)
                    return ApiResults.NotFound();

                await store.DeleteMfaFactorAsync(id, ct);
                await AuditAsync(store, context.Instance.Id, "mfa.factor_deleted", context.ApiKeyId, new() { ["factorId"] = id, ["userId"] = userId }, ct);
                return Results.NoContent();
            }, ct);
}
