using System.Collections.Concurrent;

namespace TerraScale.Platform.TerraBase;

public readonly union ResolveAccessOutcome(
    AccessContext,
    PlatformUnauthorized,
    PlatformForbidden,
    PlatformNotFound,
    PlatformUnavailable);

public readonly union DatabaseAccessOutcome(
    DatabaseAccess,
    PlatformUnauthorized,
    PlatformForbidden,
    PlatformNotFound,
    PlatformUnavailable);

public static class Scopes
{
    public const string SqlRead = "sql:read";
    public const string SqlWrite = "sql:write";

    public static IReadOnlyList<string> Read { get; } = [SqlRead];

    public static IReadOnlyList<string> ReadWrite { get; } = [SqlRead, SqlWrite];

    public static IReadOnlyList<string> Write { get; } = [SqlWrite];
}

public sealed record AccessContext(CallerGrant Grant, CallerCredential Credential, EndUserToken? Token)
{
    public Ulid Organization => Grant.OrganizationId;

    public Ulid Project => Grant.ProjectId;

    public bool IsAdministrator => !Grant.EndUser && !Grant.DatabaseRestricted;

    public bool Guest => Grant.Anonymous || Token is { Guest: true };
}

public sealed record DatabaseAccess(AccessContext Access, Ulid Database, string DatabaseName);

public sealed class PlatformAccess(TerraBaseClient terraBase, OidcTokens tokens, PlatformOptions options, TimeProvider timeProvider)
{
    private const int CacheLimit = 16 * 1024;
    private static readonly TimeSpan GrantLifetime = TimeSpan.FromSeconds(15);
    private readonly ConcurrentDictionary<string, CachedGrant> _grants = new(StringComparer.Ordinal);

    public ValueTask<ResolveAccessOutcome> ProjectAsync(
        HttpContext context,
        string project,
        IReadOnlyList<string> scopes,
        CancellationToken cancellationToken) =>
        AuthorizeAsync(context.Request, new AuthorizeRequest(project, null, scopes), cancellationToken);

    public async ValueTask<DatabaseAccessOutcome> DatabaseAsync(
        HttpContext context,
        string project,
        string database,
        IReadOnlyList<string> scopes,
        CancellationToken cancellationToken)
    {
        var outcome = await AuthorizeAsync(context.Request, new AuthorizeRequest(project, database, scopes), cancellationToken);
        return outcome switch
        {
            AccessContext { Grant: { DatabaseId: { } id, DatabaseName: { } name } } access => new DatabaseAccess(access, id, name),
            AccessContext => new PlatformNotFound("Database was not found in this project.", "database_not_found"),
            PlatformUnauthorized unauthorized => unauthorized,
            PlatformForbidden forbidden => forbidden,
            PlatformNotFound missing => missing,
            PlatformUnavailable unavailable => unavailable,
        };
    }

    public async ValueTask<ResolveAccessOutcome> AuthorizeAsync(
        HttpRequest request,
        AuthorizeRequest authorize,
        CancellationToken cancellationToken)
    {
        if (CallerCredential.From(request, options.PeerSecret) is not { } credential)
        {
            return new PlatformUnauthorized("a bearer token, API key or peer credential is required", "invalid_key");
        }

        var key = credential.CacheKey(authorize);
        var now = timeProvider.GetUtcNow();
        if (_grants.TryGetValue(key, out var cached) && cached.ExpiresAt > now)
        {
            return new AccessContext(cached.Grant, credential, cached.Token);
        }

        var screened = credential.BearerToken is { } token && credential.IsJwt
            ? await tokens.ValidateAsync(token, authorize.Project, cancellationToken)
            : new TokenAccepted();
        return screened switch
        {
            TokenAccepted => await GrantAsync(key, credential, authorize, now, cancellationToken),
            TokenRejected rejected => new PlatformUnauthorized(rejected.Message, "invalid_token"),
            PlatformUnavailable unavailable => unavailable,
        };
    }

    private async ValueTask<ResolveAccessOutcome> GrantAsync(
        string key,
        CallerCredential credential,
        AuthorizeRequest authorize,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        var outcome = await terraBase.AuthorizeAsync(credential, authorize, cancellationToken);
        return outcome switch
        {
            CallerGrant grant => Admit(key, grant, credential, authorize, now) switch
            {
                AccessContext access => access,
                PlatformUnauthorized unauthorized => unauthorized,
                PlatformForbidden forbidden => forbidden,
            },
            PlatformUnauthorized unauthorized => unauthorized,
            PlatformForbidden forbidden => forbidden,
            PlatformNotFound missing => missing,
            PlatformUnavailable unavailable => unavailable,
        };
    }

    private readonly union AdmitCallerOutcome(AccessContext, PlatformUnauthorized, PlatformForbidden);

    private AdmitCallerOutcome Admit(
        string key,
        CallerGrant grant,
        CallerCredential credential,
        AuthorizeRequest authorize,
        DateTimeOffset now)
    {
        var token = grant.EndUser ? EndUserToken.Read(credential) : null;
        if (grant.EndUser && token is null)
        {
            return new PlatformUnauthorized("the end-user token's claims are unreadable", "invalid_token");
        }

        if (grant.Anonymous && !IsGuest(grant, credential, token))
        {
            return new PlatformUnauthorized("a credential is required", "invalid_key");
        }

        if (authorize.Scopes.FirstOrDefault(scope => !grant.Scopes.Contains(scope, StringComparer.Ordinal)) is { } missing)
        {
            return new PlatformForbidden($"The API key does not grant {missing}.", "insufficient_scope");
        }

        if (_grants.Count >= CacheLimit)
        {
            _grants.Clear();
        }

        _grants[key] = new CachedGrant(grant, token, now + GrantLifetime);
        return new AccessContext(grant, credential, token);
    }

    private static bool IsGuest(CallerGrant grant, CallerCredential credential, EndUserToken? token) =>
        grant is { EndUser: true, Subject.Length: > 0 } && grant.ProjectId != Ulid.Empty && credential is { Peer: false, IsJwt: true } && token is { Guest: true };

    private sealed record CachedGrant(CallerGrant Grant, EndUserToken? Token, DateTimeOffset ExpiresAt);
}
