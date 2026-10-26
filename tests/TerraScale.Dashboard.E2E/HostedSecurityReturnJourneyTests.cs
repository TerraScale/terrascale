namespace TerraScale.Dashboard.E2E;

[ClassDataSource<DashboardE2EFixture>]
public sealed class HostedSecurityReturnJourneyTests(DashboardE2EFixture fixture)
{
    [Test]
    public async Task HostedSecurityLinksCarryTheExactFirstPartyAccountReturn()
    {
        using var client = fixture.CreateClient();
        using var login = await HttpJourney.LoginAsync(client);
        var html = await client.GetStringAsync("/profile?tab=security");
        var next = Uri.EscapeDataString("https://dash.terrascale.tech/profile?tab=security");
        await Assert.That(html).Contains("https://auth.terrascale.tech/ui/terrascale/reset?next=" + next);
        await Assert.That(html).Contains("https://auth.terrascale.tech/ui/terrascale/security?next=" + next);
    }

}
