using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using TerraScale.Dashboard.Features.Accounts;
using TerraScale.Dashboard.Features.Databases;
using TerraScale.Dashboard.Platform.Persistence;
using TerraScale.DotNet.TerraBase;

namespace TerraScale.Dashboard.Features.Data;

public interface IDocumentBackend
{
    Task<IReadOnlyList<TerraBaseCollection>> CollectionsAsync(DatabaseScope scope, DatabaseRegion home, string databaseName, CancellationToken cancellationToken);
    Task CreateCollectionAsync(DatabaseScope scope, DatabaseRegion home, string databaseName, string collection, CancellationToken cancellationToken);
    Task<TerraBaseDocumentPage> DocumentsAsync(DatabaseScope scope, DatabaseRegion home, string databaseName, string collection, TerraBaseQuery query, CancellationToken cancellationToken);
    Task<TerraBaseDocumentPage> ListAsync(DatabaseScope scope, DatabaseRegion home, string databaseName, string collection, int limit, string? cursor, CancellationToken cancellationToken);
    Task<TerraBaseDocument?> GetAsync(DatabaseScope scope, DatabaseRegion home, string databaseName, string collection, string id, CancellationToken cancellationToken);
    Task<TerraBaseWriteResult> WriteAsync(DatabaseScope scope, DatabaseRegion home, string databaseName, string collection, string operation, string id, JsonElement document, string version, CancellationToken cancellationToken);
    Task<TerraBaseCollectionSchema> SchemaAsync(DatabaseScope scope, DatabaseRegion home, string databaseName, string collection, CancellationToken cancellationToken);
}

public interface IDataCredentialRegistry
{
    Task<string?> CredentialIdAsync(DatabaseScope scope, DatabaseRegion home, CancellationToken cancellationToken);
}

// Customer data is accessed using a real, project-scoped key. The state API key
// never reaches customer databases, and credential secrets stay encrypted at rest.
public sealed class DocumentBackend(IDatabaseBackend databases, IDashboardState state, IDashboardIdentity identity, IHttpClientFactory http) : IDocumentBackend, IDataCredentialRegistry
{
    private const string CredentialsCollection = "dashboard_data_credentials";

    public async Task<IReadOnlyList<TerraBaseCollection>> CollectionsAsync(DatabaseScope scope, DatabaseRegion home, string databaseName, CancellationToken cancellationToken)
    {
        var database = await DatabaseAsync(scope, home, databaseName, cancellationToken);
        var result = new List<TerraBaseCollection>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        string? cursor = null;
        do
        {
            var page = await database.ListCollectionsAsync(200, cursor, cancellationToken);
            result.AddRange(page.Items);
            cursor = page.NextCursor;
            if (!string.IsNullOrEmpty(cursor) && !seen.Add(cursor)) throw new HttpRequestException("Collection pagination did not advance.");
        } while (!string.IsNullOrEmpty(cursor));
        return result;
    }

    public async Task CreateCollectionAsync(DatabaseScope scope, DatabaseRegion home, string databaseName, string collection, CancellationToken cancellationToken) =>
        await (await DatabaseAsync(scope, home, databaseName, cancellationToken)).CreateCollectionAsync(collection, cancellationToken);

    public async Task<TerraBaseDocumentPage> DocumentsAsync(DatabaseScope scope, DatabaseRegion home, string databaseName, string collection, TerraBaseQuery query, CancellationToken cancellationToken) =>
        await (await DatabaseAsync(scope, home, databaseName, cancellationToken)).QueryAsync(collection, query, cancellationToken);

    public async Task<TerraBaseDocumentPage> ListAsync(DatabaseScope scope, DatabaseRegion home, string databaseName, string collection, int limit, string? cursor, CancellationToken cancellationToken) =>
        await (await DatabaseAsync(scope, home, databaseName, cancellationToken)).ListDocumentsAsync(collection, limit, cursor, cancellationToken);

    public async Task<TerraBaseDocument?> GetAsync(DatabaseScope scope, DatabaseRegion home, string databaseName, string collection, string id, CancellationToken cancellationToken) =>
        await (await DatabaseAsync(scope, home, databaseName, cancellationToken)).GetDocumentAsync(collection, id, cancellationToken);

    public async Task<TerraBaseWriteResult> WriteAsync(DatabaseScope scope, DatabaseRegion home, string databaseName, string collection, string operation, string id, JsonElement document, string version, CancellationToken cancellationToken)
    {
        var database = await DatabaseAsync(scope, home, databaseName, cancellationToken);
        if (operation == "create") return id.Length == 0
            ? await database.CreateDocumentAsync(collection, document, cancellationToken)
            : await database.PutDocumentAsync(collection, id, document, TerraBasePrecondition.Absent, cancellationToken);
        var precondition = version.Length == 0 ? null : TerraBasePrecondition.Matches(version);
        return operation switch
        {
            "replace" => await database.PutDocumentAsync(collection, id, document, precondition, cancellationToken),
            "patch" => await database.PatchDocumentAsync(collection, id, document, precondition, cancellationToken),
            "delete" => await database.DeleteDocumentAsync(collection, id, precondition, cancellationToken),
            _ => throw new ArgumentException("Unknown document operation.", nameof(operation))
        };
    }

