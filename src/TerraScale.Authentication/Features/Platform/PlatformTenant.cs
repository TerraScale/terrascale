using System.Linq.Expressions;
using TerraScale.Authentication.Contracts;
using TerraScale.Authentication.Features.Oidc;

namespace TerraScale.Authentication.Features.Platform;

public static class PlatformTenant
{
    public const string Id = OidcIssuer.InstanceId;

    public const string Name = "TerraScale accounts";

    public static readonly Expression<Func<OrganizationRecord, bool>> CustomerOrganization = organization => organization.Id != Id;

    public static readonly Expression<Func<ProjectRecord, bool>> CustomerProject = project => project.OrganizationId != Id;
}
