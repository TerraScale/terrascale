namespace TerraScale.Dashboard.Shared.UI;

public sealed record ConfirmDialogModel(string Id, string Action, string Button, string Target, string Description,
    string CsrfToken, IReadOnlyDictionary<string, string> Fields, string? ConfirmationName = null);
