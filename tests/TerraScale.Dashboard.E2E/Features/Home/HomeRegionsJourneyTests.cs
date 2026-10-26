using System.Net;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using TerraScale.Dashboard.Features.Home;

namespace TerraScale.Dashboard.E2E.Features.Home;

[ClassDataSource<DashboardRootFixture>]
public sealed class HomeRegionsJourneyTests(DashboardRootFixture fixture)
{
    [Test]
    [Arguments(false)]
    [Arguments(true)]
    public async Task OverviewRemainsUsableWhileScopedPublicRegionsAreBlocked(bool unavailable)
    {
        var environment = new DemoEnvironment();
        var personas = await environment.InitializeReferenceAsync(fixture.Root);
        using var wire = new StatusWire(unavailable);
        await using var app = environment.CreateTestHost(fixture.Root, 0, "http://127.0.0.1", builder =>
        {
            wire.Fallback = (IHttpClientFactory)builder.Services.Last(service => service.ServiceType == typeof(IHttpClientFactory)).ImplementationInstance!;
            builder.Services.Replace(ServiceDescriptor.Singleton<IHttpClientFactory>(wire));
            builder.Services.Replace(ServiceDescriptor.Singleton<IHomeMeasurements, TerraBaseHomeMeasurements>());
        });
        await app.StartAsync();
        using var client = new HttpClient(new HttpClientHandler { ServerCertificateCustomValidationCallback = TestHttps.Validate, AllowAutoRedirect = false, CookieContainer = new() })
        { BaseAddress = new(app.Urls.Single().Replace("0.0.0.0", "127.0.0.1", StringComparison.Ordinal)) };
        using (var anonymous = await client.GetAsync("/overview/regions"))
            await Assert.That(anonymous.StatusCode).IsEqualTo(HttpStatusCode.Unauthorized);
        using var login = await HttpJourney.LoginAsync(client, personas.Seeded.Email, personas.Seeded.Password);
        await Assert.That(login.StatusCode).IsEqualTo(HttpStatusCode.SeeOther);
        using var identity = JsonDocument.Parse(await client.GetStringAsync("/api/v1/auth/me"));
        var scope = "?organizationId=" + identity.RootElement.GetProperty("organizationId").GetString() +
            "&projectId=" + identity.RootElement.GetProperty("projectId").GetString();
        using (var missing = await client.GetAsync("/overview/regions"))
            await Assert.That(missing.StatusCode).IsEqualTo(HttpStatusCode.BadRequest);
        using (var foreign = await client.GetAsync("/overview/regions?organizationId=" + AccountService.NewId() + "&projectId=" + AccountService.NewId()))
            await Assert.That(foreign.StatusCode).IsEqualTo(HttpStatusCode.NotFound);
        var fragment = client.GetStringAsync("/overview/regions" + scope);
        try
        {
            await wire.Started.Task.WaitAsync(TimeSpan.FromSeconds(5));
            var html = await client.GetStringAsync("/" + scope).WaitAsync(TimeSpan.FromSeconds(5));
            await Assert.That(html).Contains("id=\"home-activity\"");
            await Assert.That(WebUtility.HtmlDecode(html)).Contains("@get('/overview/regions" + scope + "')");
            await Assert.That(html).Contains("Loading region health and latency");
            await Assert.That(fragment.IsCompleted).IsFalse();
        }
        finally { wire.Release.TrySetResult(); }
        var patch = await fragment;
        await Assert.That(patch).Contains("event: datastar-patch-elements");
        await Assert.That(patch).Contains("id=\"home-regions\"");
        await Assert.That(patch).DoesNotContain("data-init");
        await Assert.That(patch).Contains(unavailable ? "Region measurements unavailable" : "12.50 ms");
        using var emptyClient = new HttpClient(new HttpClientHandler { ServerCertificateCustomValidationCallback = TestHttps.Validate, AllowAutoRedirect = false, CookieContainer = new() }) { BaseAddress = client.BaseAddress };
        using var emptyLogin = await HttpJourney.LoginAsync(emptyClient);
        var empty = environment.Workspace.Organizations.Values.Single(org => org.OwnerUserId == "test-user");
        var emptyPatch = await emptyClient.GetStringAsync("/overview/regions?organizationId=" + empty.Id + "&projectId=" + empty.Projects.Keys.Single());
        await Assert.That(emptyPatch).Contains("No database regions in this organization.");
        await Assert.That(emptyPatch).DoesNotContain("data-init");
        await app.StopAsync();
    }

    private sealed class StatusWire(bool unavailable) : HttpMessageHandler, IHttpClientFactory
    {
        public IHttpClientFactory Fallback { get; set; } = null!;
        public TaskCompletionSource Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public HttpClient CreateClient(string name) => name == "platform" ? new(this, false) : Fallback.CreateClient(name);
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            if (request.RequestUri!.Host != "status.terrascale.tech")
            { using var client = Fallback.CreateClient("platform"); return await HttpForwarding.SendAsync(client, request, cancellationToken); }
            Started.TrySetResult();
            await Release.Task.WaitAsync(cancellationToken);
            return new(unavailable ? HttpStatusCode.ServiceUnavailable : HttpStatusCode.OK)
            { Content = new StringContent("{\"regions\":[{\"code\":\"GRU\",\"status\":\"healthy\",\"consensus\":{\"samples\":1,\"median_ms\":12.5}}]}") };
        }
    }
}
