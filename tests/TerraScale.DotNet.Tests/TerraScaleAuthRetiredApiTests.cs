namespace TerraScale.DotNet.Tests;

public sealed class TerraScaleAuthRetiredApiTests
{
    [Test]
    public async Task LegacyInstanceBillingHasNoPublicFactoryOrRemainingDtos()
    {
        await Assert.That(typeof(TerraScaleAuthClient).GetProperty("Billing")).IsNull();
        foreach (var name in new[]
        {
            "TerraScaleAuthBillingClient", "TerraScaleAuthBillingOverview", "TerraScaleAuthCheckout",
            "TerraScaleAuthBillingPortal", "TerraScaleAuthQuotaDecision", "TerraScaleAuthQuotaReconciliation",
            "TerraScaleAuthCreateCheckout", "TerraScaleAuthEnforceQuota"
        })
        {
            await Assert.That(typeof(TerraScaleAuthClient).Assembly.GetType("TerraScale.DotNet." + name)).IsNull();
        }
    }

    [Test]
    public async Task LegacyInstanceApiKeysHaveNoPublicFactoryOrRemainingDtos()
    {
        await Assert.That(typeof(TerraScaleAuthClient).GetProperty("ApiKeys")).IsNull();
        foreach (var name in new[]
        {
            "TerraScaleAuthApiKeysClient", "TerraScaleAuthApiKey", "TerraScaleAuthApiKeyPage",
            "TerraScaleAuthCreatedApiKey", "TerraScaleAuthCreateApiKey"
        })
        {
            await Assert.That(typeof(TerraScaleAuthClient).Assembly.GetType("TerraScale.DotNet." + name)).IsNull();
        }
    }
}
