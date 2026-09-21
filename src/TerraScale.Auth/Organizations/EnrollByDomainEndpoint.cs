namespace TerraScale.Auth.Organizations;

using System.Net;
using TerraScale.Auth.Api;
using TerraScale.Auth.Crypto;
using TerraScale.Auth.Store;
using TerraScale.DotNet;
using TerraScale.Auth.Templates;
using TerraScale.Endpoints;
using static TerraScale.Auth.Organizations.OrganizationDomainSupport;

internal sealed class EnrollByDomainEndpoint : IEndpoint
{
    public RouteHandlerBuilder Map(IEndpointRouteBuilder routes) =>
        routes.MapPost("/v1/{slug}/domains/{domain}/enroll", HandleAsync);

    private static Task<IResult> HandleAsync(string slug, string domain, HttpRequest request, AuthStore store, InstanceResolver resolver, CancellationToken ct)
        => JwtTemplateSupport.WithScopeAsync(resolver, request, slug, async context =>
            {
                var normalized = domain.Trim().ToLowerInvariant();
                var record = (await store.ListOrgDomainsAsync(context.Instance.Id, ct)).FirstOrDefault(d => d.Domain == normalized);
                if (record is null)
                    return ApiResults.NotFound("domain_not_found", "no such enrolled domain");

                var body = await request.ReadFromJsonAsync<EnrollRequest>(ct);
                if (body is null || string.IsNullOrWhiteSpace(body.UserId))
                    return ApiResults.BadRequest("invalid_request", "user_id is required");
                var user = await store.GetUserAsync(body.UserId.Trim(), ct);
                if (user is null || user.InstanceId != context.Instance.Id)
                    return ApiResults.NotFound("user_not_found", "no such user in this instance");

                if (user.Email is null || !user.Email.EndsWith("@" + normalized, StringComparison.OrdinalIgnoreCase))
                    return ApiResults.BadRequest("email_domain_mismatch", $"the user's email is not @{normalized}");

                var alreadyMember = (await store.ListMembershipsByOrgAsync(record.OrgId, ct)).Any(m => m.UserId == user.Id);
                if (!alreadyMember)
                {
                    try
                    {
                        await store.CreateMembershipAsync(new OrgMembership
                        {
                            Id = "mem_" + SecureTokens.RandomToken(20),
                            InstanceId = context.Instance.Id,
                            OrgId = record.OrgId,
                            UserId = user.Id,
                            MemberKey = $"{record.OrgId}:{user.Id}",
                            Role = "member",
                            CreatedAt = JwtTemplateSupport.Now(),
                        }, ct);
                    }
                    catch (TerraScaleApiException exception) when (exception.StatusCode is HttpStatusCode.Conflict or HttpStatusCode.PreconditionFailed)
                    {
                        // Lost a race with a concurrent enroll — the membership exists either way.
                    }
                }

                await JwtTemplateSupport.AuditAsync(store, context.Instance.Id, "organization.enrolled", user.Id, new Dictionary<string, object>
                {
                    ["organization_id"] = record.OrgId,
                    ["user_id"] = user.Id,
                    ["domain"] = normalized,
                }, ct);
                return Results.Ok(new { org_id = record.OrgId });
            }, ct);
}
