namespace TerraScale.Dashboard.Common.Accounts
{
    public static class ProjectCollaborator
    {
        public static bool Allows(OrganizationRecord organization, ProjectRecord? project)
        {
            return !organization.MfaRequired && organization.Status == "active" && !organization.Archived &&
                project?.Role is "viewer" or "member" or "admin";
        }
    }
}
