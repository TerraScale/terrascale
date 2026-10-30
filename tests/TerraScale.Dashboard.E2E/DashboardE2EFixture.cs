using System.Net;
using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.Extensions.DependencyInjection;
using TerraScale.Dashboard.Platform.Persistence;
using TerraScale.TerraBase.Contracts;
using TerraScale.TerraBase.Contracts.Documents;
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
            first = environment.CreateTestHost(Root, 0, "http://127.0.0.1");
            try
            {
                await first.StartAsync();
                Origin = Address(first);
                second = environment.CreateTestHost(Root, 0, Origin.ToString().TrimEnd('/'));
                await second.StartAsync();
                ReplicaOrigin = Address(second);
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

        // Test admission permits more projects than Free allows; it does not simulate a paid checkout.
        public T Service<T>() where T : notnull
        {
            return first!.Services.GetRequiredService<T>();
        }

        public async Task PlanAsync(string organizationId, string planId)
        {
            var state = first!.Services.GetRequiredService<IDashboardState>();
            var current = await state.GetAsync(AccountService.OrganizationsCollection, organizationId, default)
                ?? throw new InvalidOperationException("The fixture plan targets an existing organization");
            var organization = current.Document.Deserialize<OrganizationRecord>()!;
            organization.PlanId = planId;
            await state.PutAsync(AccountService.OrganizationsCollection, organizationId, JsonSerializer.SerializeToElement(organization),
                TerraBasePrecondition.Matches(current.Version), default);
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

    // Journeys that start their own hosts need the checkout path, not two extra application instances.
    public sealed class DashboardRootFixture
    {
        public string Root { get; } = DashboardE2EFixture.FindRoot();
    }
}
