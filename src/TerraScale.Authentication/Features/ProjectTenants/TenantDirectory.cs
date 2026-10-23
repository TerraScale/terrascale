using System.Text.Json;
using System.Text.Json.Nodes;
using TerraScale.Authentication.Contracts;
using TerraScale.Authentication.Features.Identity;
using TerraScale.Authentication.Features.Platform;
using TerraScale.Authentication.Store;
using TerraScale.DotNet.TerraBase;

namespace TerraScale.Authentication.Features.ProjectTenants;

/// <summary>A live session of a project tenant.</summary>
/// <param name="Id">Its TerraBase document id; never the token or its hash.</param>
/// <param name="Session">The session.</param>
/// <param name="Email">Its user's email.</param>
public sealed record ProjectSession(string Id, SessionRecord Session, string Email);

/// <summary>
/// A project tenant's end users and sessions, managed by the project's platform members. Every record
/// lives in the project's own instance (its id), so no call reaches another tenant's data.
/// </summary>
public sealed class TenantDirectory(AuthStore store, Users users, Sessions sessions, PlatformDirectory platform, TimeProvider clock)
{
    // Bounds the pages a filtered list reads for one answer; a sparse match answers fewer items and a cursor.
    private const int MaxRounds = 10;

    /// <summary>Checks that <paramref name="userId"/> holds at least <paramref name="minimum"/> on the project: 404 when it holds none, 403 when lower.</summary>
    public async Task CheckAccessAsync(string userId, string projectId, string minimum, CancellationToken cancellationToken)
    {
        var project = await platform.ProjectAsync(projectId, cancellationToken).ConfigureAwait(false) ?? throw PlatformDirectory.NotFound();
        PlatformDirectory.RequireRole(await platform.ProjectAccessAsync(userId, project.Value.OrganizationId, projectId, cancellationToken).ConfigureAwait(false), minimum);
    }

    /// <summary>A page of the tenant's users whose email contains <paramref name="query"/> (any when null).</summary>
    public Task<Page<UserRecord>> UsersAsync(string projectId, string? query, int limit, string? after, CancellationToken cancellationToken)
    {
        var needle = string.IsNullOrWhiteSpace(query) ? null : query.Trim().ToLowerInvariant();
        return this.ScanAsync(AuthStore.UserCollection, projectId, [], limit, after, document => Task.FromResult(
            document.Document.Deserialize(IdentityJson.Default.UserRecord) is { } user && user.InstanceId == projectId
            && (needle is null || user.Email.Contains(needle, StringComparison.Ordinal)) ? user : null), cancellationToken);
    }

    /// <summary>The tenant's user, or 404.</summary>
    public async Task<UserRecord> UserAsync(string projectId, string userId, CancellationToken cancellationToken) =>
        await users.GetAsync(projectId, userId, cancellationToken).ConfigureAwait(false) ?? throw PlatformDirectory.NotFound();

    /// <summary>Disables or enables the tenant's user, or 404.</summary>
    public async Task<UserRecord> SetDisabledAsync(string projectId, string userId, bool disabled, CancellationToken cancellationToken) =>
        await users.SetDisabledAsync(projectId, userId, disabled, cancellationToken).ConfigureAwait(false) ?? throw PlatformDirectory.NotFound();

    /// <summary>A page of the tenant's live sessions (unrevoked, unexpired, of an enabled user at its current epoch), with each document id.</summary>
    public Task<Page<ProjectSession>> SessionsAsync(string projectId, string? userId, int limit, string? after, CancellationToken cancellationToken)
    {
        var now = JsonSerializer.SerializeToNode(clock.GetUtcNow(), IdentityJson.Default.DateTimeOffset);
        List<JsonObject> conditions = [new() { ["eq"] = new JsonArray("/revoked", false) }, new() { ["gt"] = new JsonArray("/expiresAt", now) }];
        if (userId is not null)
        {
            conditions.Add(PlatformDirectory.Equal("/userId", userId));
        }

        var owners = new Dictionary<string, UserRecord?>(StringComparer.Ordinal);
        return this.ScanAsync(AuthStore.SessionCollection, projectId, conditions, limit, after, async document =>
        {
            if (document.Document.Deserialize(IdentityJson.Default.SessionRecord) is not { } session || session.InstanceId != projectId)
            {
                return null;
            }

            if (!owners.TryGetValue(session.UserId, out var user))
            {
                owners[session.UserId] = user = await users.GetAsync(projectId, session.UserId, cancellationToken).ConfigureAwait(false);
            }

            return user is { Disabled: false } && user.AuthEpoch == session.AuthEpoch && user.AuthStamp == session.AuthStamp
                ? new ProjectSession(document.Id, session, user.Email) : null;
        }, cancellationToken);
    }

    /// <summary>Revokes the tenant's session with document id <paramref name="id"/>; false when there is none.</summary>
    public async Task<bool> RevokeSessionAsync(string projectId, string id, CancellationToken cancellationToken)
    {
        var filter = PlatformDirectory.Filter("and", new JsonArray(PlatformDirectory.Equal("/instanceId", projectId), PlatformDirectory.Equal("$id", id)));
        return await store.QueryAsync(AuthStore.SessionCollection, filter, 1, cancellationToken).ConfigureAwait(false) is [var document]
            && document.Document.Deserialize(IdentityJson.Default.SessionRecord) is { } session && session.InstanceId == projectId
            && await sessions.RevokeStoredAsync(projectId, session.TokenHash, cancellationToken).ConfigureAwait(false);
    }

    // Reads pages of the instance's matching documents in id order, each exactly as large as the items still missing,
    // so the answered cursor never skips an unread document.
    private async Task<Page<T>> ScanAsync<T>(string collection, string projectId, IReadOnlyList<JsonObject> conditions, int limit, string? after, Func<TerraBaseDocument, Task<T?>> select, CancellationToken cancellationToken)
        where T : class
    {
        List<T> items = [];
        var cursor = after;
        try
        {
            for (var round = 0; round < MaxRounds && items.Count < limit; round++)
            {
                var page = await store.PageAsync(collection, projectId, conditions, [], limit - items.Count, cursor, cancellationToken: cancellationToken).ConfigureAwait(false);
                foreach (var document in page.Items)
                {
                    if (await select(document).ConfigureAwait(false) is { } item)
                    {
                        items.Add(item);
                    }
                }

                if ((cursor = page.NextCursor) is null)
                {
                    break;
                }
            }
        }
        catch (TerraBaseException error) when (error.ErrorCode == "invalid_cursor")
        {
            throw new ApiException(StatusCodes.Status400BadRequest, "invalid_request", "after is not a cursor of this list.");
        }

        return new(items, cursor);
    }
}
