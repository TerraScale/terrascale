namespace TerraScale.Auth.Organizations;

using System.Net;
using TerraScale.Auth.Api;
using TerraScale.Auth.Configuration;
using TerraScale.Auth.Crypto;
using TerraScale.Auth.Email;
using TerraScale.Auth.Store;
using TerraScale.Auth.Ui;
using TerraScale.Auth.Users;
using TerraScale.DotNet;
using TerraScale.Endpoints;
using static TerraScale.Auth.Organizations.OrganizationSupport;

internal sealed class CreateOrganizationEndpoint : IEndpoint
{
    public RouteHandlerBuilder Map(IEndpointRouteBuilder routes) =>
        routes.MapPost("/v1/{slug}/organizations", HandleAsync);

    private static async Task<IResult> HandleAsync(string slug, HttpRequest httpRequest, AuthStore store, InstanceResolver resolver, CancellationToken ct)
    {
        var context = await resolver.ResolveAsync(httpRequest, ct);
        if (context is null || context.Instance.Slug != slug)
            return ApiResults.Unauthorized();

        var body = await httpRequest.ReadFromJsonAsync<CreateOrganizationRequest>(ct);
        if (body is null || string.IsNullOrWhiteSpace(body.Name))
            return ApiResults.BadRequest("invalid_request", "name is required");
        var name = body.Name.Trim();

        var orgSlug = string.IsNullOrWhiteSpace(body.Slug) ? DeriveSlug(name) : body.Slug.Trim().ToLowerInvariant();
        if (orgSlug is null)
            return ApiResults.BadRequest("invalid_slug", "a slug cannot be derived from this name; provide an explicit slug");
        if (orgSlug.Length is < 3 or > 48 || orgSlug.Any(c => !char.IsAsciiLetterOrDigit(c) && c != '-'))
            return ApiResults.BadRequest("invalid_slug", "slug must be 3-48 lowercase url-safe characters");

        var organization = new Organization
        {
            Id = "org_" + SecureTokens.RandomToken(20),
            InstanceId = context.Instance.Id,
            Name = name,
            SlugKey = $"{context.Instance.Id}:{orgSlug}",
            CreatedAt = Now(),
        };
        // Check-then-act (as instance creation does): the slugKey unique index is the
        // hard guarantee, this catches the common case with a precise error either way.
        if ((await store.ListOrganizationsAsync(context.Instance.Id, ct)).Any(o => o.SlugKey == organization.SlugKey))
            return ApiResults.Conflict("organization_exists", "an organization with this slug already exists");
        try
        {
            await store.CreateOrganizationAsync(organization, ct);
        }
        catch (TerraScaleApiException exception) when (exception.StatusCode is HttpStatusCode.Conflict or HttpStatusCode.PreconditionFailed)
        {
            return ApiResults.Conflict("organization_exists", "an organization with this slug already exists");
        }
        await AuditAsync(store, context.Instance.Id, "organization.created", context.ApiKeyId, new Dictionary<string, object>
        {
            ["organization_id"] = organization.Id,
        }, ct);
        return Results.Ok(Render(organization, context.Instance.Id));
    }

    /// <summary>Lowercase slug from a display name: alphanumerics kept, everything else collapsed to dashes, trimmed to 48. Null when no valid 3-48 slug remains.</summary>
    private static string? DeriveSlug(string name)
    {
        var builder = new System.Text.StringBuilder();
        var pendingDash = false;
        foreach (var c in name.ToLowerInvariant())
        {
            if (char.IsAsciiLetterOrDigit(c))
            {
                if (pendingDash && builder.Length > 0)
                    builder.Append('-');
                pendingDash = false;
                builder.Append(c);
            }
            else
            {
                pendingDash = true;
            }
        }
        var slug = builder.ToString();
        if (slug.Length > 48)
            slug = slug[..48].TrimEnd('-');
        return slug.Length is >= 3 and <= 48 ? slug : null;
    }
}
