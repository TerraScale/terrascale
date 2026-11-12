using System.Globalization;
using System.Text.Json;
using Microsoft.AspNetCore.Http.Metadata;
using Microsoft.EntityFrameworkCore;
using Microsoft.IdentityModel.JsonWebTokens;
using Microsoft.IdentityModel.Tokens;
using TerraScale.Authentication.Configuration;
using TerraScale.Authentication.Contracts;
using TerraScale.Authentication.Features.Identity;
using TerraScale.Authentication.Features.Mfa;
using TerraScale.Authentication.Features.Oidc;
using TerraScale.Authentication.Features.Platform;
using TerraScale.Authentication.Features.SignIn;
using TerraScale.Authentication.Features.SigningKeys;
using TerraScale.Authentication.Storage;
using TerraScale.Authentication.Hosting;
using TerraScale.Endpoints;

namespace TerraScale.Authentication.Features.ProjectTenants;

public sealed record ProjectStepUpRequest(string ClientId, string? Password = null, string? Code = null)
{
    public override string ToString() => nameof(ProjectStepUpRequest);
}
public sealed record ProjectStepUpBody(string Proof, DateTimeOffset ExpiresAt)
{
    public override string ToString() => nameof(ProjectStepUpBody);
}

public sealed class ProjectStepUpEndpoint(PlatformAuthentication authentication, Sessions sessions, HostedAuth hosted, TotpCredentials mfa,
    StepUpProofs proofs, AuthData data, KeyRing ring, AuthOptions options, TimeProvider clock) : EndpointWithoutRequest<IResult>
{
    public override void Configure()
    {
        Post("/v1/{project}/step-up");
        Name("project_step_up");
        Summary("Reverify a project end user");
        Description("Requires a live project sign-in access token for client_id and either the password or a current allowed TOTP/recovery code. Returns a five-minute ES256 JWT with typ step-up+jwt, the project issuer, client audience, sub, auth_time and amr. Verify its type, signature with the project's JWKS, issuer, audience and expiry. The next refresh reflects the fresh auth_time without extending the absolute session. Subject to the hosted IP and account attempt budgets.");
        Tags("Project tenants");
        Metadata(new AcceptsMetadata(["application/json"], typeof(ProjectStepUpRequest)), new RequiresSecurity(SecuritySchemes.OAuthAccessBearer));
        Produces<ProjectStepUpBody>();
        Produces<ErrorBody>(StatusCodes.Status400BadRequest);
        Produces<ErrorBody>(StatusCodes.Status401Unauthorized);
        Produces<ErrorBody>(StatusCodes.Status403Forbidden);
        Produces<ErrorBody>(StatusCodes.Status429TooManyRequests);
        AllowAnonymous();
    }

    public override async ValueTask<IResult> HandleAsync(CancellationToken cancellationToken)
    {
        this.HttpContext.Response.Headers.CacheControl = "no-store";
        var project = this.HttpContext.GetRouteValue("project") as string;
        if (project is null || Tenancy.Resolved(this.HttpContext) != project)
        {
            throw Denied();
        }

        var request = await this.ReadAsync(cancellationToken);
        if (!PlatformDirectory.IsId(request.ClientId)
            || await authentication.AuthenticateAsync(this.HttpContext.Request, cancellationToken, project, request.ClientId) is not { } caller
            || await sessions.ResolveIdAsync(project, caller.Id, caller.SessionId, cancellationToken) is not { } live)
        {
            throw Denied();
        }

        if (await hosted.LimitIpAsync(SignInFlow.Ip(this.HttpContext), mails: false, cancellationToken) is { } wait)
        {
            return HostedAuth.TooMany(wait);
        }

        var method = "pwd";
        if (request.Password is not null && request.Code is null)
        {
            var checkedPassword = await hosted.CheckPasswordAsync(caller.Email, request.Password, cancellationToken);
            if (checkedPassword.Wait is { } passwordWait)
            {
                return HostedAuth.TooMany(passwordWait);
            }

            if (checkedPassword.User is not { } verified || verified.AuthEpoch != caller.AuthEpoch || verified.AuthStamp != caller.AuthStamp)
            {
                throw Denied();
            }
        }
        else if (request.Code is not null && request.Password is null)
        {
            method = request.Code.Length == 6 ? "totp" : "recovery";
            if (await mfa.PrimaryUserAsync(project, caller.Id, caller.AuthEpoch.ToString(CultureInfo.InvariantCulture),
                    caller.AuthStamp, method, cancellationToken) is null
                || !await mfa.VerifyAsync(caller, request.Code, cancellationToken))
            {
                throw Denied();
            }
        }
        else
        {
            throw Denied();
        }

        var keys = await ring.GetActiveAsync(project, cancellationToken);
        try
        {
            var now = clock.GetUtcNow();
            var current = await data.ReadAsync((db, token) => db.Sessions.Where(session => session.InstanceId == project && session.Id == live.Session.Id)
                .WithVersion().SingleOrDefaultAsync(token), cancellationToken);
            if (current is null || current.Value.Revoked || current.Value.ExpiresAt <= now
                || await proofs.RequirementsAsync(caller, cancellationToken) is not { } requirements
                || !await data.SaveAsync([.. requirements, Change.Update(current, current.Value with { VerifiedAt = now })], cancellationToken))
            {
                throw Denied();
            }

            var expires = now.AddMinutes(5);
            var proof = new JsonWebTokenHandler().CreateToken(new SecurityTokenDescriptor
            {
                TokenType = "step-up+jwt",
                Issuer = OidcIssuer.Issuer(options, project).AbsoluteUri,
                Audience = request.ClientId,
                IssuedAt = now.UtcDateTime,
                NotBefore = now.UtcDateTime,
                Expires = expires.UtcDateTime,
                SigningCredentials = new SigningCredentials(keys[0].SecurityKey, SecurityAlgorithms.EcdsaSha256),
                Claims = new Dictionary<string, object>
                {
                    ["sub"] = caller.Id,
                    ["jti"] = AuthIds.New(),
                    ["auth_time"] = now.ToUnixTimeSeconds(),
                    ["amr"] = new[] { method },
                },
            });
            return Results.Json(new ProjectStepUpBody(proof, expires), ProjectTenantJson.Default.ProjectStepUpBody);
        }
        finally
        {
            foreach (var key in keys)
            {
                key.Dispose();
            }
        }
    }

    private async Task<ProjectStepUpRequest> ReadAsync(CancellationToken cancellationToken)
    {
        try
        {
            if (this.HttpContext.Request.HasJsonContentType())
            {
                using var buffer = new MemoryStream();
                var chunk = new byte[4096];
                int read;
                while ((read = await this.HttpContext.Request.Body.ReadAsync(chunk, cancellationToken)) > 0 && buffer.Length + read <= 16384)
                {
                    buffer.Write(chunk, 0, read);
                }

                if (read == 0 && JsonSerializer.Deserialize(buffer.GetBuffer().AsSpan(0, (int)buffer.Length), ProjectTenantJson.Default.ProjectStepUpRequest) is { } body)
                {
                    return body;
                }
            }
        }
        catch (Exception error) when (error is JsonException or BadHttpRequestException)
        {
            throw new ApiException(400, "invalid_request", "Supply a JSON object with client_id and either password or code.");
        }

        throw new ApiException(400, "invalid_request", "Supply a JSON object of at most 16384 bytes with client_id and either password or code.");
    }

    private static ApiException Denied() => new(401, "step_up_denied", "Present a live project sign-in token and reverify with the password or an allowed factor.");
}
