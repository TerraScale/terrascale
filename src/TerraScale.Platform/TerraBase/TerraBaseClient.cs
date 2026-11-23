using System.Diagnostics;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Runtime.CompilerServices;
using System.Text;
using TerraScale.Contracts;

namespace TerraScale.Platform.TerraBase;

public sealed record TerraBaseHealthy;

public readonly union TerraBaseHealthOutcome(TerraBaseHealthy, PlatformUnavailable);

public readonly union CallerSqlOutcome(
    CallerSqlResponse,
    SqlFailure,
    PlatformUnauthorized,
    PlatformForbidden,
    PlatformNotFound,
    PlatformUnavailable);

public readonly union AuthorizeCallerOutcome(
    CallerGrant,
    PlatformUnauthorized,
    PlatformForbidden,
    PlatformNotFound,
    PlatformUnavailable);

public readonly union ChangefeedReadOutcome(ChangefeedHttpItem, PlatformUnavailable);

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

    public async ValueTask<TerraBaseHealthOutcome> PingAsync(CancellationToken cancellationToken)
    {
        using var request = NewRequest(HttpMethod.Get, TerraBaseRoutes.Health);
        var outcome = await SendAsync(request, cancellationToken);
        return outcome switch
        {
            HttpAnswer { Status: StatusCodes.Status200OK } => new TerraBaseHealthy(),
            HttpAnswer answer => new PlatformUnavailable($"TerraBase health answered HTTP {answer.Status}", "terrabase_unhealthy"),
            PlatformUnavailable unavailable => unavailable,
        };
    }

    public async ValueTask<InternalSqlOutcome> ExecuteAsync(
        Ulid database,
        SqlExecuteRequest statements,
        SqlContext context,
        CancellationToken cancellationToken)
    {
        using var request = NewRequest(HttpMethod.Post, TerraBaseRoutes.InternalSql(database));
        request.Content = JsonContent.Create(statements, TerraScaleContractsJsonContext.Default.SqlExecuteRequest);
        request.Headers.TryAddWithoutValidation(PlatformOptions.PeerCredentialHeader, options.PeerSecret);
        Consistency(request, context);
        var outcome = await SendAsync(request, cancellationToken);
        return outcome switch
        {
            HttpAnswer answer => TerraBaseAnswers.Sql(answer.Status, answer.Body),
            PlatformUnavailable unavailable => unavailable,
        };
    }

    public ValueTask<InternalSqlOutcome> ExecuteLowLatencyAsync(
        Ulid database,
        SqlExecuteRequest statements,
        SqlContext context,
        CancellationToken cancellationToken) =>
        quic.IsEnabled ? quic.ExecuteAsync(database, statements, context, cancellationToken) : ExecuteAsync(database, statements, context, cancellationToken);

    public async ValueTask<CallerSqlOutcome> ExecuteAsCallerAsync(
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
        var outcome = await SendAsync(request, cancellationToken);
        return outcome switch
        {
            HttpAnswer { Status: StatusCodes.Status401Unauthorized } answer => AsUnauthorized(answer),
            HttpAnswer { Status: StatusCodes.Status403Forbidden } answer => AsForbidden(answer),
            HttpAnswer answer => TerraBaseAnswers.Sql(answer.Status, answer.Body) switch
            {
                SqlExecuteResponse response => new CallerSqlResponse(response, answer.RulesVersion),
                SqlFailure failure => failure,
                PlatformNotFound missing => missing,
                PlatformUnavailable unavailable => unavailable,
            },
            PlatformUnavailable unavailable => unavailable,
        };
    }

    public async ValueTask<AuthorizeCallerOutcome> AuthorizeAsync(
        CallerCredential credential,
        AuthorizeRequest authorize,
        CancellationToken cancellationToken)
    {
        using var request = NewRequest(HttpMethod.Post, TerraBaseRoutes.Authorize);
        request.Content = JsonContent.Create(authorize, TerraScaleContractsJsonContext.Default.AuthorizeRequest);
        request.Headers.TryAddWithoutValidation(PlatformOptions.PeerCredentialHeader, options.PeerSecret);
        credential.Forward(request);
        var outcome = await SendAsync(request, cancellationToken);
        return outcome switch
        {
            HttpAnswer { Status: StatusCodes.Status200OK } answer => TerraBaseAnswers.Parse(answer.Body, TerraScaleContractsJsonContext.Default.CallerGrant) is { } grant
                ? grant
                : new PlatformUnavailable("TerraBase answered an unreadable grant", "terrabase_protocol"),
            HttpAnswer { Status: StatusCodes.Status401Unauthorized } answer => AsUnauthorized(answer),
            HttpAnswer { Status: StatusCodes.Status403Forbidden } answer => AsForbidden(answer),
            HttpAnswer { Status: StatusCodes.Status404NotFound } answer => AsNotFound(answer),
            HttpAnswer answer => AsUnavailable(answer),
            PlatformUnavailable unavailable => unavailable,
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
            PlatformUnavailable => false,
        };
    }

    public async IAsyncEnumerable<ChangefeedReadOutcome> ChangefeedAsync(
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
            PlatformUnavailable unavailable => Once(unavailable),
        };
        await foreach (var item in items.WithCancellation(cancellationToken))
        {
            yield return item;
        }
    }

    private async IAsyncEnumerable<ChangefeedReadOutcome> ReadAllAsync(
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
                    PlatformUnavailable => true,
                };
                yield return line;
            }
        }
    }

    private static async IAsyncEnumerable<ChangefeedReadOutcome> Once(PlatformUnavailable unavailable)
    {
        await Task.CompletedTask;
        yield return unavailable;
    }

    private bool Received()
    {
        metrics.ChangefeedItem("http");
        return false;
    }

    private readonly union OpenChangefeedOutcome(ChangefeedReader, PlatformUnavailable);

    private async ValueTask<OpenChangefeedOutcome> OpenStreamAsync(HttpRequestMessage request, CancellationToken cancellationToken)
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
            return new PlatformUnavailable($"TerraBase changefeed is unreachable: {Describe(error)}", "terrabase_unreachable");
        }

        if (response.StatusCode != HttpStatusCode.OK)
        {
            var status = (int)response.StatusCode;
            response.Dispose();
            return new PlatformUnavailable($"TerraBase changefeed answered HTTP {status}", "changefeed_refused");
        }

        try
        {
            return new ChangefeedReader(response, await response.Content.ReadAsStreamAsync(cancellationToken));
        }
        catch (HttpRequestException error)
        {
            response.Dispose();
            return new PlatformUnavailable($"TerraBase changefeed broke: {error.Message}", "terrabase_unreachable");
        }
    }

    private readonly union SendTerraBaseRequestOutcome(HttpAnswer, PlatformUnavailable);

    private async ValueTask<SendTerraBaseRequestOutcome> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
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
            return new PlatformUnavailable($"TerraBase is unreachable: {Describe(error)}", "terrabase_unreachable");
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return new PlatformUnavailable("TerraBase did not answer in time", "terrabase_timeout");
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

    private static PlatformUnauthorized AsUnauthorized(HttpAnswer answer)
    {
        var error = TerraBaseAnswers.Error(answer.Status, answer.Body);
        return new PlatformUnauthorized(error.Message, error.Code);
    }

    private static PlatformForbidden AsForbidden(HttpAnswer answer)
    {
        var error = TerraBaseAnswers.Error(answer.Status, answer.Body);
        return new PlatformForbidden(error.Message, error.Code, error.Details);
    }

    private static PlatformNotFound AsNotFound(HttpAnswer answer)
    {
        var error = TerraBaseAnswers.Error(answer.Status, answer.Body);
        return new PlatformNotFound(error.Message, error.Code);
    }

    private static PlatformUnavailable AsUnavailable(HttpAnswer answer)
    {
        var error = TerraBaseAnswers.Error(answer.Status, answer.Body);
        return new PlatformUnavailable(error.Message, error.Code, error.Details);
    }

    private sealed class ChangefeedReader(HttpResponseMessage response, Stream stream) : IDisposable
    {
        private readonly StreamReader _reader = new(stream);

        public async ValueTask<ChangefeedReadOutcome> NextAsync(CancellationToken cancellationToken)
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
                    return new PlatformUnavailable($"TerraBase changefeed broke: {error.Message}", "changefeed_broken");
                }
                catch (HttpRequestException error)
                {
                    return new PlatformUnavailable($"TerraBase changefeed broke: {error.Message}", "changefeed_broken");
                }

                if (line is null)
                {
                    return new PlatformUnavailable("TerraBase closed the changefeed", "changefeed_closed");
                }

                if (line.Length == 0)
                {
                    continue;
                }

                return TerraBaseAnswers.Parse(Encoding.UTF8.GetBytes(line), TerraScaleContractsJsonContext.Default.ChangefeedHttpItem) is { } item
                    ? item
                    : new PlatformUnavailable("TerraBase sent an unreadable changefeed item", "terrabase_protocol");
            }
        }

        public void Dispose()
        {
            _reader.Dispose();
            response.Dispose();
        }
    }
}
