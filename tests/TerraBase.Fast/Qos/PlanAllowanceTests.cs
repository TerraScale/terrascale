using System.Net;
using Microsoft.Extensions.DependencyInjection;
using TerraBase.Fast.Documents;
using TerraBase.Features.Management.Usage;
using TerraBase.Features.Qos;
using TerraBase.Tenancy;

namespace TerraBase.Fast.Qos;

public sealed class PlanAllowanceTests
{
    [Test]
    public async Task Paid_plans_raise_the_allowance_and_hobby_restores_it()
    {
        // The real plan allowances (pricing.json) scaled down 50,000 times: hobby 1, starter 5, team 100.
        await using var api = await DocumentApi.StartAsync(services: services => services.AddSingleton<IOperationAllowance>(provider =>
            new OperationAllowance(provider.GetRequiredService<UsageMeter>(), provider.GetRequiredService<ITenantDirectory>(),
                plan => QosPolicy.For(plan).MonthlyOperations / 50_000)));
        (await api.PutAsync(DocumentApi.NewId(), new { n = 1 })).Status.ShouldBe(HttpStatusCode.Created);
        await Assert.That((await api.PutAsync(DocumentApi.NewId(), new { n = 1 })).Code).IsEqualTo("operation_allowance");

        await api.Management.SetPlanAsync(api.Tenant.User, api.Tenant.OrganizationId, "starter");
        (await api.PutAsync(DocumentApi.NewId(), new { n = 1 })).Status.ShouldBe(HttpStatusCode.Created);

        await api.Management.SetPlanAsync(api.Tenant.User, api.Tenant.OrganizationId, "team");
        (await api.PutAsync(DocumentApi.NewId(), new { n = 1 })).Status.ShouldBe(HttpStatusCode.Created);

        await api.Management.SetPlanAsync(api.Tenant.User, api.Tenant.OrganizationId, "hobby");
        var refused = await api.PutAsync(DocumentApi.NewId(), new { n = 1 });
        await Assert.That((refused.Status, refused.Code)).IsEqualTo((HttpStatusCode.TooManyRequests, "operation_allowance"));
        await Assert.That(refused.Message).Contains("hobby plan allowance");
    }
}
