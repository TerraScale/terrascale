using System.Net;
using Microsoft.Extensions.DependencyInjection;
using TerraBase.Fast.Documents;
using TerraBase.Features.Management.Usage;
using TerraBase.Tenancy;

namespace TerraBase.Fast.Qos;

public sealed class OperationAllowanceTests
{
    [Test]
    public async Task Writes_stop_at_the_monthly_operation_allowance_while_deletes_and_reads_remain()
    {
        await using var api = await DocumentApi.StartAsync(services: services => services.AddSingleton<IOperationAllowance>(provider =>
            new OperationAllowance(provider.GetRequiredService<UsageMeter>(), provider.GetRequiredService<ITenantDirectory>(), _ => 10)));
        var ids = Enumerable.Range(0, 11).Select(_ => DocumentApi.NewId()).ToArray();
        foreach (var id in ids[..10])
        {
            (await api.PutAsync(id, new { n = 1 })).Status.ShouldBe(HttpStatusCode.Created);
        }

        var refused = await api.PutAsync(ids[10], new { n = 1 });
        await Assert.That((refused.Status, refused.Code)).IsEqualTo((HttpStatusCode.TooManyRequests, "operation_allowance"));
        await Assert.That(refused.Message).Contains("upgrade the plan");

        (await api.SendAsync(HttpMethod.Delete, api.Document(ids[0]))).Status.ShouldBe(HttpStatusCode.OK);
        (await api.GetAsync(ids[1])).Status.ShouldBe(HttpStatusCode.OK);
        (await api.GetAsync(ids[10])).Status.ShouldBe(HttpStatusCode.NotFound);
    }
}
