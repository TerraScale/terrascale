namespace TerraScale.Dashboard.Features.Organizations.Projects
{
    public sealed record ProjectDialogModel(OrganizationPage Page, ProjectRecord? Project = null)
    {
        public string Id => Project is null ? "create-project" : "rename-project-" + Project.Id;
        public string NameId => Project is null ? "project-name" : "rename-" + Project.Id;
        public string RegionsId => "project-regions" + (Project is null ? "" : "-" + Project.Id);
        public string PrimaryId => "project-region" + (Project is null ? "" : "-" + Project.Id);
        public string BackupId => "project-backup" + (Project is null ? "" : "-" + Project.Id);
        public string Title => Project is null ? "Create project" : "Edit " + Project.Name;
        public string Action => Project is null ? "/api/v1/dashboard/organization/projects" : "/api/v1/dashboard/organization/projects/" + Project.Id + "/rename";
        public bool Open => Project is null && Page.OpenCreateProject;
        public IReadOnlyList<string> Selected => Project is null ? Page.ProjectDraftRegions ?? Page.Regions : Project.Regions;
        public string Primary => Project?.Region ?? (Page.ProjectDraftPrimaryRegion.Length > 0 ? Page.ProjectDraftPrimaryRegion : Page.Regions.FirstOrDefault() ?? "");
        public string Backup => Project?.BackupRegion is { Length: > 0 } backup ? backup :
            Page.ProjectDraftBackupRegion.Length > 0 ? Page.ProjectDraftBackupRegion : Page.Regions.FirstOrDefault() ?? "";
    }
}
