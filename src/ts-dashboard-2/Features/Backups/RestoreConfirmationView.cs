namespace TerraScale.Dashboard2.Features.Backups;

/// <summary>Rendered confirmation plan shown between the Restore action and
/// the Execute step (mirrors the original Confirm dialogs' detail box).</summary>
public sealed record RestoreConfirmationView(
    string Token,
    string DatabaseName,
    string Generation,
    string Region,
    string Action,
    string CreatedLabel,
    string ExpiresLabel,
    string TtlLabel,
    string CutoffWarning);
