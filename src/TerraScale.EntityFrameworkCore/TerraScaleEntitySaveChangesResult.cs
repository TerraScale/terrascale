namespace TerraScale.EntityFrameworkCore;

/// <summary>
/// Summary of EF Core tracked changes persisted to TerraScale.
/// </summary>
/// <param name="Created">Number of documents created in TerraScale.</param>
/// <param name="Upserted">Number of documents upserted in TerraScale.</param>
/// <param name="Deleted">Number of documents deleted in TerraScale.</param>
public sealed record TerraScaleEntitySaveChangesResult(
    int Created,
    int Upserted,
    int Deleted)
{
    /// <summary>
    /// Total number of TerraScale document operations performed.
    /// </summary>
    public int Total => Created + Upserted + Deleted;
}
