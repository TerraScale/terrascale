using System.Collections.Concurrent;
using System.Net;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Time.Testing;
using TerraBase.Features.Webhooks;
using TerraBase.Primitives;

namespace TerraBase.Testing;

internal sealed class WebhookApi : IAsyncDisposable
{
    private readonly string _directory = Directory.CreateTempSubdirectory("terrabase-webhooks-").FullName;
    private readonly string _machine = "webhooks-" + EntityIds.New().ToString().ToLowerInvariant();
    private ManagementApi? _api;
    private HttpClient? _client;

    private WebhookApi(WebhookReceiver receiver)
    {
        Receiver = receiver;
    }

    public WebhookReceiver Receiver { get; }
    public FakeTimeProvider Clock { get; } = new(DateTimeOffset.UtcNow);
    public WebhookLogs Logs { get; } = new();
    public ManagementApi Api => _api ?? throw new InvalidOperationException("server is stopped");
    public HttpClient Client => _client ?? throw new InvalidOperationException("server is stopped");
    public ManagedDatabaseRef Database { get; private set; } = null!;
    public string Path => Route(Database);

    public static string Route(ManagedDatabaseRef database, string collection = "items") =>
        $"{database.Data}/collections/{collection}/webhooks";

    public static Task<WebhookApi> StartAsync(CancellationToken token) => StartAsync(null, token);

    public static async Task<WebhookApi> StartAsync(Action<IServiceCollection>? services, CancellationToken token)
    {
        var fixture = new WebhookApi(await WebhookReceiver.StartAsync(token));
        try
        {
            await fixture.StartNodeAsync(fixture._machine, services, token);
            fixture.Database = await fixture.Api.CreateManagedDatabaseAsync();
            fixture._client = fixture.ProjectClient();
            await ManagementApi.CreateCollectionAsync(fixture.Client, fixture.Database, "items", token);
            return fixture;
        }
        catch
        {
            await fixture.DisposeAsync();
            throw;
        }
    }

    public HttpClient ProjectClient(string scopes = "documents:read documents:write", string? project = null, string? database = null) =>
        Api.WithToken(Api.Issuer.Mint(Api.User, descriptor =>
        {
            descriptor.Claims["project_id"] = project ?? Database.Project;
            descriptor.Claims["scope"] = scopes;
            if (database is not null)
            {
                descriptor.Claims["database"] = database;
            }
        }));

    public async Task StopAsync()
    {
        _client?.Dispose();
        _client = null;
        if (_api is { } api)
        {
            _api = null;
            await api.DisposeAsync();
        }
    }

    public async Task RestartAsync(CancellationToken token, bool differentMachine = false)
    {
        await StopAsync();
        await StartNodeAsync(differentMachine ? _machine + "-other" : _machine, null, token);
        _client = ProjectClient();
    }

    private async Task StartNodeAsync(string machine, Action<IServiceCollection>? configure, CancellationToken token) =>
        _api = await ManagementApi.StartAsync(settings =>
        {
            settings["data-directory"] = _directory;
            settings["node-name"] = machine;
            settings["region"] = "gru";
            settings["auth-issuer"] = TestIssuer.Issuer;
        }, services =>
        {
            Receiver.Configure(services);
            TerraBaseTestServer.CaptureDebugLogs(services, Logs);
            configure?.Invoke(services);
        }, Clock, cancellationToken: token);

    public async Task<JsonElement> RegisterAsync(CancellationToken token, string? path = null)
    {
        using var response = await Client.PostAsJsonAsync(ManagementApi.Uri(path ?? Path), new { url = WebhookReceiver.Url }, token);
        await Assert.That(response.StatusCode).IsEqualTo(HttpStatusCode.Created).Because(await response.Content.ReadAsStringAsync(token));
        return await response.Content.ReadFromJsonAsync<JsonElement>(token);
    }

    public async Task PutAsync(string name, CancellationToken token, ManagedDatabaseRef? database = null, string collection = "items")
    {
        using var response = await Client.PutAsJsonAsync(ManagementApi.Uri(ManagementApi.DocumentPath(database ?? Database, collection, name)),
            new { privateValue = "document-body-must-not-be-delivered" }, token);
        response.EnsureSuccessStatusCode();
    }

    public async Task<JsonElement> JsonAsync(string path, CancellationToken token)
    {
        using var response = await Client.GetAsync(ManagementApi.Uri(path), token);
        response.EnsureSuccessStatusCode();
        return await response.Content.ReadFromJsonAsync<JsonElement>(token);
    }

    public async Task<WebhookRequest> ReceiveAsync(CancellationToken token)
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(token);
        deadline.CancelAfter(TimeSpan.FromSeconds(15));
        var request = Receiver.ReadAsync(deadline.Token).AsTask();
        // A request can arrive while its pass is still held (a paused acknowledgment), so it wakes the wait too.
        await DriveAsync(_ => Task.FromResult(request.IsCompleted), deadline.Token, request);
        return await request.WaitAsync(deadline.Token);
    }

    public async Task<JsonElement> DeliveryAsync(string webhook, string status, CancellationToken token, string? error = null)
    {
        var found = default(JsonElement);
        await ObserveAsync(async cancellation =>
        {
            var list = await JsonAsync($"{Path}/{webhook}/deliveries", cancellation);
            if (list.GetArrayLength() == 0)
            {
                return false;
            }

            found = list[0];
            return found.GetProperty("status").GetString() == status
                && (error is null || found.GetProperty("errorCode").GetString() == error);
        }, token);
        return found;
    }

    public async Task DriveAsync(Func<CancellationToken, Task<bool>> condition, CancellationToken token, Task? progress = null)
    {
        if (await condition(token))
        {
            return;
        }

        Clock.Advance(TimeSpan.FromSeconds(1));
        await ObserveAsync(condition, token, progress);
    }

    public async Task ObserveAsync(Func<CancellationToken, Task<bool>> condition, CancellationToken token, Task? progress = null)
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(token);
        deadline.CancelAfter(TimeSpan.FromSeconds(15));
        while (true)
        {
            // Take the pass before checking, so progress between the check and the wait is never missed. Waiting on the
            // worker instead of polling the listener keeps the tenant rate budget, which refills on the fake clock only.
            var pass = Api.Server.Services.GetRequiredService<WebhookService>().NextPassAsync();
            if (await condition(deadline.Token))
            {
                return;
            }

            _ = await Task.WhenAny(pass, progress ?? pass).WaitAsync(deadline.Token);
        }
    }

    public async ValueTask DisposeAsync()
    {
        try
        {
            await StopAsync();
        }
        finally
        {
            await Receiver.DisposeAsync();
            Directory.Delete(_directory, recursive: true);
        }
    }
}

internal sealed class WebhookLogs : ILoggerProvider
{
    private readonly ConcurrentQueue<string> _messages = new();
    public string Text => string.Join('\n', _messages);
    public ILogger CreateLogger(string categoryName) => new Logger(_messages);
    public void Dispose() { }

    private sealed class Logger(ConcurrentQueue<string> messages) : ILogger
    {
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => true;
        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter) =>
            messages.Enqueue(formatter(state, exception));
    }
}
