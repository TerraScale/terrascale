namespace TerraScale.Auth.Management;

using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.AspNetCore.Mvc;
using TerraScale.Auth.Api;
using TerraScale.Auth.Billing;
using TerraScale.Auth.Crypto;
using TerraScale.Auth.Store;
using TerraScale.Auth.Users;
using TerraScale.Endpoints;
using static TerraScale.Auth.Management.ManagementSupport;

internal sealed class CreateUserEndpoint : IEndpoint
{
    public RouteHandlerBuilder Map(IEndpointRouteBuilder routes) =>
        routes.MapPost("/v1/{slug}/users", HandleAsync);

    private static async Task<IResult> HandleAsync(
            string slug, HttpRequest request, InstanceResolver resolver, AuthStore store, SessionService sessions, CancellationToken ct)
    {
        var gate = await RequireInstanceAsync(request, slug, resolver, ct);
        if (gate.Failure is { } failure)
            return failure;
        var context = gate.Context!;

        var (body, bodyFailure) = await ReadBodyAsync<CreateUserBody>(request, ct);
        if (body is null)
            return bodyFailure ?? ApiResults.BadRequest("invalid_request", "a JSON body is required");

        if (body.Email is not null)
        {
            if (!SessionService.IsValidEmail(body.Email))
                return ApiResults.BadRequest("invalid_email", "email is not a valid address");
            if (await store.FindUserByEmailAsync(context.Instance.Id, SessionService.NormalizeEmail(body.Email), ct) is not null)
                return ApiResults.Conflict("email_taken", "an account with this email already exists");
        }
        User created;
        try
        {
            created = await sessions.CreateUserAsync(context.Instance.Id, body.Email, body.Username, body.Password, body.Name, ct);
        }
        catch (QuotaExceededException)
        {
            return ApiResults.Error(402, "quota_exceeded", "the instance has reached its plan's user quota; upgrade to add more users");
        }
        catch (TerraScale.DotNet.TerraScaleApiException)
        {
            // ponytail: unique-index race between the email pre-check and the create; the engine
            // does not say which key conflicted yet, so both email and username map here.
            return ApiResults.Conflict("identifier_taken", "email or username already exists");
        }

        await AuditAsync(store, context, "user.created", new() { ["user_id"] = created.Id }, ct);
        return Results.Json(UserView(created, includePrivate: true), statusCode: 201);
    }

    // ---- Request bodies ------------------------------------------------------------

    private sealed record CreateUserBody(string? Email, string? Username, string? Password, string? Name);
}
