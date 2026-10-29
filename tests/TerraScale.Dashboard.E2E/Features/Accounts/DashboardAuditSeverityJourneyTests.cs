using System.Text.RegularExpressions;
using TerraScale.Dashboard.Features.Organizations;

namespace TerraScale.Dashboard.E2E.Features.Accounts
{
    // A new dashboard audit event type must get a severity in DashboardAuditSeverity, or this
    // journey fails: it reads every action the application source records into AuditEvents.
    public sealed partial class DashboardAuditSeverityJourneyTests
    {
        [Test]
        public async Task EveryAuditEventTheDashboardRecordsHasAnExplicitSeverity()
        {
            var source = Path.Combine(FindRoot(), "src", "TerraScale.Dashboard");
            var recorded = Directory.EnumerateFiles(source, "*.cs", SearchOption.AllDirectories)
                .Where(path => !path.Contains(Path.DirectorySeparatorChar + "obj" + Path.DirectorySeparatorChar, StringComparison.Ordinal))
                .SelectMany(path => Statements(File.ReadAllText(path)))
                .SelectMany(statement => Action().Matches(statement).Select(match => match.Groups[1].Value))
                .ToHashSet(StringComparer.Ordinal);
            await Assert.That(recorded.Count).IsGreaterThanOrEqualTo(10).Because("the scan must find the dashboard's audit writes");
            foreach (var action in recorded)
            {
                await Assert.That(DashboardAuditSeverity.ByAction.ContainsKey(action)).IsTrue().Because(action + " is recorded without a catalog severity");
                var record = new AuditRecord("id", "actor@example.test", action, "target", "type", DateTimeOffset.UtcNow);
                await Assert.That(OrganizationFilters.AuditSeverity(record)).IsNotEqualTo("unknown").Because(action);
            }
            await Assert.That(DashboardAuditSeverity.ByAction.Keys.Except(recorded)).IsEmpty().Because("every catalog entry is still recorded somewhere");
            await Assert.That(DashboardAuditSeverity.ByAction.Values.All(value => value is "info" or "warning" or "critical")).IsTrue();
            // Another producer's missing severity stays Not reported.
            await Assert.That(OrganizationFilters.AuditSeverity(new("id", "actor", "organization.renamed", "target", "type", DateTimeOffset.UtcNow, Source: "ts-auth"))).IsEqualTo("unknown");
        }

        // Each `AuditEvents.Add(...)` statement, through its balancing parenthesis.
        private static IEnumerable<string> Statements(string text)
        {
            for (var start = text.IndexOf("AuditEvents.Add(", StringComparison.Ordinal); start >= 0; start = text.IndexOf("AuditEvents.Add(", start + 1, StringComparison.Ordinal))
            {
                var depth = 0;
                for (var index = start + "AuditEvents.Add".Length; index < text.Length; index++)
                {
                    if (text[index] == '(')
                    {
                        depth++;
                    }
                    else if (text[index] == ')' && --depth == 0) { yield return text[start..(index + 1)]; break; }
                }
            }
        }

        [GeneratedRegex("\"([a-z_]+(?:\\.[a-z_]+)+)\"")]
        private static partial Regex Action();

        private static string FindRoot()
        {
            for (var directory = new DirectoryInfo(AppContext.BaseDirectory); directory is not null; directory = directory.Parent)
            {
                if (File.Exists(Path.Combine(directory.FullName, "TerraScale.slnx")))
                {
                    return directory.FullName;
                }
            }

            throw new DirectoryNotFoundException("The E2E executable must run from the dashboard checkout.");
        }
    }
}
