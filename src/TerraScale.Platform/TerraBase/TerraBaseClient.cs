using System.Diagnostics;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Runtime.CompilerServices;
using System.Text;
using TerraScale.Contracts;

namespace TerraScale.Platform.TerraBase;

public static class TerraBaseRoutes
{
    public const string Health = "healthz";
    public const string Authorize = "v1/internal/authorize";

    public static string InternalSql(Ulid database) => $"v1/internal/databases/{database}/sql/execute";

    public static string InternalSqlSocket(Ulid database) => $"v1/internal/databases/{database}/sql/ws";

    public static string Changefeed(Ulid database, string after) =>
        $"v1/internal/databases/{database}/changefeed?after={Uri.EscapeDataString(after)}";

    public static string PublicSql(string project, string database) =>
        $"v1/projects/{Uri.EscapeDataString(project)}/databases/{Uri.EscapeDataString(database)}/sql/execute";
}

public static class TerraBaseProtocol
{
    public static (Version Version, HttpVersionPolicy Policy) For(Uri baseAddress) =>
        baseAddress.Scheme == Uri.UriSchemeHttps
            ? (HttpVersion.Version20, HttpVersionPolicy.RequestVersionOrLower)
            : (HttpVersion.Version20, HttpVersionPolicy.RequestVersionExact);
}

public sealed record HttpAnswer(int Status, byte[] Body, string? Bookmark, string? RulesVersion);

public sealed record CallerSqlResponse(SqlExecuteResponse Response, string? RulesVersion);

public sealed class TerraBaseClient(IHttpClientFactory httpClients, PlatformOptions options, TerraBaseQuic quic, PlatformMetrics metrics)
{
    public const string HttpClientName = "terrabase";

    public Uri BaseAddress => options.TerraBaseUrl;

    public string PeerSecret => options.PeerSecret;

    public async ValueTask<Result<Done, Unavailable>> PingAsync(CancellationToken cancellationToken)
    {
        using var request = NewRequest(HttpMethod.Get, TerraBaseRoutes.Health);
        return await SendAsync(request, cancellationToken) switch
        {
            HttpAnswer { Status: StatusCodes.Status200OK } => new Done(),
            HttpAnswer answer => new Unavailable($"TerraBase health answered HTTP {answer.Status}", "terrabase_unhealthy"),
            Unavailable unavailable => unavailable,
        };
    }

    public async ValueTask<Result<SqlExecuteResponse, SqlFailure, NotFound, Unavailable>> ExecuteAsync(
        Ulid database,
        SqlExecuteRequest statements,
        SqlContext context,
        CancellationToken cancellationToken)
    {
        using var request = NewRequest(HttpMethod.Post, TerraBaseRoutes.InternalSql(database));
        request.Content = JsonContent.Create(statements, TerraScaleContractsJsonContext.Default.SqlExecuteRequest);
        request.Headers.TryAddWithoutValidation(PlatformOptions.PeerCredentialHeader, options.PeerSecret);
        Consistency(request, context);
        return await SendAsync(request, cancellationToken) switch
        {
            HttpAnswer answer => TerraBaseAnswers.Sql(answer.Status, answer.Body),
            Unavailable unavailable => unavailable,
        };
    }

    public ValueTask<Result<SqlExecuteResponse, SqlFailure, NotFound, Unavailable>> ExecuteLowLatencyAsync(
        Ulid database,
        SqlExecuteRequest statements,
        SqlContext context,
        CancellationToken cancellationToken) =>
        quic.IsEnabled ? quic.ExecuteAsync(database, statements, context, cancellationToken) : ExecuteAsync(database, statements, context, cancellationToken);

    public async ValueTask<Result<CallerSqlResponse, SqlFailure, Unauthorized, Forbidden, NotFound, Unavailable>> ExecuteAsCallerAsync(
        string project,
        string database,
        CallerCredential credential,
        SqlExecuteRequest statements,
        string bookmark,
        CancellationToken cancellationToken)
    {
        using var request = NewRequest(HttpMethod.Post, TerraBaseRoutes.PublicSql(project, database));
        request.Content = JsonContent.Create(statements, TerraScaleContractsJsonContext.Default.SqlExecuteRequest);
        credential.Apply(request, options.PeerSecret);
        Consistency(request, SqlContext.After(bookmark));
        return await SendAsync(request, cancellationToken) switch
        {
            HttpAnswer { Status: StatusCodes.Status401Unauthorized } answer => AsUnauthorized(answer),
            HttpAnswer { Status: StatusCodes.Status403Forbidden } answer => AsForbidden(answer),
            HttpAnswer answer => TerraBaseAnswers.Sql(answer.Status, answer.Body) switch
            {
                SqlExecuteResponse response => new CallerSqlResponse(response, answer.RulesVersion),
                SqlFailure failure => failure,
                NotFound missing => missing,
                Unavailable unavailable => unavailable,
            },
            Unavailable unavailable => unavailable,
        };
    }

