using TerraScale.DotNet.Qualification;
using TUnit.Core;

namespace TerraScale.DotNet.Tests.Native;

public sealed class NativePublicBoundaryTests
{
    [Test]
    public Task PackagedContractsAndActualEffectVectorsMatch() => NativeChecks.ContractResourcesAsync();

    [Test]
    public Task CallerAndOwnedTransportLifetimesRemainDistinct() => NativeChecks.TransportOwnershipAsync();

    [Test]
    // The public journey has short deadlines; unrelated worker stress tests must not consume its budget.
    [NotInParallel]
    public Task PublicNativeClientAndDiscoveryComposeWithoutPrivateCliTypes() => NativeChecks.PublicNativeJourneyAsync();

    [Test]
    public Task PublicDirectOriginsKeepNativeAndAuthorityBoundaries() => NativeChecks.DirectOriginsAsync();

    [Test]
    public Task PublicAuthDiagnosticsNeverExposeCredentials() => NativeChecks.AuthDiagnosticsAsync();
}
