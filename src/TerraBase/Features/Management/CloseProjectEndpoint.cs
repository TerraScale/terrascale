using System.Net;
using System.Text;
using System.Text.Json;
using TerraBase.Configuration;
using TerraBase.Features.Callers;
using TerraBase.Hosting;
using TerraScale.Endpoints;

namespace TerraBase.Features.Management;

internal readonly union CloseProjectResponse(ProjectClosureBody, InvalidInput, CallerForbidden, StateConflict, BackendUnavailable);

internal sealed class CloseProjectEndpoint(
    ManagementService management,
    TerraBaseOptions options,
    InternalCredential credential,
    IHttpClientFactory httpClientFactory)
    : Endpoint<ProjectClosureInput, CloseProjectResponse>
{
    public const string HttpClientName = "platform-closure";

    public override void Configure()
    {
        Put("/admin/projects/{project}/closure");
        Name("adminCloseProject");
        Summary("Closes an empty project for ts-auth");
        Description("{\"version\": 1 | 2, \"closureId\": \"<id>\", \"authorityOrganizationId\": \"<ULID>\"}: deletes the project and its API keys once it holds no database. "
            + "Repeating the request after the project is gone answers accepted again.");
        Tags("Administration");
        AllowAnonymous();
        Metadata(ListenerMetadata.Internal);
    }

    public override async ValueTask<CloseProjectResponse> HandleAsync(
        ProjectClosureInput request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (!PlanFlow.IsOperator(HttpContext.Caller, "closure"))
        {
            return new CallerForbidden("project closure needs an operator credential", "forbidden_caller");
        }

        if (request.Version is not int version || version is not (1 or 2))
        {
            return new InvalidInput("version is 1 or 2", "unsupported_version");
        }

        if (string.IsNullOrWhiteSpace(request.ClosureId) || request.ClosureId.Length > 128)
        {
            return new InvalidInput("closureId is 1 to 128 characters", "invalid_request");
        }

        if (ManagementService.Id(request.Project) is not { } projectId || ManagementService.Id(request.AuthorityOrganizationId) is not { } organizationId)
        {
            return new InvalidInput("project and authorityOrganizationId must be canonical uppercase ULIDs", "invalid_request");
        }

        var outcome = await management.CloseProjectAsync(projectId, organizationId, erase: version == 2, cancellationToken);
        return outcome switch
        {
            Completed => await ErasePlatformFilesAsync(projectId, organizationId, request.ClosureId, cancellationToken),
            StateConflict conflict => conflict,
            BackendUnavailable unavailable => unavailable,
        };
    }

    private async ValueTask<CloseProjectResponse> ErasePlatformFilesAsync(
        Ulid projectId, Ulid organizationId, string closureId, CancellationToken cancellationToken)
    {
        if (options.PlatformUrl is not { } platformUrl)
        {
            return new BackendUnavailable("platform project file erasure is not configured on this node; retry", "platform_unavailable");
        }

        var endpoint = new Uri(platformUrl, $"/internal/projects/{projectId}/close");
        var payload = JsonSerializer.Serialize(new PlatformCloseProjectBody(organizationId.ToString()), ManagementJsonContext.Default.PlatformCloseProjectBody);
        using var message = new HttpRequestMessage(HttpMethod.Post, endpoint)
        {
            Content = new StringContent(payload, Encoding.UTF8, "application/json"),
        };
        credential.Sign(message.Headers);
        try
        {
            using var response = await httpClientFactory.CreateClient(HttpClientName).SendAsync(message, cancellationToken);
            if (response.StatusCode == HttpStatusCode.NoContent)
            {
                return new ProjectClosureBody("accepted", closureId, 0, 2);
            }

            return new BackendUnavailable("platform project file erasure is unavailable; retry", "platform_unavailable");
        }
        catch (HttpRequestException)
        {
            return new BackendUnavailable("platform project file erasure is unavailable; retry", "platform_unavailable");
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return new BackendUnavailable("platform project file erasure timed out; retry", "platform_unavailable");
        }
    }
}
