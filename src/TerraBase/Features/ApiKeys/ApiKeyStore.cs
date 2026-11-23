using System.Buffers.Text;
using System.Security.Cryptography;
using System.Text;
using TerraBase.Engine;
using TerraBase.Features.Catalog;
using TerraBase.Features.ControlPlane;

namespace TerraBase.Features.ApiKeys;

public sealed record ApiKeyRecord(
    string Id, string Name, IReadOnlyList<string> Scopes, Ulid OrganizationId, Ulid? ProjectId, Ulid? DatabaseId, DateTimeOffset? ExpiresAt)
{
    public bool Grants(string scope) => Scopes.Contains(scope, StringComparer.Ordinal);

    public bool Reaches(ProjectRecord project, DatabaseRecord? database) =>
        project.OrganizationId == OrganizationId
        && (ProjectId is not { } projectId || projectId == project.Id)
        && (DatabaseId is not { } databaseId || (database is not null && databaseId == database.Id));
}

public sealed record IssuedApiKey(ApiKeyRecord Key, string Secret);

internal sealed class ApiKeyStore(SystemDatabase system, ControlSnapshots snapshots, TimeProvider time)
{
    public const string SecretPrefix = "tb_";

    public async ValueTask<Result<IssuedApiKey, Conflict, Unavailable>> CreateAsync(
        string name, IReadOnlyList<string> scopes, Ulid organizationId, Ulid? projectId, Ulid? databaseId, DateTimeOffset? expiresAt, CancellationToken cancellationToken)
    {
        var secret = string.Concat(SecretPrefix, Base64Url.EncodeToString(RandomNumberGenerator.GetBytes(32)));
        var hash = Hash(secret);
        var key = new ApiKeyRecord(Ulid.NewUlid(time.GetUtcNow()).ToString(), name, scopes, organizationId, projectId, databaseId, expiresAt);
        return await system.WriteAsync(
        [
            SqlBuild.Statement(
                "INSERT INTO api_keys (id, name, scopes, organization_id, project_id, database_id, expires_at, hash, created_at) VALUES (?, ?, ?, ?, ?, ?, ?, ?, ?)",
                SqlBuild.Text(key.Id), SqlBuild.Text(name), SqlBuild.Text(string.Join(' ', scopes)), SqlBuild.Id(organizationId),
                projectId is { } project ? SqlBuild.Id(project) : SqlValue.Null, databaseId is { } database ? SqlBuild.Id(database) : SqlValue.Null,
                SqlBuild.OptionalTime(expiresAt), SqlBuild.Text(hash), SqlBuild.Time(time.GetUtcNow())),
            SqlBuild.Statement("INSERT INTO api_key_hashes (hash, key_id) VALUES (?, ?)", SqlBuild.Text(hash), SqlBuild.Text(key.Id)),
        ], ConsistencyMode.Strong, cancellationToken) switch
        {
            SqlWriteResult => new IssuedApiKey(key, secret),
            Conflict conflict => conflict,
            Unavailable unavailable => unavailable,
        };
    }

    public async ValueTask<Result<Done, NotFound, Unavailable>> RevokeAsync(string? keyId, CancellationToken cancellationToken) =>
        keyId is null ? new NotFound("the API key does not exist", "unknown_api_key") : await system.WriteAsync(
        [
            SqlBuild.Statement("DELETE FROM api_key_hashes WHERE key_id = ?", SqlBuild.Text(keyId)),
            SqlBuild.Statement("DELETE FROM api_keys WHERE id = ?", SqlBuild.Text(keyId)),
        ], ConsistencyMode.Strong, cancellationToken) switch
        {
            SqlWriteResult { ResultSets: [_, { RowsAffected: > 0 }] } => new Done(),
            SqlWriteResult => new NotFound("the API key does not exist", "unknown_api_key"),
            Conflict conflict => new Unavailable(conflict.Message, conflict.Code),
            Unavailable unavailable => unavailable,
        };

    public async ValueTask<Result<ApiKeyRecord, NotFound, Unavailable>> FindAsync(string secret, CancellationToken cancellationToken)
    {
        if (!secret.StartsWith(SecretPrefix, StringComparison.Ordinal) || secret.Length > 256)
        {
            return new NotFound("unknown API key", "invalid_key");
        }

        var hash = Hash(secret);
        if (snapshots.FindApiKey(hash) is { } verifier)
        {
            return Usable(new ApiKeyRecord(verifier.KeyId, verifier.KeyId, verifier.Scopes, snapshots.OrganizationOf(verifier.ProjectId) ?? Ulid.Empty,
                verifier.ProjectId, verifier.DatabaseId, verifier.ExpiresAtUtc));
        }

        return await system.QueryAsync(SqlBuild.Statement(
            "SELECT k.id, k.name, k.scopes, k.organization_id, k.project_id, k.database_id, k.expires_at FROM api_key_hashes h JOIN api_keys k ON k.id = h.key_id WHERE h.hash = ?",
            SqlBuild.Text(hash)), cancellationToken) switch
        {
            IReadOnlyList<IReadOnlyList<SqlValue>> { Count: > 0 } rows => Usable(Read(rows[0])),
            IReadOnlyList<IReadOnlyList<SqlValue>> => new NotFound("unknown API key", "invalid_key"),
            Unavailable unavailable => unavailable,
        };
    }

    private Result<ApiKeyRecord, NotFound, Unavailable> Usable(ApiKeyRecord key) =>
        key.ExpiresAt is { } expires && expires <= time.GetUtcNow() ? new NotFound("the API key expired", "invalid_key") : key;

    private static ApiKeyRecord Read(IReadOnlyList<SqlValue> row) => new(
        row.TextAt(0), row.TextAt(1), row.TextAt(2).Split(' ', StringSplitOptions.RemoveEmptyEntries), row.IdAt(3),
        row.OptionalTextAt(4) is { } project && Ulid.TryParse(project, out var projectId) ? projectId : null,
        row.OptionalTextAt(5) is { } database && Ulid.TryParse(database, out var databaseId) ? databaseId : null,
        row.OptionalTimeAt(6));

    public static string Hash(string secret) => Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(secret)));
}
