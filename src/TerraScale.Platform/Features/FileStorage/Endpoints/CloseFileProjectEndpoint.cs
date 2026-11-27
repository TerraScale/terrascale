using TerraScale.Platform.Features.Functions;

namespace TerraScale.Platform.Features.FileStorage.Endpoints;

public sealed record CloseFileProjectRequest(string Project, string? OrganizationId);

public readonly union CloseFileProjectReply(ProjectClosed, PlatformInvalidRequest, PlatformForbidden, PlatformConflict, PlatformUnavailable);

public sealed class CloseFileProjectEndpoint(PlatformOptions options, ProjectFileErasure erasure, FunctionService functions) : Endpoint<CloseFileProjectRequest, CloseFileProjectReply>
{
    public override void Configure()
    {
        Post("/internal/projects/{project}/close");
        Name("closeFileProject");
        Summary("Closes a project's file storage and functions and erases their data");
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

        var outcome = await erasure.MarkClosedAsync(project, organization, cancellationToken);
        return outcome switch
        {
            ProjectMarkedClosed marked => await ForgetAndEraseAsync(project, organization, marked.Regions, cancellationToken),
            FileProjectOrganizationMismatch => new PlatformConflict("the project belongs to another organization in file storage", "binding_conflict"),
            PlatformUnavailable unavailable => unavailable,
        };
    }

    private async ValueTask<CloseFileProjectReply> ForgetAndEraseAsync(
        Ulid project,
        Ulid organization,
        IReadOnlyList<string> regions,
        CancellationToken cancellationToken)
    {
        var forgotten = await functions.ForgetProjectAsync(project, organization, cancellationToken);
        return forgotten switch
        {
            ProjectFunctionsForgotten => await EraseFilesAsync(project, organization, regions, cancellationToken),
            PlatformUnavailable unavailable => unavailable,
        };
    }

    private async ValueTask<CloseFileProjectReply> EraseFilesAsync(
        Ulid project,
        Ulid organization,
        IReadOnlyList<string> regions,
        CancellationToken cancellationToken)
    {
        var erased = await erasure.EraseFilesAsync(project, organization, regions, cancellationToken);
        return erased switch
        {
            ProjectClosed closed => closed,
            PlatformUnavailable unavailable => unavailable,
        };
    }

    private static bool CanonicalId(string? value, out Ulid id) => Ulid.TryParse(value, out id) && id.ToString() == value;
}
