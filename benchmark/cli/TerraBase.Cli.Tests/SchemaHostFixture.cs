// <copyright file="SchemaHostFixture.cs" company="TerraScale">
// TerraBaseV2 (SM-04). See LICENSE.md.
// </copyright>

namespace TerraBase.Cli.Tests;

using System.Text;
using TerraBase.Api;
using TerraBase.Api.Tests;

/// <summary>
/// The live API host fixture of the SM-04 CLI rows: the real ASP.NET Core
/// host (<see cref="TerraBaseHost.Create"/>) on an ephemeral loopback port
/// over the scripted TBEP engine of the host test family. Serving the
/// schema lifecycle from a real engine process is the engine-backed host
/// integrator action the SM-04 record names, so the fixture scripts the
/// same typed wire answers the registry defines; the host, the protocol
/// client, and the CLI under test are all the real production code.
/// </summary>
public sealed class SchemaHostFixture : IAsyncDisposable
{
    private readonly ScriptedTbepEngine engine;
    private readonly HostBundle bundle;

    private SchemaHostFixture(ScriptedTbepEngine engine, HostBundle bundle)
    {
        this.engine = engine;
        this.bundle = bundle;
        this.Url = bundle.App.Urls.First();
    }

    /// <summary>The loopback URL the host serves.</summary>
    public string Url { get; }

    /// <summary>The engine under the host, for scripting schema state before a command runs.</summary>
    public ScriptedTbepEngine Engine => this.engine;

    /// <summary>The migration contract JSON the rows pass to the dry-run and apply commands.</summary>
    public const string Contract =
        """{"collection":"users","fromVersion":1,"toVersion":2,"target":{"$schema":"https://json-schema.org/draft/2020-12/schema","type":"object","properties":{"email":{"type":"string"},"login_count":{"type":"integer"}},"required":["email"],"x-terrabase-schema-version":2},"transforms":[{"kind":"remove_field","field":"nick"},{"kind":"add_field","field":"login_count","default":{"integer":0}}]}""";

    /// <summary>The schema document JSON the rows declare before a command runs.</summary>
    public const string Schema =
        """{"$schema":"https://json-schema.org/draft/2020-12/schema","type":"object","properties":{"email":{"type":"string"},"nick":{"type":"string"}},"required":["email"],"x-terrabase-schema-version":1}""";

    /// <summary>Boots the host over the scripted schema engine.</summary>
    /// <returns>The running fixture.</returns>
    public static async Task<SchemaHostFixture> StartAsync()
    {
        var path = Path.Combine(Path.GetTempPath(), $"tbep-schema-cli-{Guid.NewGuid():N}.sock");
        var engine = new ScriptedTbepEngine(path);
        var bundle = TerraBaseHost.Create(new EngineOptions { SocketPath = path, ListenUrl = "http://127.0.0.1:0" });
        await bundle.Supervisor.StartAsync().ConfigureAwait(false);
        try
        {
            await bundle.App.StartAsync().ConfigureAwait(false);
        }
        catch
        {
            await bundle.Supervisor.DisposeAsync().ConfigureAwait(false);
            await bundle.App.DisposeAsync().ConfigureAwait(false);
            await engine.DisposeAsync().ConfigureAwait(false);
            throw;
        }

        return new SchemaHostFixture(engine, bundle);
    }

    /// <summary>Declares one collection's schema through the host's own typed endpoint.</summary>
    /// <param name="tenant">The backend-supplied tenant.</param>
    /// <param name="collection">The collection.</param>
    /// <param name="schema">The schema document JSON.</param>
    /// <param name="version">The version the scripted engine records.</param>
    /// <returns>A task that completes when the declaration is answered.</returns>
    public async Task DeclareAsync(string tenant, string collection, string schema, ulong version)
    {
        using var client = new HttpClient { BaseAddress = new Uri(this.Url), Timeout = TimeSpan.FromSeconds(30) };
        client.DefaultRequestHeaders.Add("X-TB-Principal", "cli-fixture");
        client.DefaultRequestHeaders.Add("X-TB-Tenant", tenant);
        using var content = new StringContent($"{{\"schema\":{schema}}}", Encoding.UTF8, new System.Net.Http.Headers.MediaTypeHeaderValue("application/json"));
        using var request = new HttpRequestMessage(HttpMethod.Put, $"/v1/schemas/{collection}") { Content = content };
        request.Headers.Add("Idempotency-Key", $"fixture-declare-{version}");
        using var answer = await client.SendAsync(request).ConfigureAwait(false);
        _ = answer.EnsureSuccessStatusCode();
    }

    /// <inheritdoc/>
    public async ValueTask DisposeAsync()
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        await this.bundle.App.StopAsync(timeout.Token).ConfigureAwait(false);
        await this.bundle.Supervisor.DisposeAsync().ConfigureAwait(false);
        await this.bundle.App.DisposeAsync().ConfigureAwait(false);
        await this.engine.DisposeAsync().ConfigureAwait(false);
    }
}
