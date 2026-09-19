namespace TerraScale.Dashboard2.Features.Backups;

public sealed record RestorePointRow(
    string GenerationId,
    string Generation,
    string Database,
    string Region,
    string Mode,
    string ModeTone,
    string EncryptionLabel,
    int FilesToRestore,
    string SizeLabel,
    string CreatedLabel);
