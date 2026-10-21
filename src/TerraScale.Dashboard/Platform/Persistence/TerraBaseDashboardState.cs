using System.Text.Json;
using TerraScale.DotNet.TerraBase;

namespace TerraScale.Dashboard.Platform.Persistence;

// The dashboard's own dogfood database, reached through TerraBase's private
// listener with the shared peer credential. No customer key or token ever reaches it.
public sealed class TerraBaseDashboardState(IHttpClientFactory http, IHostEnvironment environment) : IDashboardState
{
    // The only client that carries the peer credential (TerraBasePeerCredential); every other client uses "platform".
    public const string ClientName = "terrabase-state";

    // The private listener's origin (scheme, host, port) when it is not Fly's .internal one, in any environment.
    public const string OriginName = "TS_DASHBOARD_TERRABASE_URL";

    private readonly Uri origin = Origin(environment);

    // gru stays the single home until cross-region conditional writes are proven (integration plan G15).
    public static Uri Origin(IHostEnvironment environment) =>
        Origin(environment, Environment.GetEnvironmentVariable(OriginName), Environment.GetEnvironmentVariable("FLY_APP_NAME"));

    public static Uri Origin(IHostEnvironment environment, string? configured, string? flyApp) =>
        configured is { Length: > 0 } ? Configured(configured)
        : flyApp is not null ? new("http://gru.ts-terrabase.internal:8081/")
        : environment.IsDevelopment() ? new("http://127.0.0.1:8081/")
        : throw new InvalidOperationException($"Set {OriginName}: off Fly only Development defaults to the loopback TerraBase.");

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

    public Task<TerraBaseDocument?> GetAsync(string collection, string id, CancellationToken cancellationToken) =>
        Database().GetDocumentAsync(collection, id, cancellationToken);

    public async Task PutAsync(string collection, string id, JsonElement document, TerraBasePrecondition precondition, CancellationToken cancellationToken) =>
        await Database().PutDocumentAsync(collection, id, document, precondition, cancellationToken);

    public async Task DeleteAsync(string collection, string id, string version, CancellationToken cancellationToken) =>
        await Database().DeleteDocumentAsync(collection, id, TerraBasePrecondition.Matches(version), cancellationToken);

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
        return await base.SendAsync(request, cancellationToken);
    }
}
