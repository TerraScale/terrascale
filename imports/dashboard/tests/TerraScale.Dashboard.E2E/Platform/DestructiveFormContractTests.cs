using System.Text.RegularExpressions;

namespace TerraScale.Dashboard.E2E.Platform;

public sealed class DestructiveFormContractTests
{
    [Test]
    public async Task DestructivePostFormsInAuditedFlowsRequireADialogAndSubmitOnce()
    {
        var root = Path.Combine(DashboardE2EFixture.FindRoot(), "src/TerraScale.Dashboard");
        var files = new[] { "Shared/UI/Slices/ConfirmDialog.cshtml", "Features/FileStorage/FileStorageView.cshtml",
            "Features/Backups/BackupSection.cshtml", "Features/Backups/WholeDatabaseRecoveryView.cshtml",
            "Features/Databases/DatabaseDetailView.cshtml", "Features/Logs/DatabaseLogView.cshtml", "Features/Webhooks/WebhooksView.cshtml" };
        var checkedForms = 0;
        foreach (var file in files)
        {
            var source = await File.ReadAllTextAsync(Path.Combine(root, file));
            var dialogDepth = 0;
            foreach (Match token in Regex.Matches(source, "<dialog\\b[^>]*>|</dialog>|<form\\b[^>]*>[\\s\\S]*?</form>"))
            {
                if (token.Value.StartsWith("<dialog", StringComparison.Ordinal)) { dialogDepth++; continue; }
                if (token.Value == "</dialog>") { dialogDepth--; continue; }
                var tag = token.Value[..(token.Value.IndexOf('>') + 1)];
                if (!tag.Contains("method=\"post\"", StringComparison.Ordinal)) continue;
                var destructive = Regex.IsMatch(tag, "action=\"[^\"]*(?:delete|remove|revoke)")
                    || token.Value.Contains("ts-btn-danger", StringComparison.Ordinal);
                if (destructive)
                {
                    await Assert.That(dialogDepth > 0).IsTrue().Because(file + ": destructive POST must require opening a confirmation dialog.");
                    await Assert.That(tag).Contains("data-submit-once").Because(file + ": destructive POST must suppress repeated submission.");
                    checkedForms++;
                }
                if (Regex.IsMatch(tag, "action=\"/api/v1/dashboard/(?:webhooks|backups(?:/restore)?|databases/branch)\""))
                    await Assert.That(tag).Contains("data-submit-once").Because(file + ": create and restore POST must suppress repeated submission.");
                if (tag.Contains("Confirm restore as a new database", StringComparison.Ordinal))
                    await Assert.That(tag).Contains("data-submit-once");
            }
        }
        await Assert.That(checkedForms >= 2).IsTrue().Because("Both the shared destructive dialog and existing delete-database form must be checked.");
    }
}
