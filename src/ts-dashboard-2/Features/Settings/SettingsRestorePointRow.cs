namespace TerraScale.Dashboard2;

/// <summary>A completed backup generation of the selected database
/// (RestorePointView).</summary>
public sealed record SettingsRestorePointRow(
    string Name,
    string BackupId,
    string Mode,
    string ModeTone,
    string EncryptionLabel,
    int FilesToRestore,
    string CreatedLabel);
