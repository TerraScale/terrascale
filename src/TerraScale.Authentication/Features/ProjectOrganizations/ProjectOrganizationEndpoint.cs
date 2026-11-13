using System.Text.Json;
using System.Text.Json.Serialization.Metadata;
using TerraScale.Authentication.Features.Platform;
using TerraScale.Authentication.Features.ProjectTenants;

namespace TerraScale.Authentication.Features.ProjectOrganizations;

public abstract class ProjectOrganizationEndpoint(TenantDirectory tenants, PlatformAuthentication authentication)
    : ProjectTenantEndpoint(tenants, authentication)
{
    protected const string OrganizationRoot = Root + "/organizations";
    protected const string OrganizationRoute = OrganizationRoot + "/{org}";
    protected string Org => this.HttpContext.GetRouteValue("org") as string ?? throw PlatformDirectory.NotFound();
    protected async Task<T> OrganizationBodyAsync<T>(JsonTypeInfo<T> type, CancellationToken cancellationToken)
        where T : class
    {
        if (!this.HttpContext.Request.HasJsonContentType())
        {
            throw Invalid("Send an application/json body.");
        }

        using var buffer = new MemoryStream();
        var chunk = new byte[4096];
        int read;
        while ((read = await this.HttpContext.Request.Body.ReadAsync(chunk, cancellationToken)) > 0)
        {
            if (buffer.Length + read > 24 * 1024)
            {
                throw Invalid("The body must be at most 24 KiB.");
            }

            buffer.Write(chunk, 0, read);
        }

        try
        {
            return JsonSerializer.Deserialize(buffer.GetBuffer().AsSpan(0, (int)buffer.Length), type) ?? throw Invalid("Send a JSON object.");
        }
        catch (JsonException)
        {
            throw Invalid("Send a valid JSON object containing only this operation's members.");
        }
    }
}

