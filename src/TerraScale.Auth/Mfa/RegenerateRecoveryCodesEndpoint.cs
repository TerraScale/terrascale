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

internal sealed class RegenerateRecoveryCodesEndpoint : IEndpoint
{
    public RouteHandlerBuilder Map(IEndpointRouteBuilder routes) =>
        routes.MapPost("/v1/{slug}/users/{userId}/mfa-factors/{id}/regenerate-recovery", HandleAsync);

    private static async Task<IResult> HandleAsync(string slug, string userId, string id, HttpRequest request, InstanceResolver resolver, AuthStore store, CancellationToken ct)
        => await WithScopeAsync(resolver, request, store, slug, userId, async context =>
            {
                var factor = await store.GetMfaFactorAsync(id, ct);
                if (factor is null || factor.InstanceId != context.Instance.Id || factor.UserId != userId)
                    return ApiResults.NotFound();
                if (factor.Type != "recovery")
                    return ApiResults.BadRequest("invalid_factor", "recovery codes can only be regenerated for a recovery factor");

                var codes = NewRecoveryCodes();
                await store.UpsertMfaFactorAsync(factor with { RecoveryCodes = codes.Select(SecureTokens.Sha256Hex).ToArray() }, ct);
                return Results.Ok(new { data = codes });
            }, ct);
}
