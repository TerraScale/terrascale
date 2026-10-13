using System.Net;
using System.Security.Cryptography;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.Extensions.DependencyInjection;
using TerraScale.Authentication.Configuration;
using TerraScale.Authentication.Features.Identity;
using TerraScale.Authentication.Features.Platform;
using TerraScale.Authentication.Features.SigningKeys;
using TerraScale.Authentication.Store;

namespace TerraScale.Authentication.Tests;

/// <summary>
/// Runs the real ts-auth application on an ephemeral loopback Kestrel port and talks to it
/// over HTTP. Each instance has its own synthetic secrets; tests point its storage at their own database.
/// </summary>
public sealed class AuthTestServer : IAsyncDisposable
{
    /// <summary>Each test database seeds a synthetic mailbox; tests never use a real owner address.</summary>
    public const string BootstrapOwnerEmail = "platform-owner@example.invalid";

    private AuthTestServer(WebApplication app, Uri baseAddress, Uri metricsAddress)
    {
        this.App = app;
        this.BaseAddress = baseAddress;
        this.MetricsAddress = metricsAddress;
        this.Client = NewClient();
    }

    /// <summary>The running application; use only to resolve test doubles registered by the test.</summary>
    public WebApplication App { get; }

    /// <summary>The loopback origin the server listens on.</summary>
    public Uri BaseAddress { get; }

    /// <summary>The loopback origin of the metrics listener.</summary>
    public Uri MetricsAddress { get; }

    /// <summary>A client that never follows redirects and never stores cookies.</summary>
    public HttpClient Client { get; }

    /// <summary>Settings every test server starts from: Development mode with fresh synthetic secrets.</summary>
    public static Dictionary<string, string> DefaultSettings() => new(StringComparer.Ordinal)
    {
        ["listen"] = "http://127.0.0.1:0",
        ["metrics-listen"] = "http://127.0.0.1:0",
        ["public-origin"] = "https://auth.test.invalid",
        ["master-key"] = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32)),
        ["audit-signing-key"] = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32)),
        ["environment"] = "Development",
    };

    /// <summary>
    /// Starts a server. <paramref name="settings"/> edits the defaults, <paramref name="services"/>
    /// replaces infrastructure, <paramref name="routes"/> adds test-only routes that exist
    /// only inside this test process, <paramref name="configure"/> edits the host builder, and
    /// <paramref name="beforeStart"/> runs on the built services before the host starts (an operator step, say).
    /// </summary>
    public static async Task<AuthTestServer> StartAsync(
        Action<Dictionary<string, string>>? settings = null,
        Action<IServiceCollection>? services = null,
        Action<WebApplication>? routes = null,
        Action<WebApplicationBuilder>? configure = null,
        Func<IServiceProvider, Task>? beforeStart = null)
    {
        var values = DefaultSettings();
        settings?.Invoke(values);
        var app = TerraScale.Authentication.App.Build(AuthOptions.Load(values), builder =>
        {
            // Tests change keys behind the server's back; every lookup reads TerraBase unless a test opts in.
            builder.Services.AddSingleton(new KeyRingRefresh(TimeSpan.Zero));
            builder.Services.AddSingleton(provider => new PlatformBootstrap(provider.GetRequiredService<AuthStore>(),
                provider.GetRequiredService<Users>(), provider.GetRequiredService<PlatformDirectory>(), provider.GetRequiredService<TimeProvider>(), BootstrapOwnerEmail));
            services?.Invoke(builder.Services);
            configure?.Invoke(builder);
        });
        try
        {
            routes?.Invoke(app);
            if (beforeStart is not null)
            {
                await beforeStart(app.Services);
            }

            await app.StartAsync();
            // Bound in order: the public listener, then the metrics listener.
            var addresses = app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses
                .Select(address => new Uri(address.Replace("[::]", "127.0.0.1", StringComparison.Ordinal))).ToArray();
            return new AuthTestServer(app, addresses[0], addresses[1]);
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
