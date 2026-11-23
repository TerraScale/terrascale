using TerraScale.Results;
using System.Globalization;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization.Metadata;

namespace TerraScale.Dashboard.Platform.Clients;

public sealed record ServiceCaller(string? AccessToken)
{
    public static ServiceCaller Operator { get; } = new((string?)null);

    public static ServiceCaller User(string accessToken)
    {
        ArgumentException.ThrowIfNullOrEmpty(accessToken);
        return new(accessToken);
    }

    public override string ToString() => AccessToken is null ? "Operator caller" : "User caller [REDACTED]";
}

public sealed record RawAnswer(int Status, string? ContentType, string Body);

public sealed class ServiceTransport(HttpClient http)
{
    private const int MaxResponseBytes = 16 * 1024 * 1024;

    public Uri BaseAddress => http.BaseAddress ?? throw new InvalidOperationException("The service HttpClient has no base address.");

    public Task<Result<T, TerraBaseRefusal>> SendAsync<T>(ServiceCaller caller, HttpMethod method, string path, JsonTypeInfo<T> response,
        CancellationToken cancellationToken) =>
        SendAsync(caller, method, path, null, response, null, cancellationToken);

    public async Task<Result<T, TerraBaseRefusal>> SendAsync<T>(ServiceCaller caller, HttpMethod method, string path, HttpContent? content, JsonTypeInfo<T> response,
        Action<HttpRequestHeaders>? headers, CancellationToken cancellationToken)
    {
        using var request = Request(caller, method, path, content, headers);
        var answer = await ExchangeAsync(request, cancellationToken);
        return answer switch
        {
            RawAnswer { Status: >= 200 and < 300 } ok => Parse(ok, response),
            RawAnswer refused => Refusal(refused),
            TerraBaseRefusal refusal => refusal,
        };
    }

    public async Task<Result<Done, TerraBaseRefusal>> SendAsync(ServiceCaller caller, HttpMethod method, string path, HttpContent? content, CancellationToken cancellationToken)
    {
        using var request = Request(caller, method, path, content, null);
        var answer = await ExchangeAsync(request, cancellationToken);
        return answer switch
        {
            RawAnswer { Status: >= 200 and < 300 } => new Done(),
            RawAnswer refused => Refusal(refused),
            TerraBaseRefusal refusal => refusal,
        };
    }

    public async Task<Result<RawAnswer, TerraBaseRefusal>> SendRawAsync(ServiceCaller caller, HttpMethod method, string path, HttpContent? content,
        Action<HttpRequestHeaders>? headers, CancellationToken cancellationToken)
    {
        using var request = Request(caller, method, path, content, headers);
        return await ExchangeAsync(request, cancellationToken);
    }

    public static HttpContent Json<T>(T body, JsonTypeInfo<T> typeInfo) => JsonContent.Create(body, typeInfo);

    public static string Segment(string value) => Uri.EscapeDataString(value);

    public static string Query(params ReadOnlySpan<(string Name, string? Value)> pairs)
    {
        var query = new List<string>(pairs.Length);
        foreach (var (name, value) in pairs)
        {
            if (value is not null)
            {
                query.Add(string.Concat(name, "=", Uri.EscapeDataString(value)));
            }
        }

        return query.Count == 0 ? "" : string.Concat("?", string.Join('&', query));
    }

    public static TerraBaseRefusal Refusal(RawAnswer answer)
    {
        var refusal = new TerraBaseRefusal(answer.Status, Fallback(answer.Status), Message(answer.Status));
        if (answer.Body.Length == 0)
        {
            return refusal;
        }

        return Parse(answer, ClientJson.Default.TerraBaseErrorEnvelope) switch
        {
            TerraBaseErrorEnvelope { Error: { } error } => refusal with { Code = error.Code, Message = error.Message, SqlState = error.SqlState, Statement = error.Statement, Details = error.Details },
            TerraBaseErrorEnvelope or TerraBaseRefusal => refusal,
        };
    }

    public static Result<T, TerraBaseRefusal> Parse<T>(RawAnswer answer, JsonTypeInfo<T> typeInfo)
    {
        try
        {
            return JsonSerializer.Deserialize(answer.Body, typeInfo) is { } value
                ? value
                : TerraBaseRefusal.Invalid("The service answered an empty JSON document.");
        }
        catch (JsonException error)
        {
            return TerraBaseRefusal.Invalid(string.Concat("The service answered invalid JSON: ", error.Message));
        }
    }

    private HttpRequestMessage Request(ServiceCaller caller, HttpMethod method, string path, HttpContent? content, Action<HttpRequestHeaders>? headers)
    {
        var request = new HttpRequestMessage(method, new Uri(BaseAddress, path)) { Content = content };
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        if (caller.AccessToken is { } token)
        {
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        }

        headers?.Invoke(request.Headers);
        return request;
    }

    private async Task<Result<RawAnswer, TerraBaseRefusal>> ExchangeAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        try
        {
            using var response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
            if (response.Content.Headers.ContentLength > MaxResponseBytes)
            {
                return TerraBaseRefusal.Invalid("The service answer is too large.");
            }

            var body = await response.Content.ReadAsStringAsync(cancellationToken);
            var status = (int)response.StatusCode;
            var answer = new RawAnswer(status, response.Content.Headers.ContentType?.ToString(), body);
            return status is >= 200 and < 300 || RetryAfter(response) is not { } wait ? answer : Refusal(answer) with { RetryAfter = wait };
        }
        catch (HttpRequestException error)
        {
            return TerraBaseRefusal.Unreachable(string.Concat("The service is unreachable: ", error.Message));
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return new TerraBaseRefusal(StatusCodes.Status504GatewayTimeout, "deadline_exceeded", "The service call timed out.");
        }
    }

    private static TimeSpan? RetryAfter(HttpResponseMessage response) =>
        response.Headers.RetryAfter?.Delta;

    private static string Fallback(int status) => status switch
    {
        401 => "unauthorized",
        403 => "forbidden",
        404 => "not_found",
        409 => "conflict",
        412 => "precondition_failed",
        429 => "rate_limited",
        503 => "unavailable",
        _ => string.Concat("http_", status.ToString(CultureInfo.InvariantCulture)),
    };

    private static string Message(int status) => string.Concat("The service answered HTTP ", status.ToString(CultureInfo.InvariantCulture), ".");
}
