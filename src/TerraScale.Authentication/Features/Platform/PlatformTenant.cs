using System.Collections.Frozen;
using System.Globalization;
using System.Linq.Expressions;
using TerraScale.Authentication.Contracts;
using TerraScale.Authentication.Features.Oidc;

namespace TerraScale.Authentication.Features.Platform;

public static class PlatformTenant
{
    public const string Id = OidcIssuer.InstanceId;

    public const string Name = "TerraScale accounts";

    public static readonly FrozenSet<string> ReservedProjectIds = Enumerable.Range(1, 7)
        .Select(number => number.ToString("D26", CultureInfo.InvariantCulture)).ToFrozenSet(StringComparer.Ordinal);

    public static readonly Expression<Func<OrganizationRecord, bool>> CustomerOrganization = organization => organization.Id != Id;

    public static readonly Expression<Func<ProjectRecord, bool>> CustomerProject = project =>
        project.OrganizationId != Id && project.OrganizationId != PlatformBootstrap.OrganizationId && !ReservedProjectIds.Contains(project.Id);

    private static readonly Func<ProjectRecord, bool> IsCustomer = CustomerProject.Compile();

    public static bool IsCustomerProject(ProjectRecord project) => IsCustomer(project);
}
