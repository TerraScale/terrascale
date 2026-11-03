namespace TerraScale.Dashboard.Common.UI
{
    public sealed record ConfirmDialogModel(string Id, string Action, string Button, string Target,
        string CsrfToken, IReadOnlyDictionary<string, string> Fields, string? ConfirmationName = null, string? Description = null);
}
