using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;

namespace TerraScale.DotNet;

/// <summary>
/// Entry point for TerraScale database and repository API operations.
/// </summary>
public sealed class TerraScaleClient
{
    private static readonly JsonSerializerOptions DefaultJsonOptions = new(JsonSerializerDefaults.Web);

    private readonly HttpClient httpClient;
    private readonly TerraScaleClientOptions options;
    private readonly JsonSerializerOptions jsonOptions;

    /// <summary>
    /// Creates a TerraScale client over a caller-owned <see cref="HttpClient"/>.
    /// </summary>
    public TerraScaleClient(HttpClient httpClient, TerraScaleClientOptions options)
    {
        ArgumentNullException.ThrowIfNull(httpClient);
        ArgumentNullException.ThrowIfNull(options);
        options.Validate();

        this.httpClient = httpClient;
        this.options = options;
        jsonOptions = DefaultJsonOptions;

        this.httpClient.BaseAddress ??= options.BaseAddress;
        Databases = new TerraScaleDatabasesClient(this);
        Documents = new TerraScaleDocumentsClient(this);
        Tql = new TerraScaleTqlClient(this);
    }

    /// <summary>
    /// Database management operations.
    /// </summary>
    public TerraScaleDatabasesClient Databases { get; }

    /// <summary>
    /// Repository document operations.
    /// </summary>
    public TerraScaleDocumentsClient Documents { get; }

    /// <summary>
    /// TerraScale Query Language operations.
    /// </summary>
    public TerraScaleTqlClient Tql { get; }

    internal async Task<TResponse> SendAsync<TResponse>(
        HttpMethod method,
        string requestUri,
        object? body,
        CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(method, requestUri);
        ApplyRequestVersion(request);
        ApplyAuthentication(request);

        if (body is not null)
        {
            request.Content = JsonContent.Create(body, options: jsonOptions);
        }

        using var response = await httpClient.SendAsync(
            request,
            HttpCompletionOption.ResponseHeadersRead,
            cancellationToken).ConfigureAwait(false);

        if (!response.IsSuccessStatusCode)
        {
            throw await TerraScaleApiException.CreateAsync(response, cancellationToken).ConfigureAwait(false);
        }

        var payload = await response.Content.ReadFromJsonAsync<TResponse>(
            jsonOptions,
            cancellationToken).ConfigureAwait(false);

        return payload ?? throw new TerraScaleApiException(
            response.StatusCode,
            "TerraScale returned an empty response body.",
            errorCode: null,
            requestId: ReadRequestId(response),
            responseBody: null,
            retryAfter: response.Headers.RetryAfter?.Delta);
    }

    internal async Task SendAsync(
        HttpMethod method,
        string requestUri,
        CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(method, requestUri);
        ApplyRequestVersion(request);
        ApplyAuthentication(request);

        using var response = await httpClient.SendAsync(
            request,
            HttpCompletionOption.ResponseHeadersRead,
            cancellationToken).ConfigureAwait(false);

        if (!response.IsSuccessStatusCode)
        {
            throw await TerraScaleApiException.CreateAsync(response, cancellationToken).ConfigureAwait(false);
        }
    }

    private void ApplyAuthentication(HttpRequestMessage request)
    {
        if (!string.IsNullOrWhiteSpace(options.ApiKey))
        {
            request.Headers.TryAddWithoutValidation("X-TerraScale-Auth-Key", options.ApiKey);
            request.Headers.TryAddWithoutValidation("X-TerraScale-Project-Id", options.ProjectId);
            return;
        }

        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", options.BearerToken);
    }

    private void ApplyRequestVersion(HttpRequestMessage request)
    {
        request.Version = httpClient.DefaultRequestVersion;
        request.VersionPolicy = httpClient.DefaultVersionPolicy;
    }

    private static string? ReadRequestId(HttpResponseMessage response) =>
        response.Headers.TryGetValues("X-Request-Id", out var requestIds)
            ? requestIds.FirstOrDefault()
            : null;
}