    public async ValueTask<Result<CallerGrant, Unauthorized, Forbidden, NotFound, Unavailable>> AuthorizeAsync(
        CallerCredential credential,
        AuthorizeRequest authorize,
        CancellationToken cancellationToken)
    {
        using var request = NewRequest(HttpMethod.Post, TerraBaseRoutes.Authorize);
        request.Content = JsonContent.Create(authorize, TerraScaleContractsJsonContext.Default.AuthorizeRequest);
        request.Headers.TryAddWithoutValidation(PlatformOptions.PeerCredentialHeader, options.PeerSecret);
        credential.Forward(request);
        return await SendAsync(request, cancellationToken) switch
        {
            HttpAnswer { Status: StatusCodes.Status200OK } answer => TerraBaseAnswers.Parse(answer.Body, TerraScaleContractsJsonContext.Default.CallerGrant) is { } grant
                ? grant
                : new Unavailable("TerraBase answered an unreadable grant", "terrabase_protocol"),
            HttpAnswer { Status: StatusCodes.Status401Unauthorized } answer => AsUnauthorized(answer),
            HttpAnswer { Status: StatusCodes.Status403Forbidden } answer => AsForbidden(answer),
            HttpAnswer { Status: StatusCodes.Status404NotFound } answer => AsNotFound(answer),
            HttpAnswer answer => AsUnavailable(answer),
            Unavailable unavailable => unavailable,
        };
    }

    public async ValueTask RecordRequestLogAsync(InternalLogPayload payload, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(payload);
        using var request = NewRequest(HttpMethod.Post, "/internal/logs");
        request.Content = JsonContent.Create(payload, PlatformJsonContext.Default.InternalLogPayload);
        request.Headers.TryAddWithoutValidation(PlatformOptions.PeerCredentialHeader, options.PeerSecret);
        var outcome = await SendAsync(request, cancellationToken);
        _ = outcome switch
        {
            HttpAnswer => true,
            Unavailable => false,
        };
    }

