namespace TerraScale.Dashboard.Features.Organizations.Projects
{
    public sealed record ProjectDialogModel(OrganizationPage Page, ProjectRecord? Project = null)
    {
        public string Id => Project is null ? "create-project" : "rename-project-" + Project.Id;
        public string NameId => Project is null ? "project-name" : "rename-" + Project.Id;
        public string RegionsId => "project-regions" + (Project is null ? "" : "-" + Project.Id);
        public string Title => Project is null ? "Create project" : "Edit " + Project.Name;
        public string Action => Project is null ? "/api/v1/dashboard/organization/projects" : "/api/v1/dashboard/organization/projects/" + Project.Id + "/rename";
        public bool Open => Project is null ? Page.OpenCreateProject : Page.ProjectDraftId == Project.Id && Page.Error.Length > 0;
        public IReadOnlyList<string> Selected => Project is null ? Page.ProjectDraftRegions ?? Page.Regions
            : Page.ProjectDraftId == Project.Id && Page.ProjectDraftRegions is { } drafted ? drafted : Project.Regions;

        public string Name => Project is null ? Page.ProjectDraftName : Page.ProjectDraftId == Project.Id ? Page.ProjectDraftName : Project.Name;
    }
}
