using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using NUlid;
using TerraScale.DotNet.TerraBase;

namespace TerraScale.Dashboard.Platform.Persistence;

// The dashboard's own dogfood database, reached through TerraBase's private
// listener with the shared peer credential. No customer key or token ever reaches it.
public sealed class TerraBaseDashboardState(IHttpClientFactory http, IHostEnvironment environment) : IDashboardState
{
    // The only client that carries the peer credential (TerraBasePeerCredential); every other client uses "platform".
    public const string ClientName = "terrabase-state";

    // ts-orch supplies the private listener's origin (scheme, host, port).
    public const string OriginName = "TS_DASHBOARD_TERRABASE_URL";

    private readonly Uri origin = Origin(environment);

    // gru stays the single home until cross-region conditional writes are proven (integration plan G15).
    public static Uri Origin(IHostEnvironment environment) =>
        Origin(environment, Environment.GetEnvironmentVariable(OriginName));

    public static Uri Origin(IHostEnvironment environment, string? configured) =>
        configured is { Length: > 0 } ? Configured(configured)
        : environment.IsDevelopment() ? new("http://127.0.0.1:8081/")
        : throw new InvalidOperationException($"Set {OriginName}: only Development defaults to the loopback TerraBase.");

    // The peer credential goes to exactly this origin, so it is an origin and nothing more.
    private static Uri Configured(string value) =>
        Uri.TryCreate(value, UriKind.Absolute, out var uri) && uri.Scheme is "http" or "https" && uri.UserInfo.Length == 0 &&
        uri.AbsolutePath == "/" && uri.Query.Length == 0 && uri.Fragment.Length == 0
            ? uri
            : throw new InvalidOperationException($"{OriginName} must be an http(s) origin such as http://127.0.0.1:8081.");

    public async Task InitializeAsync(CancellationToken cancellationToken)
    {
        // The database is seeded by TerraBase; a missing one fails here instead of being created.
        var database = Database();
        foreach (var name in DashboardStateSchema.Collections)
        {
            TerraBaseCollection collection;
            try { collection = await database.GetCollectionAsync(name, cancellationToken); }
            catch (TerraBaseNotFoundException error) when (error.ErrorCode == "unknown_collection")
            {
                try { collection = await database.CreateCollectionAsync(name, cancellationToken); }
                catch (TerraBaseConflictException conflict) when (conflict.ErrorCode == "name_taken")
                {
                    collection = await database.GetCollectionAsync(name, cancellationToken);
                }
            }
            ValidateCollection(collection, name);
        }
    }

    public async Task CheckReadyAsync(CancellationToken cancellationToken)
    {
        var database = Database();
        await Task.WhenAll(DashboardStateSchema.Collections.Select(async name =>
            ValidateCollection(await database.GetCollectionAsync(name, cancellationToken), name)));
    }

    public async Task<TerraBaseDocument?> GetAsync(string collection, string id, CancellationToken cancellationToken)
    {
        var key = DocumentId(collection, id);
        var document = await Database().GetDocumentAsync(collection, key, cancellationToken);
        // Callers verify identity against their own key, so a mismatched reply stays mismatched.
        return document?.Id == key ? document with { Id = id } : document;
    }

    public async Task<IReadOnlyList<TerraBaseDocument>> ListAsync(string collection, CancellationToken cancellationToken)
    {
        var database = Database();
        var documents = new List<TerraBaseDocument>();
        string? cursor = null;
        do
        {
            var page = await database.ListDocumentsAsync(collection, 1000, cursor, cancellationToken);
            documents.AddRange(page.Items);
            cursor = page.NextCursor;
        } while (cursor is not null);
        return documents;
    }

    public Task<TerraBaseDocumentPage> QueryPageAsync(string collection, TerraBaseQuery query, CancellationToken cancellationToken) =>
        Database().QueryAsync(collection, query, cancellationToken);

    public async Task PutAsync(string collection, string id, JsonElement document, TerraBasePrecondition precondition, CancellationToken cancellationToken) =>
        await Database().PutDocumentAsync(collection, DocumentId(collection, id), document, precondition, cancellationToken);

    public async Task DeleteAsync(string collection, string id, string version, CancellationToken cancellationToken) =>
        await Database().DeleteDocumentAsync(collection, DocumentId(collection, id), TerraBasePrecondition.Matches(version), cancellationToken);

    public async Task DeleteListedAsync(string collection, TerraBaseDocument document, CancellationToken cancellationToken) =>
        await Database().DeleteDocumentAsync(collection, document.Id, TerraBasePrecondition.Matches(document.Version), cancellationToken);

    // TerraBase document ids are ULIDs. Callers key state by natural names (hashes, emails, Stripe ids, ULIDs), so
    // every key becomes a name-based ULID: the first 16 bytes of SHA-256 over "collection\0key". Nothing reads it back.
    public static string DocumentId(string collection, string key) =>
        new Ulid(SHA256.HashData(Encoding.UTF8.GetBytes(collection + "\0" + key)).AsSpan(0, 16).ToArray()).ToString();

    private TerraBaseDatabase Database() =>
        new TerraBaseDocumentsClient(http.CreateClient(ClientName), new TerraBaseDocumentsClientOptions { BaseAddress = origin })
            .Database(DashboardStateSchema.ProjectId, DashboardStateSchema.Database);

    private static void ValidateCollection(TerraBaseCollection collection, string name)
    {
        if (collection.Name != name) throw new IOException("Dashboard state collection identity is invalid.");
    }
}

// Attaches TERRABASE_PEER_SECRET only to requests for the state listener's origin and refuses any other destination.
// The primary handler follows no redirects (TerraBaseHttp), so a redirect cannot carry the credential elsewhere.
public sealed class TerraBasePeerCredential(IDashboardIdentity identity, IHostEnvironment environment) : DelegatingHandler
{
    public const string Header = "X-TerraBase-Peer-Credential";
    private readonly Uri origin = TerraBaseDashboardState.Origin(environment);

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        if (request.RequestUri is not { IsAbsoluteUri: true, UserInfo.Length: 0 } uri ||
            Uri.Compare(uri, origin, UriComponents.SchemeAndServer, UriFormat.UriEscaped, StringComparison.OrdinalIgnoreCase) != 0)
            throw new HttpRequestException("The TerraBase peer credential is sent only to the dashboard state listener.");
        var credentials = await identity.ReadAsync(cancellationToken);
        request.Headers.Remove(Header);
        request.Headers.Add(Header, credentials.TerraBasePeerSecret);
        RequestIdentity.Forward(request, credentials.TerraBasePeerSecret);
        return await base.SendAsync(request, cancellationToken);
    }
}