    public async IAsyncEnumerable<Result<ChangefeedHttpItem, Unavailable>> ChangefeedAsync(
        Ulid database,
        string after,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        using var request = NewRequest(HttpMethod.Get, TerraBaseRoutes.Changefeed(database, after));
        request.Headers.TryAddWithoutValidation(PlatformOptions.PeerCredentialHeader, options.PeerSecret);
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue(PlatformHeaders.Changefeed));
        var opened = await OpenStreamAsync(request, cancellationToken);
        var items = opened switch
        {
            ChangefeedReader reader => ReadAllAsync(reader, cancellationToken),
            Unavailable unavailable => Once(unavailable),
        };
        await foreach (var item in items.WithCancellation(cancellationToken))
        {
            yield return item;
        }
    }

    private async IAsyncEnumerable<Result<ChangefeedHttpItem, Unavailable>> ReadAllAsync(
        ChangefeedReader reader,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        using (reader)
        {
            var ended = false;
            while (!ended && !cancellationToken.IsCancellationRequested)
            {
                var line = await reader.NextAsync(cancellationToken);
                ended = line switch
                {
                    ChangefeedHttpItem => Received(),
                    Unavailable => true,
                };
                yield return line;
            }
        }
    }

    private static async IAsyncEnumerable<Result<ChangefeedHttpItem, Unavailable>> Once(Unavailable unavailable)
    {
        await Task.CompletedTask;
        yield return unavailable;
    }

    private bool Received()
    {
        metrics.ChangefeedItem("http");
        return false;
    }

    private async ValueTask<Result<ChangefeedReader, Unavailable>> OpenStreamAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        var client = Client();
        client.Timeout = Timeout.InfiniteTimeSpan;
        HttpResponseMessage response;
        try
        {
            response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        }
        catch (HttpRequestException error)
        {
            return new Unavailable($"TerraBase changefeed is unreachable: {Describe(error)}", "terrabase_unreachable");
        }

        if (response.StatusCode != HttpStatusCode.OK)
        {
            var status = (int)response.StatusCode;
            response.Dispose();
            return new Unavailable($"TerraBase changefeed answered HTTP {status}", "changefeed_refused");
        }

        try
        {
            return new ChangefeedReader(response, await response.Content.ReadAsStreamAsync(cancellationToken));
        }
        catch (HttpRequestException error)
        {
            response.Dispose();
            return new Unavailable($"TerraBase changefeed broke: {error.Message}", "terrabase_unreachable");
        }
    }

    private async ValueTask<Result<HttpAnswer, Unavailable>> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        try
        {
            using var response = await Client().SendAsync(request, HttpCompletionOption.ResponseContentRead, cancellationToken);
            var body = await response.Content.ReadAsByteArrayAsync(cancellationToken);
            var bookmark = response.Headers.TryGetValues(ConsistencyHeaders.Bookmark, out var values) ? values.FirstOrDefault() : null;
            var rulesVersion = response.Headers.TryGetValues(PlatformHeaders.RulesVersion, out var versions) ? versions.FirstOrDefault() : null;
            return new HttpAnswer((int)response.StatusCode, body, bookmark, rulesVersion);
        }
        catch (HttpRequestException error)
        {
            return new Unavailable($"TerraBase is unreachable: {Describe(error)}", "terrabase_unreachable");
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return new Unavailable("TerraBase did not answer in time", "terrabase_timeout");
        }
    }

    public HttpMessageInvoker Invoker() => Client();

    private HttpRequestMessage NewRequest(HttpMethod method, string path)
    {
        var (version, policy) = TerraBaseProtocol.For(options.TerraBaseUrl);
        return new HttpRequestMessage(method, path) { Version = version, VersionPolicy = policy };
    }

    private HttpClient Client() => httpClients.CreateClient(HttpClientName);

    private static string Describe(HttpRequestException error) => error.InnerException is { } inner ? error.Message + " " + inner.Message : error.Message;

    private static void Consistency(HttpRequestMessage request, SqlContext context)
    {
        if (context.Bookmark.Length > 0)
        {
            request.Headers.TryAddWithoutValidation(ConsistencyHeaders.Bookmark, context.Bookmark);
        }

        switch (context.Hint)
        {
            case SqlConsistencyHint.Fast:
                request.Headers.TryAddWithoutValidation(ConsistencyHeaders.Consistency, ConsistencyHeaders.FastValue);
                break;
            case SqlConsistencyHint.Strong:
                request.Headers.TryAddWithoutValidation(ConsistencyHeaders.Consistency, ConsistencyHeaders.StrongValue);
                break;
            case SqlConsistencyHint.DatabaseDefault:
                break;
            default:
                throw new UnreachableException();
        }
    }

    private static Unauthorized AsUnauthorized(HttpAnswer answer)
    {
        var error = TerraBaseAnswers.Error(answer.Status, answer.Body);
        return new Unauthorized(error.Message, error.Code);
    }

    private static Forbidden AsForbidden(HttpAnswer answer)
    {
        var error = TerraBaseAnswers.Error(answer.Status, answer.Body);
        return new Forbidden(error.Message, error.Code, error.Details);
    }

    private static NotFound AsNotFound(HttpAnswer answer)
    {
        var error = TerraBaseAnswers.Error(answer.Status, answer.Body);
        return new NotFound(error.Message, error.Code);
    }

    private static Unavailable AsUnavailable(HttpAnswer answer)
    {
        var error = TerraBaseAnswers.Error(answer.Status, answer.Body);
        return new Unavailable(error.Message, error.Code, error.Details);
    }

    private sealed class ChangefeedReader(HttpResponseMessage response, Stream stream) : IDisposable
    {
        private readonly StreamReader _reader = new(stream);

        public async ValueTask<Result<ChangefeedHttpItem, Unavailable>> NextAsync(CancellationToken cancellationToken)
        {
            while (true)
            {
                string? line;
                try
                {
                    line = await _reader.ReadLineAsync(cancellationToken);
                }
                catch (IOException error)
                {
                    return new Unavailable($"TerraBase changefeed broke: {error.Message}", "changefeed_broken");
                }
                catch (HttpRequestException error)
                {
                    return new Unavailable($"TerraBase changefeed broke: {error.Message}", "changefeed_broken");
                }

                if (line is null)
                {
                    return new Unavailable("TerraBase closed the changefeed", "changefeed_closed");
                }

                if (line.Length == 0)
                {
                    continue;
                }

                return TerraBaseAnswers.Parse(Encoding.UTF8.GetBytes(line), TerraScaleContractsJsonContext.Default.ChangefeedHttpItem) is { } item
                    ? item
                    : new Unavailable("TerraBase sent an unreadable changefeed item", "terrabase_protocol");
            }
        }

        public void Dispose()
        {
            _reader.Dispose();
            response.Dispose();
        }
    }
}
