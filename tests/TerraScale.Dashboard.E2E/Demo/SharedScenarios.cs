using System.Net;
using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.Extensions.DependencyInjection;
using TerraScale.Dashboard.E2E.Features.Organizations;
using TerraScale.Dashboard.Platform.Persistence;
using TUnit.Core.Interfaces;

namespace TerraScale.Dashboard.E2E
{
    public sealed class DashboardE2EFixture : IAsyncInitializer, IAsyncDisposable
    {
        private WebApplication? first;
        private WebApplication? second;
        public string Root { get; } = FindRoot();
        public Uri Origin { get; private set; } = null!;
        public Uri ReplicaOrigin { get; private set; } = null!;

        public async Task InitializeAsync()
        {
            var environment = new DemoEnvironment();
            await environment.InitializeAsync();
            first = await environment.CreateTestHostAsync(Root, 0, "http://127.0.0.1");
            try
            {
                await first.StartAsync();
                Origin = Address(first);
                second = await environment.CreateTestHostAsync(Root, 0, Origin.ToString().TrimEnd('/'));
                await second.StartAsync();
                ReplicaOrigin = Address(second);
                await Task.WhenAll(TestDeadline.StateInitializedAsync(first), TestDeadline.StateInitializedAsync(second));
                using var client = CreateClient();
                using var response = await client.GetAsync("/readyz");
                response.EnsureSuccessStatusCode();
            }
            catch { await DisposeAsync(); throw; }
        }

        public HttpClient CreateClient(bool replica = false)
        {
            return new(new HttpClientHandler
            {
                ServerCertificateCustomValidationCallback = TestHttps.Validate,
                AllowAutoRedirect = false,
                CookieContainer = new CookieContainer()
            })
            { BaseAddress = replica ? ReplicaOrigin : Origin, Timeout = TimeSpan.FromSeconds(30) };
        }

        public T Service<T>() where T : notnull
        {
            return first!.Services.GetRequiredService<T>();
        }

        public async Task PlanAsync(string organizationId, string planId)
        {
            var organizations = first!.Services.GetRequiredService<OrganizationStore>();
            var current = await organizations.FindAsync(organizationId, default)
                ?? throw new InvalidOperationException("The fixture plan targets an existing organization");
            current.Organization.PlanId = planId;
            await organizations.ReplaceAsync(current, default);
        }

        public async ValueTask DisposeAsync()
        {
            if (second is not null) { await second.StopAsync(); await second.DisposeAsync(); second = null; }
            if (first is not null) { await first.StopAsync(); await first.DisposeAsync(); first = null; }
        }

        private static Uri Address(WebApplication application)
        {
            return new(application.Services.GetRequiredService<IServer>()
            .Features.Get<IServerAddressesFeature>()!.Addresses.Single().Replace("0.0.0.0", "127.0.0.1", StringComparison.Ordinal));
        }

        internal static string FindRoot()
        {
            for (var directory = new DirectoryInfo(AppContext.BaseDirectory); directory is not null; directory = directory.Parent)
            {
                if (File.Exists(Path.Combine(directory.FullName, "TerraScale.slnx")))
                {
                    return directory.FullName;
                }
            }

            throw new DirectoryNotFoundException("The E2E executable must run from the dashboard checkout.");
        }
    }

    public sealed class DashboardRootFixture
    {
        public string Root { get; } = DashboardE2EFixture.FindRoot();
    }

    public sealed class SharedDatabaseSdk : IAsyncInitializer, IAsyncDisposable
    {
        public string Root { get; } = DashboardE2EFixture.FindRoot();

        public DatabaseSdkScenario Scenario { get; private set; } = null!;

        public async Task InitializeAsync()
        {
            Scenario = await DatabaseSdkScenario.StartAsync(Root);
        }

        public async ValueTask DisposeAsync()
        {
            if (Scenario is not null)
            {
                await Scenario.DisposeAsync();
            }
        }
    }

    public sealed class SharedDemoEnvironment : IAsyncInitializer, IDisposable
    {
        public string Root { get; } = DashboardE2EFixture.FindRoot();

        public DemoEnvironment Environment { get; } = new();

        public Task InitializeAsync()
        {
            return Environment.InitializeAsync();
        }

        public void Dispose()
        {
            Environment.Dispose();
        }
    }

    public sealed class SharedAuthProvider : IAsyncInitializer, IAsyncDisposable
    {
        public string Root { get; } = DashboardE2EFixture.FindRoot();

        public AuthProviderScenario Scenario { get; private set; } = null!;

        public async Task InitializeAsync()
        {
            Scenario = await AuthProviderScenario.StartAsync(Root);
        }

        public async ValueTask DisposeAsync()
        {
            if (Scenario is not null)
            {
                await Scenario.DisposeAsync();
            }
        }
    }

    public sealed class SharedTsAuthPlatform : IAsyncInitializer, IAsyncDisposable
    {
        internal TsAuthPlatformJourneyTests.PlatformScenario Scenario { get; private set; } = null!;

        public async Task InitializeAsync()
        {
            Scenario = await TsAuthPlatformJourneyTests.PlatformScenario.StartAsync();
        }

        public async ValueTask DisposeAsync()
        {
            if (Scenario is not null)
            {
                await Scenario.DisposeAsync();
            }
        }
    }
}
