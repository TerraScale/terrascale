using System.Buffers;
using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Net.WebSockets;
using System.Runtime.CompilerServices;
using System.Text.Json;
using Microsoft.AspNetCore.WebUtilities;
using TerraScale.Dashboard.Common.Live;
using TerraScale.Dashboard.Features.Auth;

namespace TerraScale.Dashboard.Features.Databases;

public static partial class DatabaseTablesHints
{
    private const string Protocol = "terrascale.shape.v1";
    private static readonly TimeSpan HintedPollInterval = TimeSpan.FromSeconds(6);
    private static readonly SearchValues<char> Hex = SearchValues.Create("0123456789abcdef");

    public static async IAsyncEnumerable<DateTimeOffset> ChangesAsync(HttpContext http,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        var time = http.RequestServices.GetRequiredService<TimeProvider>();
        var logger = http.RequestServices.GetRequiredService<ILoggerFactory>().CreateLogger(typeof(DatabaseTablesHints));
        yield return time.GetUtcNow();
        using var stop = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        using var timer = new PeriodicTimer(LiveView.PollInterval, time);
        Task<ClientWebSocket?>? opening = OpenAsync(http, logger, stop.Token);
        ClientWebSocket? socket = null;
        Task<bool>? hint = null;
        var tick = timer.WaitForNextTickAsync(stop.Token).AsTask();
        try
        {
            while (true)
            {
                var source = (Task?)hint ?? (Task?)opening ?? tick;
                var next = await Task.WhenAny(tick, source);
                if (opening is not null && next == opening)
                {
                    socket = await opening;
                    opening = null;
                    hint = socket is null ? null : ReadAsync(socket, logger, stop.Token);
                    timer.Period = hint is null ? LiveView.PollInterval : HintedPollInterval;
                    continue;
                }

                if (hint is not null && next == hint)
                {
                    var changed = await hint;
                    hint = changed ? ReadAsync(socket!, logger, stop.Token) : null;
                    if (!changed)
                    {
                        socket?.Dispose();
                        timer.Period = LiveView.PollInterval;
                        continue;
                    }
                }
                else
                {
                    if (!await tick)
                    {
                        yield break;
                    }

                    tick = timer.WaitForNextTickAsync(stop.Token).AsTask();
                }

                yield return time.GetUtcNow();
            }
        }
        finally
        {
            await stop.CancelAsync();
            if (opening is not null)
            {
                await DisposeOpeningAsync(opening, stop.Token);
            }

            socket?.Dispose();
            if (hint is not null)
            {
                await hint;
            }
        }
    }

    private static async Task DisposeOpeningAsync(Task<ClientWebSocket?> opening, CancellationToken cancellationToken)
    {
        try
        {
            (await opening)?.Dispose();
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            return;
        }
    }

