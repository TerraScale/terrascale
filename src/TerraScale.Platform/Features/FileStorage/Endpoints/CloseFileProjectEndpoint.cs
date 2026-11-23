namespace TerraScale.Platform.Features.FileStorage.Endpoints;

public sealed record CloseFileProjectRequest(string Project, string? OrganizationId);

public readonly union CloseFileProjectReply(ProjectClosed, PlatformInvalidRequest, PlatformForbidden, PlatformConflict, PlatformUnavailable);

public sealed class CloseFileProjectEndpoint(PlatformOptions options, ProjectFileErasure erasure) : Endpoint<CloseFileProjectRequest, CloseFileProjectReply>
{
    public override void Configure()
    {
        Post("/internal/projects/{project}/close");
        Name("closeFileProject");
        Summary("Closes a project's file storage and erases its objects");
        Tags("Internal");
        AllowAnonymous();
    }

    public override async ValueTask<CloseFileProjectReply> HandleAsync(CloseFileProjectRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (CallerCredential.From(HttpContext.Request, options.PeerSecret) is not { Peer: true })
        {
            return new PlatformForbidden("project file closure needs a peer credential", "forbidden_caller");
        }

        if (!CanonicalId(request.Project, out var project) || !CanonicalId(request.OrganizationId, out var organization))
        {
            return new PlatformInvalidRequest("project and organizationId must be canonical uppercase ULIDs", "invalid_project_binding");
        }

        var outcome = await erasure.CloseAsync(project, organization, cancellationToken);
        return outcome switch
        {
            ProjectClosed closed => closed,
            FileProjectOrganizationMismatch => new PlatformConflict("the project belongs to another organization in file storage", "binding_conflict"),
            PlatformUnavailable unavailable => unavailable,
        };
    }

    private static bool CanonicalId(string? value, out Ulid id) => Ulid.TryParse(value, out id) && id.ToString() == value;
}
