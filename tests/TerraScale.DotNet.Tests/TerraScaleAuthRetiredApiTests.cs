using System.Net;
using System.Text;

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

    [Test]
    public async Task LegacyInstanceOrganizationsHaveNoPublicFactoryOrExclusiveDtos()
    {
        await Assert.That(typeof(TerraScaleAuthClient).GetProperty("Organizations")).IsNull();
        foreach (var name in new[]
        {
            "TerraScaleAuthOrganizationsClient", "TerraScaleAuthMembership", "TerraScaleAuthMembershipUser",
            "TerraScaleAuthInvitation", "TerraScaleAuthOrgDomain", "TerraScaleAuthDomainEnrollment",
            "TerraScaleAuthCreateOrganization", "TerraScaleAuthRenameOrganization", "TerraScaleAuthCreateMembership",
            "TerraScaleAuthUpdateMembership", "TerraScaleAuthCreateInvitation", "TerraScaleAuthAddDomain",
            "TerraScaleAuthEnrollDomainUser"
        })
        {
            await Assert.That(typeof(TerraScaleAuthClient).Assembly.GetType("TerraScale.DotNet." + name)).IsNull();
        }
    }

    [Test]
    public async Task RetainedLegacyUserMembershipStillReadsItsEmbeddedOrganization()
    {
        using var handler = new MembershipHandler();
        using var http = new HttpClient(handler);
        var client = new TerraScaleAuthClient(http, new()
        {
            BaseAddress = new Uri("https://auth.example.test"),
            InstanceSlug = "terrascale",
            ApiKey = "fixture-management"
        });
        var memberships = await client.Users.ListOrganizationsAsync("fixture-user");
        await Assert.That(memberships[0].Organization).IsEqualTo(new TerraScaleAuthOrganization("fixture-org", "Fixture", "fixture", 7));
    }

    private sealed class MembershipHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("""[{"id":"fixture-membership","role":"member","created_at":8,"organization":{"id":"fixture-org","name":"Fixture","slug":"fixture","created_at":7}}]""", Encoding.UTF8, "application/json")
            });
    }
}
