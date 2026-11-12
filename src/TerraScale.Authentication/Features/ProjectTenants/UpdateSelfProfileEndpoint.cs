using System.Text.Json;
using Microsoft.AspNetCore.Http.Metadata;
using TerraScale.Authentication.Configuration;
using TerraScale.Authentication.Contracts;
using TerraScale.Authentication.Features.Identity;
using TerraScale.Authentication.Features.Oidc;
using TerraScale.Authentication.Hosting;
using TerraScale.Endpoints;
using TerraScale.DotNet;

namespace TerraScale.Authentication.Features.ProjectTenants;

public sealed class UpdateSelfProfileEndpoint(Sessions sessions, UserProfiles profiles, AuthOptions options, TimeProvider clock)
    : EndpointWithoutRequest<IResult>
{
    public override void Configure()
    {
        Patch("/v1/{project}/me/profile");
        Name("update_self_profile");
        Summary("Update your name and unsafe metadata");
        Description("Requires the project's live hosted session cookie and an exact same-origin Origin header. Only first_name, last_name and unsafe_metadata are accepted. Metadata uses JSON merge patch, limited to 8192 UTF-8 bytes.");
        Tags("Project tenants");
        Metadata(new RequiresSecurity(SecuritySchemes.SessionCookie));
        Metadata(new AcceptsMetadata(["application/json"], typeof(SelfProfilePatch)));
        Produces<SelfProfileBody>();
        Produces<ErrorBody>(400);
        Produces<ErrorBody>(401);
        Produces<ErrorBody>(403);
        Produces<ErrorBody>(409);
    }

    public override async ValueTask<IResult> HandleAsync(CancellationToken cancellationToken)
    {
        this.HttpContext.Response.Headers.CacheControl = "no-store";
        var request = this.HttpContext.Request;
        if (request.Headers.Origin.Count != 1 || request.Headers.Origin[0] != options.PublicOrigin.GetLeftPart(UriPartial.Authority))
        {
            throw new ApiException(403, "origin_denied", "Use the hosted account origin.");
        }

        var project = this.HttpContext.GetRouteValue("project") as string;
        if (project is null || !TerraScaleIds.IsValid(project)
            || await sessions.ResolveVersionedAsync(project, request.Cookies[Tenancy.SessionCookie(project)], cancellationToken) is not { } live)
        {
            throw new ApiException(401, "unauthorized", "Sign in to this project.");
        }

        if (!request.HasJsonContentType())
        {
            throw new ApiException(400, "invalid_request", "Supply a JSON object.");
        }

        using var buffer = new MemoryStream();
        var chunk = new byte[4096];
        int read;
        while ((read = await request.Body.ReadAsync(chunk, cancellationToken)) > 0 && buffer.Length + read <= 16 * 1024)
        {
            buffer.Write(chunk, 0, read);
        }

        if (read > 0)
        {
            throw new ApiException(400, "invalid_request", "Profile patch exceeds 16384 bytes.");
        }

        SelfProfilePatch patch;
        try
        {
            patch = JsonSerializer.Deserialize(buffer.GetBuffer().AsSpan(0, (int)buffer.Length), UserProfileJson.Default.SelfProfilePatch)
                ?? throw new JsonException();
        }
        catch (JsonException)
        {
            throw new ApiException(400, "invalid_request", "Only first_name, last_name and unsafe_metadata are accepted.");
        }

        var updated = await profiles.UpdateSelfAsync(live.User, patch, Sessions.Live(live.Session, live.Version, clock.GetUtcNow()), cancellationToken);
        return Results.Json(SelfProfileBody.From(updated), UserProfileJson.Default.SelfProfileBody);
    }
}
