using System.Net;
using Bogus;
using Microsoft.Extensions.DependencyInjection;
using TerraBase.Engine;
using TerraBase.Fast.Management;
using TerraBase.Testing;

namespace TerraBase.Fast.Documents;

public sealed class ChangeFeedRecoveryTests
{
    [Test]
    public async Task Query_budget_exhaustion_returns_capacity_and_releases_admission()
    {
        var clock = new SteppingClock();
        var hook = new UsageCommitHook();
        await using var api = await DocumentApi.StartAsync(TerraBaseTestServer.UseProductionQueryBudget, services: services =>
        {
            hook.Install(services);
            services.AddKeyedSingleton<TimeProvider>(EngineExtensions.QueryDeadlineClock, clock);
        });
        using var owner = api.Management.As(api.Tenant.User);
        await api.Management.VerifyProjectBindingAsync(api.Tenant.OrganizationId, api.Tenant.ProjectId, owner);
        using var feed = api.Management.WithToken(api.Management.Issuer.Mint(api.Tenant.User, token =>
        {
            token.Claims["project_id"] = api.Tenant.ProjectId;
            token.Claims["scope"] = "documents:read";
        }));
        var id = DocumentApi.NewId();
        (await api.PutAsync(id, new { name = new Faker().Name.FirstName() })).Status.ShouldBe(HttpStatusCode.Created);
        var route = $"/projects/{api.Tenant.ProjectId}/databases/db/collections/items/changes";
        var reserved = await api.Server.MetricAsync("terrabase_memory_reserved_bytes");
        var database = await api.TenantOfAsync();
        hook.BeforeBegin = (tenant, _) =>
        {
            clock.Armed |= tenant.DatabaseId == database.DatabaseId;
            return ValueTask.CompletedTask;
        };
        try
        {
            var refused = await DocumentApi.SendAsync(feed, HttpMethod.Get, route);
            await Assert.That((refused.Status, refused.Code)).IsEqualTo((HttpStatusCode.TooManyRequests, "change_feed_busy"));
        }
        finally
        {
            hook.BeforeBegin = null;
            clock.Armed = false;
        }
        await Assert.That(await api.Server.MetricAsync("terrabase_change_feed_active")).IsEqualTo(0);
        await Assert.That(await api.Server.MetricAsync("terrabase_memory_reserved_bytes")).IsEqualTo(reserved);
        var recovered = await DocumentApi.SendAsync(feed, HttpMethod.Get, route);
        await Assert.That(recovered.Status).IsEqualTo(HttpStatusCode.OK);
        await Assert.That(recovered.Json.GetProperty("changes").EnumerateArray().Single().GetProperty("id").GetString()).IsEqualTo(id);
    }

    [Test]
    public async Task Resume_token_survives_server_restart_with_retained_writes()
    {
        var directory = Directory.CreateTempSubdirectory("terrabase-feed-restart-").FullName;
        try
        {
            string route;
            string project;
            string user;
            string token;
            var id = DocumentApi.NewId();
            var name = new Faker().Name.FirstName();
            await using (var api = await DocumentApi.StartAsync(settings => settings["data-directory"] = directory))
            {
                route = $"/projects/{api.Tenant.ProjectId}/databases/db/collections/items/changes";
                project = api.Tenant.ProjectId;
                user = api.Tenant.User;
                using var owner = api.Management.As(user);
                await api.Management.VerifyProjectBindingAsync(api.Tenant.OrganizationId, project, owner);
                using var feed = api.Management.WithToken(api.Management.Issuer.Mint(user, jwt =>
                {
                    jwt.Claims["project_id"] = project;
                    jwt.Claims["scope"] = "documents:read";
                }));
                var start = await DocumentApi.SendAsync(feed, HttpMethod.Get, route);
                start.Status.ShouldBe(HttpStatusCode.OK);
                token = start.Json.GetProperty("resume_token").GetString()!;
                (await api.PutAsync(id, new { name })).Status.ShouldBe(HttpStatusCode.Created);
            }

            await using var restarted = await ManagementApi.StartAsync(settings => settings["data-directory"] = directory);
            using var client = restarted.WithToken(restarted.Issuer.Mint(user, jwt =>
            {
                jwt.Claims["project_id"] = project;
                jwt.Claims["scope"] = "documents:read";
            }));
            var page = await DocumentApi.SendAsync(client, HttpMethod.Get, $"{route}?resume_token={Uri.EscapeDataString(token)}");
            await Assert.That(page.Status).IsEqualTo(HttpStatusCode.OK);
            var change = page.Json.GetProperty("changes").EnumerateArray().Single();
            await Assert.That(change.GetProperty("id").GetString()).IsEqualTo(id);
            await Assert.That(change.GetProperty("document").GetProperty("name").GetString()).IsEqualTo(name);
            var next = page.Json.GetProperty("resume_token").GetString()!;
            var tail = await DocumentApi.SendAsync(client, HttpMethod.Get, $"{route}?resume_token={Uri.EscapeDataString(next)}");
            await Assert.That(tail.Status).IsEqualTo(HttpStatusCode.OK);
            await Assert.That(tail.Json.GetProperty("changes").GetArrayLength()).IsEqualTo(0);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }
}