    public async Task<TerraBaseCollectionSchema> SchemaAsync(DatabaseScope scope, DatabaseRegion home, string databaseName, string collection, CancellationToken cancellationToken)
    {
        // The producer's schema routes require the signed-in user's project token.
        var client = new TerraBaseDocumentsClient(http.CreateClient("platform"), new TerraBaseDocumentsClientOptions
        {
            BaseAddress = home.Api,
            AccessTokenProvider = _ => ValueTask.FromResult(scope.AccessToken)
        });
        return await client.Database(scope.ProjectId, databaseName).GetSchemaAsync(collection, cancellationToken);
    }

    private async Task<TerraBaseDatabase> DatabaseAsync(DatabaseScope scope, DatabaseRegion home, string name, CancellationToken cancellationToken)
    {
        var key = await CredentialAsync(scope, home, cancellationToken);
        var client = new TerraBaseDocumentsClient(http.CreateClient("platform"), new TerraBaseDocumentsClientOptions { BaseAddress = home.Api, ApiKey = key.Secret });
        return client.Database(scope.ProjectId, name);
    }

    private async Task<DataCredential> CredentialAsync(DatabaseScope scope, DatabaseRegion home, CancellationToken cancellationToken)
    {
        var id = CredentialRecordId(scope, home);
        var machine = await identity.ReadAsync(cancellationToken);
        var existing = await state.GetAsync(CredentialsCollection, id, cancellationToken);
        if (existing is not null) return Open(AccountService.Read<EncryptedCredential>(existing), machine.SessionEncryptionKey, id);
        await databases.RegisterAsync(scope, home, cancellationToken);
        var issued = await databases.CreateCredentialAsync(scope, home, "ts-dashboard", ["documents:read", "documents:write"], null, cancellationToken);
        var value = new DataCredential(issued.Key.Id, issued.Secret);
        try
        {
            await state.PutAsync(CredentialsCollection, id, JsonSerializer.SerializeToElement(Seal(value, machine.SessionEncryptionKey, id)), TerraBasePrecondition.Absent, cancellationToken);
            return value;
        }
        catch (TerraBasePreconditionFailedException)
        {
            // Another replica recorded its key first; revoke our unused credential.
            using var cleanup = new CancellationTokenSource(TimeSpan.FromSeconds(30));
            await databases.RevokeCredentialAsync(scope, home, value.Id, cleanup.Token);
            var stored = await state.GetAsync(CredentialsCollection, id, cancellationToken) ?? throw new IOException("Document credential did not persist.");
            return Open(AccountService.Read<EncryptedCredential>(stored), machine.SessionEncryptionKey, id);
        }
    }

    public async Task<string?> CredentialIdAsync(DatabaseScope scope, DatabaseRegion home, CancellationToken cancellationToken)
    {
        var id = CredentialRecordId(scope, home);
        var stored = await state.GetAsync(CredentialsCollection, id, cancellationToken);
        if (stored is null) return null;
        var machine = await identity.ReadAsync(cancellationToken);
        return Open(AccountService.Read<EncryptedCredential>(stored), machine.SessionEncryptionKey, id).Id;
    }

    private static string CredentialRecordId(DatabaseScope scope, DatabaseRegion home) =>
        Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(scope.OrganizationId + "/" + scope.ProjectId + "/" + home.Id)));

    private static EncryptedCredential Seal(DataCredential value, string key, string id)
    {
        var plain = JsonSerializer.SerializeToUtf8Bytes(value);
        var nonce = RandomNumberGenerator.GetBytes(12);
        var cipher = new byte[plain.Length];
        var tag = new byte[16];
        using var aes = new AesGcm(Convert.FromBase64String(key), 16);
        try
        {
            aes.Encrypt(nonce, plain, cipher, tag, Encoding.UTF8.GetBytes("TerraScale.Dashboard/data-key/v1/" + id));
            return new(Convert.ToBase64String(nonce), Convert.ToBase64String(cipher), Convert.ToBase64String(tag));
        }
        finally { CryptographicOperations.ZeroMemory(plain); }
    }

    private static DataCredential Open(EncryptedCredential value, string key, string id)
    {
        var cipher = Convert.FromBase64String(value.Ciphertext);
        var plain = new byte[cipher.Length];
        using var aes = new AesGcm(Convert.FromBase64String(key), 16);
        try
        {
            aes.Decrypt(Convert.FromBase64String(value.Nonce), cipher, Convert.FromBase64String(value.Tag), plain, Encoding.UTF8.GetBytes("TerraScale.Dashboard/data-key/v1/" + id));
            return JsonSerializer.Deserialize<DataCredential>(plain) ?? throw new CryptographicException("Invalid document credential.");
        }
        finally { CryptographicOperations.ZeroMemory(plain); }
    }

    private sealed record EncryptedCredential(string Nonce, string Ciphertext, string Tag);
    private sealed record DataCredential(string Id, string Secret)
    {
        public override string ToString() => "Document credential [REDACTED]";
    }
}
