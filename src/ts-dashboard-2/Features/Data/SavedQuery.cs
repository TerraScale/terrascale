namespace TerraScale.Dashboard2;

public sealed record SavedQuery(
    string Id,
    string Name,
    string Query,
    string Revision,
    string UpdatedAt,
    string UpdatedByEmail);
