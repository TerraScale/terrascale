namespace TerraScale.Dashboard.E2E.PagePaymentAudit.Oct13.AccountsMembersSettings;

[Explicit]
[ClassDataSource<AccountsAuditScenario>(Shared = SharedType.PerClass)]
[NotInParallel("dashboard-demo")]
public sealed class AccountsAuditPreviewJourneyTests(AccountsAuditScenario scenario)
{
    [Test]
    public async Task HoldSyntheticHostForCollaborativeBrowserEvidence()
    {
        Console.WriteLine("ACCOUNTS_AUDIT_ORIGIN=" + scenario.Origin);
        await scenario.Finished.Task.WaitAsync(TimeSpan.FromMinutes(15));
    }
}
