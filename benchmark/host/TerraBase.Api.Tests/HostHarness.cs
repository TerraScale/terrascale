// <copyright file="HostHarness.cs" company="TerraScale">
// TerraBaseV2 (SE-08). See LICENSE.md.
// </copyright>

namespace TerraBase.Api.Tests;

using System.Net.Http.Json;
using System.Text;
using System.Text.Json;

/// <summary>
/// The in-process host harness: boots the real ASP.NET Core host
/// (<see cref="TerraBaseHost.Create"/>) on an ephemeral loopback port
/// against one engine endpoint — the same served surface the production
/// entry point runs. Tests speak plain HTTP (and SSE/WebSocket) to it.
/// </summary>
public sealed class HostHarness : IAsyncDisposable
{
    private readonly HostBundle bundle;

    private HostHarness(HostBundle bundle)
    {
        this.bundle = bundle;
        // A bounded request timeout: a request that never completes
        // fails with its identity instead of hanging the suite.
        this.Http = new HttpClient { BaseAddress = new Uri(bundle.App.Urls.First()), Timeout = TimeSpan.FromSeconds(30) };
    }

    /// <summary>The supervisor under the host.</summary>
    public EngineSupervisor Supervisor => this.bundle.Supervisor;

    /// <summary>The backbone hub under the host.</summary>
    public BackboneHub Hub => this.bundle.Hub;

    /// <summary>The audit log under the host.</summary>
    public AuditLog Audit => this.bundle.Audit;

    /// <summary>A plain HTTP client bound to the host's loopback URL.</summary>
    public HttpClient Http { get; }

    /// <summary>Boots the host against one engine endpoint.</summary>
    /// <param name="options">The engine endpoint and restart policy.</param>
    /// <returns>The running host.</returns>
    public static async Task<HostHarness> StartAsync(EngineOptions options)
    {
        var bundle = TerraBaseHost.Create(options with { ListenUrl = "http://127.0.0.1:0" });
        await bundle.Supervisor.StartAsync().ConfigureAwait(false);
        await bundle.App.StartAsync().ConfigureAwait(false);
        return new HostHarness(bundle);
    }

    /// <summary>A client with backend-supplied context headers set.</summary>
    /// <param name="principal">The backend principal.</param>
    /// <param name="tenant">The backend-supplied tenant.</param>
    /// <returns>The configured client.</returns>
    public HttpClient As(string principal, string tenant)
    {
        var client = new HttpClient { BaseAddress = this.Http.BaseAddress, Timeout = TimeSpan.FromSeconds(30) };
        client.DefaultRequestHeaders.Add("X-TB-Principal", principal);
        client.DefaultRequestHeaders.Add("X-TB-Tenant", tenant);
        return client;
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
        this.Http.Dispose();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        await this.bundle.App.StopAsync(timeout.Token).ConfigureAwait(false);
        await this.bundle.Supervisor.DisposeAsync().ConfigureAwait(false);
        await this.bundle.App.DisposeAsync().ConfigureAwait(false);
    }
}
