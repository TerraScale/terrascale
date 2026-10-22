using System.Net;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Time.Testing;
using TerraBase.Features.Replication;
using TerraBase.Testing;

namespace TerraBase.Fast.Backup;

public sealed class BackupFixtureReadinessTests
{
    [Test]
    public async Task A_frozen_clock_fixture_retries_real_catalog_verification_after_the_startup_pass_is_cancelled(CancellationToken cancellationToken)
    {
        var s3 = new FakeS3
        {
            BeforeGet = (key, token) => key.StartsWith("catalog-anchor/", StringComparison.Ordinal)
                ? Task.Delay(Timeout.InfiniteTimeSpan, token)
                : Task.CompletedTask
        };
        var time = new FakeTimeProvider();
        var started = time.GetUtcNow();
        await using var api = await ManagementApi.StartAsync(s3.Configure, services =>
        {
            s3.Route(services);
            ManualBackupHosting.Configure(services);
        }, time, cancellationToken);
        var gate = api.Server.Services.GetRequiredService<ICatalogGate>();
        await Assert.That(gate.IsOpen).IsFalse();
        using var refused = await api.Client.PostAsJsonAsync(ManagementApi.Uri("/v1/management/organizations"), new { name = "before-verification" }, cancellationToken);
        await Assert.That(refused.StatusCode).IsEqualTo(HttpStatusCode.ServiceUnavailable);

        s3.BeforeGet = null;
        await api.VerifyCatalogAsync(cancellationToken);

        await Assert.That(gate.IsOpen).IsTrue();
        await Assert.That(time.GetUtcNow()).IsEqualTo(started);
        await Assert.That((await api.CreateOrganizationAsync("after-verification")).Length).IsEqualTo(26);
    }
}
