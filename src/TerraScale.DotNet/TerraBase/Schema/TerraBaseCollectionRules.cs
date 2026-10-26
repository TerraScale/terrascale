namespace TerraScale.DotNet.TerraBase;

/// <summary>Owner, authenticated-public and trusted-role grants for one document operation.</summary>
/// <param name="Owner">Grants access to documents owned by the signed subject.</param>
/// <param name="Public">Grants access to every authenticated end-user; never grants anonymous access.</param>
/// <param name="Roles">Case-sensitive trusted roles with collection-wide access.</param>
public sealed record TerraBaseCollectionAccessPolicy(bool Owner, bool Public, IReadOnlyList<string> Roles);

/// <summary>A collection's declarative end-user access policy.</summary>
/// <param name="Owner">JSON Pointer to the immutable owner subject, or null for policies without ownership.</param>
/// <param name="Read">Required read grants.</param>
/// <param name="Write">Required write grants.</param>
public sealed record TerraBaseCollectionAccessRules(string? Owner, TerraBaseCollectionAccessPolicy Read, TerraBaseCollectionAccessPolicy Write);

/// <summary>The collection rules response.</summary>
/// <param name="Rules">Current rules, or null before declaration. Missing rules deny every end-user operation.</param>
public sealed record TerraBaseCollectionRules(TerraBaseCollectionAccessRules? Rules);

public sealed partial class TerraBaseDatabase
{
    /// <summary>Reads collection rules. Requires project credentials with documents:read; end-user credentials are refused.</summary>
    public async Task<TerraBaseCollectionRules> GetCollectionRulesAsync(string collection, CancellationToken cancellationToken = default) =>
        (await connection.SendAsync(HttpMethod.Get, CollectionPath(collection) + "/rules", null,
            Json.TerraBaseCollectionRules, null, cancellationToken).ConfigureAwait(false)).Value;

    /// <summary>
    /// Replaces collection rules, at most 16 KiB. Requires project credentials with documents:write; end-user
    /// credentials are refused. Both grants and their roles arrays are required. All-false grants deny access.
    /// Owner grants require an owner pointer. End-users cannot transfer ownership or recreate another user's deleted id.
    /// Sent once, without automatic retries.
    /// </summary>
    public async Task<TerraBaseCollectionRules> SetCollectionRulesAsync(
        string collection, TerraBaseCollectionAccessRules rules, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ArgumentNullException.ThrowIfNull(rules);
        var body = TerraBaseConnection.Json(rules, Json.TerraBaseCollectionAccessRules);
        if (body.Headers.ContentLength > 16 * 1024)
        {
            body.Dispose();
            throw new ArgumentException("Collection rules must fit within 16 KiB of UTF-8 JSON.", nameof(rules));
        }
        return (await connection.SendAsync(HttpMethod.Put, CollectionPath(collection) + "/rules", body,
            Json.TerraBaseCollectionRules, null, cancellationToken).ConfigureAwait(false)).Value;
    }
}
