using TerraScale.Dashboard.Common.UI;
using TerraScale.Dashboard.Platform.TsAuth;

namespace TerraScale.Dashboard.Features.Organizations.Projects
{
    public sealed record ProjectInvitationPage(ShellModel Shell, IReadOnlyList<TsAuthPlatformInvitation> Invitations,
        string? Next = null, string Error = "", bool HasPrevious = false, string After = "", string Notice = "")
    {
        public string PostUrl => WorkspaceUrl.For(Shell.Context, "/api/v1/dashboard/project/invitations/revoke", replace: true);
        public string Url => WorkspaceUrl.For(Shell.Context, "/project/invitations", replace: true);
    }

    public static class ProjectInvitationEndpoints
    {
        internal static RazorSlices.RazorSlice<ProjectInvitationPage> Page(HttpContext http, AccountContext context, IReadOnlyList<TsAuthPlatformInvitation> invitations,
            string? next = null, string error = "", int status = 200, bool previous = false, string after = "", string notice = "")
        {
            http.Response.Headers.CacheControl = "no-store";
            var page = ProjectInvitationView.Create(new(ShellModel.For(http, context, "Project invitations"), invitations, next, error, previous, after, notice));
            page.StatusCode = status;
            return page;
        }
    }
}
