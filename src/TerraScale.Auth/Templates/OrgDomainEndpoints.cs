namespace TerraScale.Auth.Templates;

using System.Net;
using TerraScale.Auth.Api;
using TerraScale.Auth.Crypto;
using TerraScale.Auth.Store;
using TerraScale.DotNet;

/// <summary>
/// Organization enrollment domains (admin-declared) and the enrollment hook.
/// Lives under Templates/ because this feature slice owns only that directory
/// (plus Users/Passcode*); it is wired from JwtTemplateEndpoints.Map.
/// </summary>
public static class OrgDomainEndpoints
{
    /// <summary>Maps org-domain endpoints.</summary>
    public static void Map(WebApplication app)
    {
        app.MapPost("/v1/{slug}/organizations/{orgId}/domains", (string slug, string orgId, HttpRequest request, AuthStore store, InstanceResolver resolver, CancellationToken ct) =>
            JwtTemplateEndpoints.WithScopeAsync(resolver, request, slug, async context =>
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
                    CreatedAt = JwtTemplateEndpoints.Now(),
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
            }, ct));

        app.MapGet("/v1/{slug}/organizations/{orgId}/domains", (string slug, string orgId, HttpRequest request, AuthStore store, InstanceResolver resolver, CancellationToken ct) =>
            JwtTemplateEndpoints.WithScopeAsync(resolver, request, slug, async context =>
            {
                var organization = await FindOrganizationAsync(store, context.Instance.Id, orgId, ct);
                if (organization is null)
                    return ApiResults.NotFound("organization_not_found", "no such organization");

                var domains = (await store.ListOrgDomainsAsync(context.Instance.Id, ct)).Where(d => d.OrgId == organization.Id);
                return Results.Ok(domains.Select(Render).ToList());
            }, ct));

        app.MapDelete("/v1/{slug}/organizations/{orgId}/domains/{id}", (string slug, string orgId, string id, HttpRequest request, AuthStore store, InstanceResolver resolver, CancellationToken ct) =>
            JwtTemplateEndpoints.WithScopeAsync(resolver, request, slug, async context =>
            {
                var organization = await FindOrganizationAsync(store, context.Instance.Id, orgId, ct);
                if (organization is null)
                    return ApiResults.NotFound("organization_not_found", "no such organization");
                var record = await store.GetOrgDomainAsync(id, ct);
                if (record is null || record.InstanceId != context.Instance.Id || record.OrgId != organization.Id)
                    return ApiResults.NotFound("domain_not_found", "no such domain for this organization");

                await store.DeleteOrgDomainAsync(record.Id, ct);
                return Results.NoContent();
            }, ct));

        // Enrollment hook: verifies the user's email belongs to the domain and joins the
        // domain's org with the "member" role. The hosted sign-up page should call this
        // same logic right after creating a user, so domain-verified sign-ups land in
        // their organization automatically (that integration lives with the sign-up flow).
        app.MapPost("/v1/{slug}/domains/{domain}/enroll", (string slug, string domain, HttpRequest request, AuthStore store, InstanceResolver resolver, CancellationToken ct) =>
            JwtTemplateEndpoints.WithScopeAsync(resolver, request, slug, async context =>
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
                            CreatedAt = JwtTemplateEndpoints.Now(),
                        }, ct);
                    }
                    catch (TerraScaleApiException exception) when (exception.StatusCode is HttpStatusCode.Conflict or HttpStatusCode.PreconditionFailed)
                    {
                        // Lost a race with a concurrent enroll — the membership exists either way.
                    }
                }

                await JwtTemplateEndpoints.AuditAsync(store, context.Instance.Id, "organization.enrolled", user.Id, new Dictionary<string, object>
                {
                    ["organization_id"] = record.OrgId,
                    ["user_id"] = user.Id,
                    ["domain"] = normalized,
                }, ct);
                return Results.Ok(new { org_id = record.OrgId });
            }, ct));
    }

    // ---- Helpers ---------------------------------------------------------------------

    private static async Task<Organization?> FindOrganizationAsync(AuthStore store, string instanceId, string orgId, CancellationToken ct)
    {
        var organization = await store.GetOrganizationAsync(orgId, ct);
        return organization is not null && organization.InstanceId == instanceId ? organization : null;
    }

    /// <summary>Hostname shape: dot-separated labels of ascii letters, digits, and inner hyphens; total 1-253 characters.</summary>
    private static bool IsValidDomain(string? domain)
    {
        var value = domain?.Trim().ToLowerInvariant();
        return value is { Length: >= 1 and <= 253 }
            && value.Split('.').All(label => label.Length is >= 1 and <= 63
                && !label.StartsWith('-') && !label.EndsWith('-')
                && label.All(c => char.IsAsciiLetterOrDigit(c) || c == '-'));
    }

    private static object Render(OrgDomain domain) =>
        new { id = domain.Id, organization_id = domain.OrgId, domain = domain.Domain, created_at = domain.CreatedAt };
}

/// <summary>Create org domain request body.</summary>
internal sealed record CreateDomainRequest(string? Domain);

/// <summary>Domain enrollment request body.</summary>
internal sealed record EnrollRequest([property: System.Text.Json.Serialization.JsonPropertyName("user_id")] string? UserId);
