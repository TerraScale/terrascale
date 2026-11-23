using System.Net;
using System.Text;
using System.Text.Json;
using TerraBase.Configuration;
using TerraBase.Features.Callers;
using TerraBase.Hosting;
using TerraScale.Endpoints;

namespace TerraBase.Features.Management;

internal sealed class CloseProjectEndpoint(
    ManagementService management,
    TerraBaseOptions options,
    InternalCredential credential,
    IHttpClientFactory httpClientFactory)
    : Endpoint<ProjectClosureInput, Result<ProjectClosureBody, Validation, Forbidden, Conflict, Unavailable>>
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

    public override async ValueTask<Result<ProjectClosureBody, Validation, Forbidden, Conflict, Unavailable>> HandleAsync(
        ProjectClosureInput request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (!PlanFlow.IsOperator(HttpContext.Caller, "closure"))
        {
            return new Forbidden("project closure needs an operator credential", "forbidden_caller");
        }

        if (request.Version is not int version || version is not (1 or 2))
        {
            return new Validation("version is 1 or 2", "unsupported_version");
        }

        if (string.IsNullOrWhiteSpace(request.ClosureId) || request.ClosureId.Length > 128)
        {
            return new Validation("closureId is 1 to 128 characters", "invalid_request");
        }

        if (ManagementService.Id(request.Project) is not { } projectId || ManagementService.Id(request.AuthorityOrganizationId) is not { } organizationId)
        {
            return new Validation("project and authorityOrganizationId must be canonical uppercase ULIDs", "invalid_request");
        }

        var outcome = await management.CloseProjectAsync(projectId, organizationId, erase: version == 2, cancellationToken);
        return outcome switch
        {
            Done => await ErasePlatformFilesAsync(projectId, organizationId, request.ClosureId, cancellationToken),
            Conflict conflict => conflict,
            Unavailable unavailable => unavailable,
        };
    }

    private async ValueTask<Result<ProjectClosureBody, Validation, Forbidden, Conflict, Unavailable>> ErasePlatformFilesAsync(
        Ulid projectId, Ulid organizationId, string closureId, CancellationToken cancellationToken)
    {
        if (options.PlatformUrl is not { } platformUrl)
        {
            return new ProjectClosureBody("accepted", closureId, 0, 2);
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

            return new Unavailable("platform project file erasure is unavailable; retry", "platform_unavailable");
        }
        catch (HttpRequestException)
        {
            return new Unavailable("platform project file erasure is unavailable; retry", "platform_unavailable");
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return new Unavailable("platform project file erasure timed out; retry", "platform_unavailable");
        }
    }
}
