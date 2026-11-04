using TerraScale.Dashboard.Common.UI;

namespace TerraScale.Dashboard.Features.Data
{
    public sealed record DataPage(ShellModel Shell, DatabaseRecord Database, string Error = "", string FormError = "", string Dialog = "",
        string Sql = "", DatabaseSqlResult? SqlResult = null, IReadOnlyList<SavedQuery>? SavedQueries = null, string QueryName = "",
        SqlCatalog? Catalog = null, IReadOnlyList<string>? History = null, bool SqlChecked = false, string CatalogError = "", string SqlNotice = "")
    {
        public AccountContext Context => Shell.Context;
        public SqlAnalysis Analysis => SqlValidation.Analyze(Sql);
    }

    public sealed record QueryDeleteModel(DataPage Page, SavedQuery Query);
}
