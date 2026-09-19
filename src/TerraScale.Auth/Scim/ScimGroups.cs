namespace TerraScale.Auth.Scim;

using System.Net;
using System.Text.Json;
using TerraScale.DotNet;

/// <summary>A SCIM group provisioned through a directory.</summary>
internal sealed record ScimGroup
{
    public required string Id { get; init; }               // grp_...
    public required string InstanceId { get; init; }
    public required string DirectoryId { get; init; }
    public required string DisplayName { get; init; }
    public string[] MemberUserIds { get; init; } = [];
    public long CreatedAt { get; init; }
    public long UpdatedAt { get; init; }
}

/// <summary>
/// Group persistence over the raw TerraScale document API. The shared AuthStore
/// owns users and directories; groups exist only for SCIM, so they live here —
/// same document patterns the store uses, one dedicated collection.
/// </summary>
internal sealed class ScimGroups(TerraScaleClient client, string database)
{
    private const string Collection = "scim_groups";
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    public async Task<ScimGroup?> GetAsync(string id, CancellationToken ct = default)
    {
        try
        {
            var document = await client.Documents.GetAsync(database, Collection, id, cancellationToken: ct);
            return document.Value.Deserialize<ScimGroup>(Json);
        }
        catch (TerraScaleApiException exception) when (exception.StatusCode == HttpStatusCode.NotFound)
        {
            return null;
        }
    }

    public Task CreateAsync(ScimGroup group, CancellationToken ct = default) =>
        client.Documents.PutAsync(database, Collection, group.Id, group, new TerraScaleDocumentMutationOptions(CreateOnly: true), ct);

    public Task UpsertAsync(ScimGroup group, CancellationToken ct = default) =>
        client.Documents.PutAsync(database, Collection, group.Id, group, cancellationToken: ct);

    public Task DeleteAsync(string id, CancellationToken ct = default) =>
        client.Documents.DeleteAsync(database, Collection, id, ct);

    public async Task<IReadOnlyList<ScimGroup>> ListByDirectoryAsync(string directoryId, CancellationToken ct = default)
    {
        var groups = new List<ScimGroup>();
        string? after = null;
        while (true)
        {
            var result = await client.Documents.QueryAsync(
                database,
                Collection,
                new TerraScaleDocumentQuery(
                    Predicates: [new TerraScaleDocumentPredicate("directoryId", TerraScaleQueryOperator.Equal, JsonSerializer.SerializeToElement(directoryId))],
                    Projection: null,
                    Limit: 100,
                    After: after),
                cancellationToken: ct);
            groups.AddRange(result.Documents.Select(row => row.Value.Deserialize<ScimGroup>(Json)).OfType<ScimGroup>());
            if (!result.HasMore || result.NextAfter is null)
                return groups;
            after = result.NextAfter;
        }
    }
}
