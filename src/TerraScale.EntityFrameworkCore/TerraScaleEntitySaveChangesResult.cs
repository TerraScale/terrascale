namespace TerraScale.EntityFrameworkCore;
/// <summary>Summary of tracked changes sent to TerraScale.</summary>
public sealed record TerraScaleEntitySaveChangesResult(int Created, int Upserted, int Deleted)
{
    /// <summary>Total document operations performed.</summary>
    public int Total => Created + Upserted + Deleted;
}
