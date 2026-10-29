using System.Net;
using Microsoft.Extensions.DependencyInjection;
using TerraBase.Features.Management.Usage;
using TerraBase.Features.Qos;
using TerraBase.Tenancy;
using TerraBase.Testing;

namespace TerraBase.Slow.Qos;

public sealed class PlanAllowanceTests
{
    [Test]
    public async Task Paid_plans_write_past_their_allowance_and_hobby_stops_at_it()
    {
        // The real plan allowances (pricing.json) scaled down 50,000 times: hobby 1, starter 5, team 100.
        await using var api = await DocumentApi.StartAsync(services: services => services.AddSingleton<IOperationAllowance>(provider =>
            new OperationAllowance(provider.GetRequiredService<UsageMeter>(), provider.GetRequiredService<ITenantDirectory>(),
                plan => QosPolicy.For(plan).MonthlyOperations / 50_000)));
        (await api.PutAsync(DocumentApi.NewId(), new { n = 1 })).Status.ShouldBe(HttpStatusCode.Created);
        await Assert.That((await api.PutAsync(DocumentApi.NewId(), new { n = 1 })).Code).IsEqualTo("operation_allowance");

        // Starter's 5 units are used up and its writes continue: ts-dashboard bills the units over the allowance.
        await api.Management.SetPlanAsync(api.Tenant.User, api.Tenant.OrganizationId, "starter");
        for (var i = 0; i < 6; i++)
        {
            (await api.PutAsync(DocumentApi.NewId(), new { n = i })).Status.ShouldBe(HttpStatusCode.Created);
        }

        await api.Management.SetPlanAsync(api.Tenant.User, api.Tenant.OrganizationId, "hobby");
        var refused = await api.PutAsync(DocumentApi.NewId(), new { n = 1 });
        await Assert.That((refused.Status, refused.Code)).IsEqualTo((HttpStatusCode.TooManyRequests, "operation_allowance"));
        await Assert.That(refused.Message).Contains("hobby plan allowance");
    }
}
