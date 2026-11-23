using System.Collections.Concurrent;

namespace TerraScale.Platform.TerraBase;

public static class Scopes
{
    public const string SqlRead = "sql:read";
    public const string SqlWrite = "sql:write";

    public static IReadOnlyList<string> Read { get; } = [SqlRead];

    public static IReadOnlyList<string> ReadWrite { get; } = [SqlRead, SqlWrite];

    public static IReadOnlyList<string> Write { get; } = [SqlWrite];
}

public sealed record AccessContext(CallerGrant Grant, CallerCredential Credential)
{
    public Ulid Organization => Grant.OrganizationId;

    public Ulid Project => Grant.ProjectId;

    public bool IsAdministrator => !Grant.EndUser && !Grant.DatabaseRestricted;
}

public sealed record DatabaseAccess(AccessContext Access, Ulid Database, string DatabaseName);

public sealed class PlatformAccess(TerraBaseClient terraBase, OidcTokens tokens, PlatformOptions options, TimeProvider timeProvider)
{
    private const int CacheLimit = 16 * 1024;
    private static readonly TimeSpan GrantLifetime = TimeSpan.FromSeconds(15);
    private readonly ConcurrentDictionary<string, CachedGrant> _grants = new(StringComparer.Ordinal);

    public async ValueTask<Result<AccessContext, Unauthorized, Forbidden, NotFound, Unavailable>> ProjectAsync(
        HttpContext context,
        string project,
        IReadOnlyList<string> scopes,
        CancellationToken cancellationToken) =>
        await AuthorizeAsync(context.Request, new AuthorizeRequest(project, null, scopes), cancellationToken) switch
        {
            AccessContext access => access,
            Unauthorized unauthorized => unauthorized,
            Forbidden forbidden => forbidden,
            NotFound missing => missing,
            Unavailable unavailable => unavailable,
        };

    public async ValueTask<Result<DatabaseAccess, Unauthorized, Forbidden, NotFound, Unavailable>> DatabaseAsync(
        HttpContext context,
        string project,
        string database,
        IReadOnlyList<string> scopes,
        CancellationToken cancellationToken) =>
        await AuthorizeAsync(context.Request, new AuthorizeRequest(project, database, scopes), cancellationToken) switch
        {
            AccessContext { Grant: { DatabaseId: { } id, DatabaseName: { } name } } access => new DatabaseAccess(access, id, name),
            AccessContext => new NotFound("Database was not found in this project.", "database_not_found"),
            Unauthorized unauthorized => unauthorized,
            Forbidden forbidden => forbidden,
            NotFound missing => missing,
            Unavailable unavailable => unavailable,
        };

    public async ValueTask<Result<AccessContext, Unauthorized, Forbidden, NotFound, Unavailable>> AuthorizeAsync(
        HttpRequest request,
        AuthorizeRequest authorize,
        CancellationToken cancellationToken)
    {
        if (CallerCredential.From(request, options.PeerSecret) is not { } credential)
        {
            return new Unauthorized("a bearer token, API key or peer credential is required", "invalid_key");
        }

        var key = credential.CacheKey(authorize);
        var now = timeProvider.GetUtcNow();
        if (_grants.TryGetValue(key, out var cached) && cached.ExpiresAt > now)
        {
            return new AccessContext(cached.Grant, credential);
        }

        var screened = credential.BearerToken is { } token && credential.IsJwt
            ? await tokens.ValidateAsync(token, authorize.Project, cancellationToken)
            : new Done();
        return screened switch
        {
            Done => await GrantAsync(key, credential, authorize, now, cancellationToken),
            TokenRejected rejected => new Unauthorized(rejected.Message, "invalid_token"),
            Unavailable unavailable => unavailable,
        };
    }

    private async ValueTask<Result<AccessContext, Unauthorized, Forbidden, NotFound, Unavailable>> GrantAsync(
        string key,
        CallerCredential credential,
        AuthorizeRequest authorize,
        DateTimeOffset now,
        CancellationToken cancellationToken) =>
        await terraBase.AuthorizeAsync(credential, authorize, cancellationToken) switch
        {
            CallerGrant grant => Admit(key, grant, credential, authorize, now),
            Unauthorized unauthorized => unauthorized,
            Forbidden forbidden => forbidden,
            NotFound missing => missing,
            Unavailable unavailable => unavailable,
        };

    private Result<AccessContext, Unauthorized, Forbidden, NotFound, Unavailable> Admit(
        string key,
        CallerGrant grant,
        CallerCredential credential,
        AuthorizeRequest authorize,
        DateTimeOffset now)
    {
        if (grant.Anonymous)
        {
            return new Unauthorized("a credential is required", "invalid_key");
        }

        if (authorize.Scopes.FirstOrDefault(scope => !grant.Scopes.Contains(scope, StringComparer.Ordinal)) is { } missing)
        {
            return new Forbidden($"The API key does not grant {missing}.", "insufficient_scope");
        }

        if (_grants.Count >= CacheLimit)
        {
            _grants.Clear();
        }

        _grants[key] = new CachedGrant(grant, now + GrantLifetime);
        return new AccessContext(grant, credential);
    }

    private sealed record CachedGrant(CallerGrant Grant, DateTimeOffset ExpiresAt);
}
