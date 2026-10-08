using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;
using System.Text.Json.Serialization;
using TerraBase.Configuration;
using TerraBase.Errors;
using TerraBase.Features.Memory;
using TerraBase.Hosting;

namespace TerraBase.Features.UserAuth;

internal sealed record TsAuthProjectAuthority(
    string? Id, [property: JsonPropertyName("organization_id")] string? OrganizationId, string? Role);

internal sealed class TsAuthProjectAuthorityVerifier(TerraBaseOptions options, IHttpClientFactory http, IMemoryBudget budget) : IProjectAuthorityVerifier
{
    private const int MaxInFlight = 16;
    private int _inFlight;

    public async ValueTask<ProjectAuthorityResult> IsProjectAdministratorAsync(string token, string organizationId, string projectId, CancellationToken cancellationToken)
    {
        if (options.AuthIssuer is not { } configured)
        {
            return false;
        }

        // Only the configured ts-auth origin receives the credential; its client follows no redirects and bounds reads.
        var issuer = new Uri(configured);
        var address = new Uri(issuer.GetLeftPart(UriPartial.Authority) + "/v1" + issuer.AbsolutePath.TrimEnd('/')
            + "/organizations/" + Uri.EscapeDataString(organizationId) + "/projects/" + Uri.EscapeDataString(projectId));
        using var request = new HttpRequestMessage(HttpMethod.Get, address);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        try
        {
            if (Interlocked.Increment(ref _inFlight) > MaxInFlight)
            {
                return Unavailable();
            }

            // Reserve the bounded response plus JSON decoding before the HTTP client buffers either.
            using var reservation = budget.TryReserve(MemoryLane.Foreground, 4L * TsAuthKeys.MaxDocumentBytes);
            if (reservation is null)
            {
                return Unavailable();
            }

            using var client = http.CreateClient(TsAuthKeys.HttpClientName);
            using var response = await client.SendAsync(request, cancellationToken).ConfigureAwait(false);
            if ((int)response.StatusCode >= 500 || response.StatusCode == HttpStatusCode.TooManyRequests)
            {
                return Unavailable();
            }

            if (response.StatusCode != HttpStatusCode.OK)
            {
                return false;
            }

            var project = await response.Content.ReadFromJsonAsync(TerraBaseJsonContext.Default.TsAuthProjectAuthority, cancellationToken).ConfigureAwait(false);
            return project is { Role: "admin" } && project.Id == projectId && project.OrganizationId == organizationId;
        }
        catch (JsonException)
        {
            return false;
        }
        catch (HttpRequestException)
        {
            return Unavailable();
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return Unavailable();
        }
        finally
        {
            Interlocked.Decrement(ref _inFlight);
        }
    }

    private static Unavailable Unavailable() => new("ts-auth project verification is unavailable");
}
