using System.Net.Security;
using System.Security.Authentication;
using TerraBase.Security;

namespace TerraBase.Api.Tests;

public sealed class SecurityTests
{
    [Test]
    public async Task AuthenticatedTlsRequiresHttpsAndRestrictsProtocols()
    {
        await Assert.ThrowsAsync<ArgumentException>(() =>
            Task.Run(() => new AuthenticatedTlsOptions(new Uri("http://localhost"))));
        await Assert.ThrowsAsync<ArgumentException>(() =>
            Task.Run(() => new AuthenticatedTlsOptions(new Uri("https://localhost"), requireClientCertificate: true)));

        var options = new AuthenticatedTlsOptions(new Uri("https://localhost"));
        using var handler = options.CreateHandler();
        await Assert.That(handler.SslOptions.EnabledSslProtocols)
            .IsEqualTo(SslProtocols.Tls12 | SslProtocols.Tls13);
        await Assert.That(handler.SslOptions.RemoteCertificateValidationCallback is not null).IsTrue();
    }

    [Test]
    public async Task CapabilityChecksForwardExactTenantAndOperation()
    {
        var observed = new List<TenantAuthorizationRequest>();
        var hook = new RecordingHook(observed);
        var principal = new SecurityPrincipal("user-1");
        var key = new DocumentKey(new TenantId("tenant-a"), new CollectionId("items"), "doc-1");

        await CapabilityChecks.RequireAsync(CapabilityChecks.CheckDocumentReadAsync(hook, principal, key));
        await CapabilityChecks.RequireAsync(CapabilityChecks.CheckQueryAsync(hook, principal, key.Tenant, "query-1"));
        await CapabilityChecks.RequireAsync(CapabilityChecks.CheckSubscriptionAsync(hook, principal, key.Tenant, "subscription-1"));
        await CapabilityChecks.RequireAsync(CapabilityChecks.CheckBackupAsync(
            hook,
            principal,
            key.Tenant,
            TenantCapability.BackupWrite,
            "backup-1"));

        await Assert.That(observed.Select(request => request.Capability).SequenceEqual(new[]
        {
            TenantCapability.DocumentRead,
            TenantCapability.QueryExecute,
            TenantCapability.SubscriptionRead,
            TenantCapability.BackupWrite,
        })).IsTrue();
        await Assert.That(observed.All(request => request.Tenant == key.Tenant)).IsTrue();
        await Assert.That(observed.All(request => request.Principal == principal)).IsTrue();
    }

    [Test]
    public async Task CapabilityRequirementsFailClosedForDeniedAndExpiredDecisions()
    {
        var principal = new SecurityPrincipal("user-1");
        var tenant = new TenantId("tenant-a");
        var denied = new StaticHook(TenantAuthorizationDecision.Deny("membership revoked"));
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() =>
            CapabilityChecks.RequireAsync(CapabilityChecks.CheckQueryAsync(denied, principal, tenant)).AsTask());

        var expiredDecision = new TenantAuthorizationDecision(true, DateTimeOffset.UtcNow.AddMilliseconds(1));
        await Task.Delay(20);
        var expired = new StaticHook(expiredDecision);
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() =>
            CapabilityChecks.RequireAsync(CapabilityChecks.CheckQueryAsync(expired, principal, tenant)).AsTask());
    }

    [Test]
    public async Task KeyLeasesEnforceExactScopeBoundedLifetimeAndZeroization()
    {
        var scope = new KeyEncryptionScope(
            new TenantId("tenant-a"),
            "database-a",
            "key-version-1",
            "GRU",
            KeyEncryptionPurpose.Data);
        var key = Enumerable.Repeat((byte)7, 32).ToArray();
        await using var lease = new KeyEncryptionLease(scope, key, DateTimeOffset.UtcNow.AddMinutes(1));

        key[0] = 1;
        await Assert.That(lease.KeyMaterial.Length).IsEqualTo(32);
        await Assert.That(lease.KeyMaterial.Span[0]).IsEqualTo((byte)7);
        await Assert.That(lease.IsExpired(DateTimeOffset.UtcNow)).IsFalse();

        await lease.DisposeAsync();
        await Assert.ThrowsAsync<ObjectDisposedException>(() => Task.Run(() => _ = lease.KeyMaterial));
        await Assert.That(lease.IsExpired(DateTimeOffset.UtcNow)).IsTrue();
        await Assert.ThrowsAsync<ArgumentException>(() => Task.Run(() =>
            new KeyEncryptionLease(scope, new byte[31], DateTimeOffset.UtcNow.AddMinutes(1))));
    }

    private sealed class RecordingHook(List<TenantAuthorizationRequest> observed) : ITenantAuthorizationHook
    {
        public ValueTask<TenantAuthorizationDecision> AuthorizeAsync(
            TenantAuthorizationRequest request,
            CancellationToken cancellationToken = default)
        {
            observed.Add(request);
            return ValueTask.FromResult(new TenantAuthorizationDecision(
                true,
                DateTimeOffset.UtcNow.AddSeconds(1)));
        }
    }

    private sealed class StaticHook(TenantAuthorizationDecision decision) : ITenantAuthorizationHook
    {
        public ValueTask<TenantAuthorizationDecision> AuthorizeAsync(
            TenantAuthorizationRequest request,
            CancellationToken cancellationToken = default) => ValueTask.FromResult(decision);
    }
}
