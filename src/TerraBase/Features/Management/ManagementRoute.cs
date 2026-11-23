namespace TerraBase.Features.Management;

internal static class ManagementRoute
{
    public const string Organizations = "/v1/management/organizations";

    public const string Organization = Organizations + "/{organization}";

    public const string Projects = Organization + "/projects";

    public const string Project = Projects + "/{project}";

    public const string ProjectApi = "/v1/projects/{project}";

    public const string Databases = ProjectApi + "/databases";

    public const string Database = Databases + "/{database}";

    public const string Tag = "Management";
}
