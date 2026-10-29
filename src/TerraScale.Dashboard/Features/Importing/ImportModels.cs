using TerraScale.DotNet.TerraBase;
using TerraScale.Dashboard.Common.UI;

namespace TerraScale.Dashboard.Features.Importing
{
    // What the confirmation step discloses: every way the destination will differ from the source.
    public sealed record ImportDifferences(IReadOnlyList<string> TypeChanges, IReadOnlyList<string> Constraints, IReadOnlyList<string> Excluded,
        IReadOnlyList<string> GeneratedIds, IReadOnlyList<string> Warnings)
    {
        public bool Any => TypeChanges.Count + Constraints.Count + Excluded.Count + GeneratedIds.Count + Warnings.Count > 0;
    }

    // NewDatabase is minted once per page render, so a resubmitted form reuses the destination it created.
    public sealed record ImportsPage(ShellModel Shell, IReadOnlyList<TerraBaseImportJob> Jobs, string NewDatabase, string Unavailable = "", string Error = "");
    public sealed record ImportJobPage(ShellModel Shell, TerraBaseImportJob Job, string Error = "")
    {
        public string State => ImportService.Wire(Job.State);
        public string? DatabaseId => Shell.Context.Project?.Databases.Values.FirstOrDefault(database => database.Name == Job.Database)?.Id;
    }
}