    private static async Task<ClientWebSocket?> OpenAsync(HttpContext http, ILogger logger, CancellationToken cancellationToken)
    {
        ClientWebSocket? socket = null;
        try
        {
            var context = await AccountRequest.RequireAsync(http,
                http.RequestServices.GetRequiredService<DashboardSessions>(), http.RequestServices.GetRequiredService<AccountService>());
            if (context is null)
            {
                return null;
            }

            var databases = http.RequestServices.GetRequiredService<DatabaseService>();
            var id = http.Request.RouteValues["databaseId"]?.ToString() ?? "";
            var (current, database) = await databases.LookupAsync(context, id, cancellationToken);
            var home = await databases.HomeAsync(current, database, cancellationToken);
            var catalog = await http.RequestServices.GetRequiredService<IDatabaseBackend>()
                .SqlTablesAsync(DatabaseService.Scope(current), home, database.Id, cancellationToken);
            var selected = http.Request.Query["table"].ToString();
            var table = catalog.Tables.FirstOrDefault(table => table.Name == selected)
                ?? (selected.Length == 0 && catalog.Tables.Count > 0 ? catalog.Tables[0] : null);
            if (table is null)
            {
                return null;
            }

            var token = await http.RequestServices.GetRequiredService<IProjectAccessTokens>()
                .GetAsync(current.Session, current.Organization!.Id, database.ProjectId, ["sql:read"], cancellationToken);
            var path = $"/v1/projects/{Uri.EscapeDataString(database.ProjectId)}/databases/{Uri.EscapeDataString(database.Id)}/tables/{Uri.EscapeDataString(table.Name)}/shape";
            using var client = new HttpClient(new SocketsHttpHandler { AllowAutoRedirect = false, UseProxy = false, UseCookies = false });
            using var request = new HttpRequestMessage(HttpMethod.Get, new Uri(home.Api, path + "?offset=-1"));
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
            using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
            if (response.StatusCode != HttpStatusCode.TemporaryRedirect || response.Headers.Location is not { } location
                || !response.Headers.TryGetValues("TB-Shape-Hints", out var protocols) || !protocols.Contains(Protocol, StringComparer.Ordinal))
            {
                throw new HttpRequestException("TerraBase shape hints are unavailable.", null, response.StatusCode);
            }

            var query = QueryHelpers.ParseQuery(new Uri(home.Api, location).Query);
            var handle = query.GetValueOrDefault("handle").ToString();
            var offset = query.GetValueOrDefault("offset").ToString().Split('.');
            if (handle.Length != 19 || !handle.StartsWith("h1-", StringComparison.Ordinal) || handle.AsSpan(3).ContainsAnyExcept(Hex)
                || offset is not ["snap", var boundary, "0"] || !long.TryParse(boundary, NumberStyles.None, CultureInfo.InvariantCulture, out _))
            {
                throw new JsonException("TerraBase returned an invalid shape bootstrap.");
            }

            var uri = new UriBuilder(new Uri(home.Api, path + "/ws"))
            { Scheme = "wss", Query = QueryString.Create("handle", handle).Add("offset", boundary).Value };
            socket = new ClientWebSocket();
            socket.Options.Proxy = null;
            socket.Options.AddSubProtocol(Protocol);
            socket.Options.SetRequestHeader("Authorization", "Bearer " + token);
            await socket.ConnectAsync(uri.Uri, cancellationToken);
            return socket;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            socket?.Dispose();
            throw;
        }
        catch (Exception error) when (!cancellationToken.IsCancellationRequested &&
            (DatabaseResults.KnownFailure(error) || error is HttpRequestException or IOException or WebSocketException or JsonException or OperationCanceledException))
        {
            socket?.Dispose();
            HintsUnavailable(logger, error);
            return null;
        }
    }

    private static async Task<bool> ReadAsync(ClientWebSocket socket, ILogger logger, CancellationToken cancellationToken)
    {
        try
        {
            var bytes = new byte[64 * 1024];
            while (true)
            {
                var length = 0;
                ValueWebSocketReceiveResult frame;
                do
                {
                    frame = await socket.ReceiveAsync(bytes.AsMemory(length), cancellationToken);
                    if (frame.MessageType != WebSocketMessageType.Text || (length += frame.Count) >= bytes.Length)
                    {
                        throw new WebSocketException("The shape hint stream ended or exceeded its frame limit.");
                    }
                } while (!frame.EndOfMessage);

                using var message = JsonDocument.Parse(bytes.AsMemory(0, length));
                if (message.RootElement.ValueKind != JsonValueKind.Object || !message.RootElement.TryGetProperty("type", out var kind)
                    || kind.ValueKind != JsonValueKind.String)
                {
                    throw new JsonException("TerraBase returned an invalid shape hint.");
                }

                var type = kind.GetString();
                if (type is "new_data" or "provisional")
                {
                    return true;
                }

                if (type != "ready")
                {
                    throw new JsonException("TerraBase ended its shape hints.");
                }
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            return false;
        }
        catch (Exception error) when (error is WebSocketException or JsonException or IOException)
        {
            HintsUnavailable(logger, error);
            return false;
        }
    }

    [LoggerMessage(Level = LogLevel.Warning, Message = "Table change hints are unavailable; using the server timer")]
    private static partial void HintsUnavailable(ILogger logger, Exception exception);
}
