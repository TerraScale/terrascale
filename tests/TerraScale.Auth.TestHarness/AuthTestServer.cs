using System.Net;
using System.Security.Cryptography;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.Extensions.DependencyInjection;
using TerraScale.Auth.Configuration;

namespace TerraScale.Auth.Tests;

/// <summary>
/// Runs the real ts-auth application on an ephemeral loopback Kestrel port and talks to it
/// over HTTP. Each instance has its own synthetic database name and secrets.
/// </summary>
public sealed class AuthTestServer : IAsyncDisposable
{
    private static int sequence;

    private AuthTestServer(WebApplication app, Uri baseAddress)
    {
        this.App = app;
        this.BaseAddress = baseAddress;
        this.Client = NewClient();
    }

    /// <summary>The running application; use only to resolve test doubles registered by the test.</summary>
    public WebApplication App { get; }

    /// <summary>The loopback origin the server listens on.</summary>
    public Uri BaseAddress { get; }

    /// <summary>A client that never follows redirects and never stores cookies.</summary>
    public HttpClient Client { get; }

    /// <summary>Settings every test server starts from: Development mode with fresh synthetic secrets.</summary>
    public static Dictionary<string, string> DefaultSettings() => new(StringComparer.Ordinal)
    {
        ["listen"] = "http://127.0.0.1:0",
        ["public-origin"] = "https://auth.test.invalid",
        ["terrascale-api-url"] = "https://127.0.0.1:9",
        ["terrascale-api-key"] = "contract-fixture-key",
        ["database"] = $"{Guid.NewGuid():D}/ts-auth-test-{Environment.ProcessId}-{Interlocked.Increment(ref sequence)}",
        ["master-key"] = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32)),
        ["audit-signing-key"] = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32)),
        ["operator-key"] = Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(24)),
        ["environment"] = "Development",
    };

    /// <summary>
    /// Starts a server. <paramref name="settings"/> edits the defaults, <paramref name="services"/>
    /// replaces infrastructure, and <paramref name="routes"/> adds test-only routes that exist
    /// only inside this test process.
    /// </summary>
    public static async Task<AuthTestServer> StartAsync(
        Action<Dictionary<string, string>>? settings = null,
        Action<IServiceCollection>? services = null,
        Action<WebApplication>? routes = null)
    {
        var values = DefaultSettings();
        settings?.Invoke(values);
        var app = TerraScale.Auth.App.Build(AuthOptions.Load(values), builder => services?.Invoke(builder.Services));
        try
        {
            routes?.Invoke(app);
            await app.StartAsync();
            var address = app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.Single();
            return new AuthTestServer(app, new Uri(address.Replace("[::]", "127.0.0.1", StringComparison.Ordinal)));
        }
        catch (Exception startFailure)
        {
            // Never leave a listener or DI-owned resources behind for later tests, and never
            // let a cleanup failure hide why startup failed.
            try
            {
                await app.DisposeAsync();
            }
            catch (Exception disposeFailure)
            {
                throw new AggregateException(startFailure, disposeFailure);
            }

            throw;
        }
    }

    /// <summary>Creates another independent client for the same server.</summary>
    public HttpClient NewClient() =>
        new(new SocketsHttpHandler { AllowAutoRedirect = false, UseCookies = false, AutomaticDecompression = DecompressionMethods.None })
        {
            BaseAddress = this.BaseAddress,
            Timeout = TimeSpan.FromSeconds(30),
        };

    /// <inheritdoc />
    public async ValueTask DisposeAsync()
    {
        this.Client.Dispose();
        Exception? stopFailure = null;
        try
        {
            await this.App.StopAsync();
        }
        catch (Exception exception)
        {
            stopFailure = exception;
        }

        // Always dispose, and never let a disposal failure hide the shutdown failure.
        try
        {
            await this.App.DisposeAsync();
        }
        catch (Exception disposeFailure) when (stopFailure is not null)
        {
            throw new AggregateException(stopFailure, disposeFailure);
        }

        if (stopFailure is not null)
        {
            System.Runtime.ExceptionServices.ExceptionDispatchInfo.Throw(stopFailure);
        }
    }
}
