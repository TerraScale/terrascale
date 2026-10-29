using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.Json.Serialization.Metadata;
using TerraBase.Configuration;
using TerraBase.Errors;
using TerraBase.Features.Memory;
using TerraBase.Hosting;

namespace TerraBase.Features.UserAuth;

internal sealed record TsAuthProjectAuthority(
    string? Id, [property: JsonPropertyName("organization_id")] string? OrganizationId, string? Role);

internal sealed record TsAuthOrganizationAuthority(string? Id, string? Role);
internal sealed record TsAuthAuthoritySubject(string? Sub);

internal sealed class TsAuthProjectAuthorityVerifier(TerraBaseOptions options, IHttpClientFactory http, IMemoryBudget budget) : IProjectAuthorityVerifier
{
    private const int MaxInFlight = 16;
    private const int MaxResponseBytes = 64 * 1024;
    private int _inFlight;

    public ValueTask<ProjectAuthorityResult> IsRecoveryAdministratorAsync(
        string token, string userId, string organizationId, string projectId, CancellationToken cancellationToken) =>
        CheckAsync(token, async (client, issuer) =>
        {
            var subject = await FetchAsync(client, new Uri(issuer.AbsoluteUri.TrimEnd('/') + "/oauth/userinfo"), token,
                TerraBaseJsonContext.Default.TsAuthAuthoritySubject, cancellationToken).ConfigureAwait(false);
            if (subject?.Sub != userId)
            {
                return false;
            }

            var organization = await FetchAsync(client, OrganizationAddress(issuer, organizationId), token,
                TerraBaseJsonContext.Default.TsAuthOrganizationAuthority, cancellationToken).ConfigureAwait(false);
            if (organization is not { Role: "owner" or "admin" } || organization.Id != organizationId)
            {
                return false;
            }

            var project = await FetchAsync(client, ProjectAddress(issuer, organizationId, projectId), token,
                TerraBaseJsonContext.Default.TsAuthProjectAuthority, cancellationToken).ConfigureAwait(false);
            return project is { Role: "admin" } && project.Id == projectId && project.OrganizationId == organizationId;
        }, cancellationToken);

    private async ValueTask<ProjectAuthorityResult> CheckAsync(
        string token, Func<HttpClient, Uri, Task<bool>> check, CancellationToken cancellationToken)
    {
        if (options.AuthIssuer is not { } configured || token.Length is 0 or > 8192)
        {
            return false;
        }

        try
        {
            if (Interlocked.Increment(ref _inFlight) > MaxInFlight)
            {
                return Unavailable();
            }

            // Only the configured ts-auth origin receives the credential. No redirects; bounded responses and admission.
            using var reservation = budget.TryReserve(MemoryLane.Foreground, 4L * MaxResponseBytes);
            if (reservation is null)
            {
                return Unavailable();
            }

            using var client = http.CreateClient(TsAuthKeys.HttpClientName);
            client.MaxResponseContentBufferSize = MaxResponseBytes;
            return await check(client, new Uri(configured)).ConfigureAwait(false);
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

    private static Uri OrganizationAddress(Uri issuer, string organization) => new(issuer.GetLeftPart(UriPartial.Authority)
        + "/v1" + issuer.AbsolutePath.TrimEnd('/') + "/organizations/" + Uri.EscapeDataString(organization));

    private static Uri ProjectAddress(Uri issuer, string organization, string project) =>
        new(OrganizationAddress(issuer, organization).AbsoluteUri + "/projects/" + Uri.EscapeDataString(project));

    private static async Task<T?> FetchAsync<T>(HttpClient client, Uri address, string token, JsonTypeInfo<T> type, CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, address);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        using var response = await client.SendAsync(request, cancellationToken).ConfigureAwait(false);
        if ((int)response.StatusCode >= 500 || response.StatusCode == HttpStatusCode.TooManyRequests)
        {
            throw new HttpRequestException("ts-auth authority unavailable");
        }

        return response.StatusCode == HttpStatusCode.OK
            ? await response.Content.ReadFromJsonAsync(type, cancellationToken).ConfigureAwait(false) : default;
    }

    private static Unavailable Unavailable() => new("ts-auth project verification is unavailable");
}
