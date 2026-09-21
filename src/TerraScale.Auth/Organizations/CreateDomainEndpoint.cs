namespace TerraScale.Auth.Organizations;

using System.Net;
using TerraScale.Auth.Api;
using TerraScale.Auth.Crypto;
using TerraScale.Auth.Store;
using TerraScale.DotNet;
using TerraScale.Auth.Templates;
using TerraScale.Endpoints;
using static TerraScale.Auth.Organizations.OrganizationDomainSupport;

internal sealed class CreateDomainEndpoint : IEndpoint
{
    public RouteHandlerBuilder Map(IEndpointRouteBuilder routes) =>
        routes.MapPost("/v1/{slug}/organizations/{orgId}/domains", HandleAsync);

    private static Task<IResult> HandleAsync(string slug, string orgId, HttpRequest request, AuthStore store, InstanceResolver resolver, CancellationToken ct)
        => JwtTemplateSupport.WithScopeAsync(resolver, request, slug, async context =>
            {
                var organization = await FindOrganizationAsync(store, context.Instance.Id, orgId, ct);
                if (organization is null)
                    return ApiResults.NotFound("organization_not_found", "no such organization");

                var body = await request.ReadFromJsonAsync<CreateDomainRequest>(ct);
                if (body is null || !IsValidDomain(body.Domain))
                    return ApiResults.BadRequest("invalid_domain", "domain must be 1-253 characters of valid hostname labels");

                var domain = body.Domain!.Trim().ToLowerInvariant();
                var record = new OrgDomain
                {
                    Id = "dom_" + SecureTokens.RandomToken(20),
                    InstanceId = context.Instance.Id,
                    OrgId = organization.Id,
                    Domain = domain,
                    DomainKey = $"{context.Instance.Id}:{domain}",
                    CreatedAt = JwtTemplateSupport.Now(),
                };
                // Check-then-act ahead of the domainKey unique index (see organization creation).
                if ((await store.ListOrgDomainsAsync(context.Instance.Id, ct)).Any(d => d.DomainKey == record.DomainKey))
                    return ApiResults.Conflict("domain_exists", "this domain is already enrolled in an organization");
                try
                {
                    await store.CreateOrgDomainAsync(record, ct);
                }
                catch (TerraScaleApiException exception) when (exception.StatusCode is HttpStatusCode.Conflict or HttpStatusCode.PreconditionFailed)
                {
                    return ApiResults.Conflict("domain_exists", "this domain is already enrolled in an organization");
                }
                return Results.Ok(Render(record));
            }, ct);

    /// <summary>Hostname shape: dot-separated labels of ascii letters, digits, and inner hyphens; total 1-253 characters.</summary>
    private static bool IsValidDomain(string? domain)
    {
        var value = domain?.Trim().ToLowerInvariant();
        return value is { Length: >= 1 and <= 253 }
            && value.Split('.').All(label => label.Length is >= 1 and <= 63
                && !label.StartsWith('-') && !label.EndsWith('-')
                && label.All(c => char.IsAsciiLetterOrDigit(c) || c == '-'));
    }
}
