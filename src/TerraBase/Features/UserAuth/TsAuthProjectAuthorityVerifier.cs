using System.Net.Http.Headers;
using System.Net;
using System.Text.Json.Serialization.Metadata;
using System.Text.Json.Serialization;
using System.Text.Json;
using TerraBase.Configuration;
using TerraBase.Errors;
using TerraBase.Features.Memory;
using TerraBase.Features.UserAuth;
using TerraBase.Hosting;

namespace TerraBase.Features.UserAuth;

internal sealed record TsAuthProjectAuthority(
    string? Id, [property: JsonPropertyName("organization_id")] string? OrganizationId, string? Role);

internal sealed record TsAuthOrganizationAuthority(string? Id, string? Role);
internal sealed record TsAuthAuthoritySubject(string? Sub);

internal readonly union Fetched<T>(T, NoBody, Unavailable);

internal sealed record NoBody;

internal sealed class TsAuthProjectAuthorityVerifier(TerraBaseOptions options, IHttpClientFactory http, IMemoryBudget budget) : IProjectAuthorityVerifier
{
    private const int MaxInFlight = 16;
    private const int MaxResponseBytes = 64 * 1024;
    private int _inFlight;

    public ValueTask<ProjectAuthorityResult> IsRecoveryAdministratorAsync(
        string token, string userId, string organizationId, string projectId, CancellationToken cancellationToken) =>
        CheckAsync(token, async (client, issuer) =>
        {
            switch (await FetchAsync(client, new Uri(issuer.AbsoluteUri.TrimEnd('/') + "/oauth/userinfo"), token,
                UserAuthJsonContext.Default.TsAuthAuthoritySubject, cancellationToken).ConfigureAwait(false))
            {
                case Unavailable error:
                    return error;
                case NoBody:
                    return false;
                case TsAuthAuthoritySubject { Sub: var subject }:
                    if (subject != userId)
                    {
                        return false;
                    }

                    break;
            }

            switch (await FetchAsync(client, OrganizationAddress(issuer, organizationId), token,
                UserAuthJsonContext.Default.TsAuthOrganizationAuthority, cancellationToken).ConfigureAwait(false))
            {
                case Unavailable error:
                    return error;
                case NoBody:
                    return false;
                case TsAuthOrganizationAuthority authority:
                    if (authority.Role is not ("owner" or "admin") || authority.Id != organizationId)
                    {
                        return false;
                    }

                    break;
            }

            return await FetchAsync(client, ProjectAddress(issuer, organizationId, projectId), token,
                UserAuthJsonContext.Default.TsAuthProjectAuthority, cancellationToken).ConfigureAwait(false) switch
            {
                Unavailable error => error,
                NoBody => false,
                TsAuthProjectAuthority authority => authority.Role == "admin" && authority.Id == projectId && authority.OrganizationId == organizationId,
            };
        }, cancellationToken);

    private async ValueTask<ProjectAuthorityResult> CheckAsync(
        string token, Func<HttpClient, Uri, Task<ProjectAuthorityResult>> check, CancellationToken cancellationToken)
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

            using var reservation = budget.TryReserve(4L * MaxResponseBytes);
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

    private static async Task<Fetched<T>> FetchAsync<T>(HttpClient client, Uri address, string token, JsonTypeInfo<T> type, CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, address);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        using var response = await client.SendAsync(request, cancellationToken).ConfigureAwait(false);
        if ((int)response.StatusCode >= 500 || response.StatusCode == HttpStatusCode.TooManyRequests)
        {
            return new Unavailable("ts-auth authority unavailable");
        }

        if (response.StatusCode != HttpStatusCode.OK)
        {
            return new NoBody();
        }

        try
        {
            return await response.Content.ReadFromJsonAsync(type, cancellationToken).ConfigureAwait(false) is { } value ? value : new NoBody();
        }
        catch (JsonException)
        {
            return new NoBody();
        }
    }

    private static Unavailable Unavailable() => new("ts-auth project verification is unavailable");
}
