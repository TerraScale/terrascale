// <copyright file="HostFixture.cs" company="TerraScale">
// TerraBaseV2 (SE-13). See LICENSE.md.
// </copyright>

namespace TerraBase.Cli.Tests;

using System.Text;
using System.Text.Json;
using TerraBase.Api;

/// <summary>
/// The live API host fixture of the CLI rows, following the SE-08
/// <c>HostHarness</c> pattern: it boots the real ASP.NET Core host
/// (<see cref="TerraBaseHost.Create"/>) on an ephemeral loopback port
/// over the real engine process the supervisor spawns, watches, and
/// restarts over UDS — the same served surface the production entry
/// point runs. The CLI under test speaks plain HTTP (and SSE) to it,
/// exactly as an operator's terminal would.
/// </summary>
public sealed class HostFixture : IAsyncDisposable
{
    private readonly HostBundle bundle;

    private HostFixture(HostBundle bundle)
    {
        this.bundle = bundle;
        this.Url = bundle.App.Urls.First();
    }

    /// <summary>The loopback URL the host serves.</summary>
    public string Url { get; }

    /// <summary>Boots the host against the real engine process.</summary>
    /// <returns>The running host.</returns>
    public static async Task<HostFixture> StartAsync()
    {
        var binary = await EngineHarness.BinaryPathAsync().ConfigureAwait(false);
        var bundle = TerraBaseHost.Create(new EngineOptions
        {
            SocketPath = EngineHarness.SocketPath(),
            ManagedBinary = binary,
            MaxRestarts = 1,
            ListenUrl = "http://127.0.0.1:0",
        });
        await bundle.Supervisor.StartAsync().ConfigureAwait(false);
        await bundle.App.StartAsync().ConfigureAwait(false);
        return new HostFixture(bundle);
    }

    /// <summary>A client with backend-supplied context headers set (the commit driver).</summary>
    /// <param name="principal">The backend principal.</param>
    /// <param name="tenant">The backend-supplied tenant.</param>
    /// <returns>The configured client.</returns>
    public HttpClient As(string principal, string tenant)
    {
        var client = new HttpClient { BaseAddress = new Uri(this.Url), Timeout = TimeSpan.FromSeconds(30) };
        client.DefaultRequestHeaders.Add("X-TB-Principal", principal);
        client.DefaultRequestHeaders.Add("X-TB-Tenant", tenant);
        return client;
    }

    /// <summary>Opens one sync session on the caller's tenant (the commit driver of the changes rows).</summary>
    /// <param name="caller">The caller's client.</param>
    /// <param name="collection">The collection scope.</param>
    /// <returns>The session id.</returns>
    public static async Task<ulong> OpenSessionAsync(HttpClient caller, string collection)
    {
        var (status, body) = await SendAsync(caller, "/v1/sync/sessions", new { collection }).ConfigureAwait(false);
        if (status != 200)
        {
            throw new InvalidOperationException($"the sync session setup failed: {status}");
        }

        return body.RootElement.GetProperty("sessionId").GetUInt64();
    }

    /// <summary>Submits one server-ack mutation through the session (lands a journal commit on the backbone).</summary>
    /// <param name="caller">The caller's client.</param>
    /// <param name="sessionId">The session.</param>
    /// <param name="document">The document id.</param>
    /// <param name="operation">The operation id and idempotency key.</param>
    /// <returns>The commit receipt's sequence.</returns>
    public static async Task<ulong> CommitAsync(HttpClient caller, ulong sessionId, string document, string operation)
    {
        var (status, body) = await SendAsync(
            caller,
            $"/v1/sync/sessions/{sessionId}/mutations",
            new
            {
                document,
                // The field's VALUE is a distinctive body string so the
                // content-free rows can prove it never leaks into a
                // Change Notification or a machine envelope.
                change = new { put = new[] { new { name = "note", value = new { @string = $"body-of-{operation}" } } } },
            },
            idempotencyKey: operation).ConfigureAwait(false);
        if (status != 200)
        {
            throw new InvalidOperationException($"the mutation failed: {status}");
        }

        return body.RootElement.GetProperty("outcome").GetProperty("receipt").GetProperty("sequence").GetUInt64();
    }

    /// <summary>Sends a JSON request and reads the answer's status and body.</summary>
    /// <param name="client">The caller's client.</param>
    /// <param name="path">The path.</param>
    /// <param name="body">The request body, or <c>null</c> for none.</param>
    /// <param name="idempotencyKey">The idempotency key, when the operation is a mutation.</param>
    /// <param name="method">The method; the default is GET without a body and POST with one.</param>
    /// <returns>The status and the parsed JSON document.</returns>
    public static async Task<(int Status, JsonDocument Body)> SendAsync(
        HttpClient client,
        string path,
        object? body = null,
        string? idempotencyKey = null,
        string? method = null)
    {
        using var request = new HttpRequestMessage(
            method is null ? (body is null ? HttpMethod.Get : HttpMethod.Post) : new HttpMethod(method),
            path);
        if (body is not null)
        {
            request.Content = new StringContent(JsonSerializer.Serialize(body), Encoding.UTF8, "application/json");
        }

        if (idempotencyKey is not null)
        {
            request.Headers.Add("Idempotency-Key", idempotencyKey);
        }

        using var answer = await client.SendAsync(request).ConfigureAwait(false);
        var json = await answer.Content.ReadAsStringAsync().ConfigureAwait(false);
        return ((int)answer.StatusCode, JsonDocument.Parse(json.Length > 0 ? json : "{}"));
    }

    /// <inheritdoc/>
    public async ValueTask DisposeAsync()
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        await this.bundle.App.StopAsync(timeout.Token).ConfigureAwait(false);
        await this.bundle.Supervisor.DisposeAsync().ConfigureAwait(false);
        await this.bundle.App.DisposeAsync().ConfigureAwait(false);
    }
}
