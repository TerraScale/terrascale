using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using TerraScale.Dashboard.Features.Auth;
using TerraScale.Dashboard.Platform.Persistence;

namespace TerraScale.Dashboard.Features.Backups;

// ts-dotnet has no backup wrappers yet. These call TerraBase's published project backup routes,
// /v1/projects/{project}/databases/{name}/backups..., with the same current-user, action-scoped project
// token as the SDK calls (databases:read to read, databases:write to change). The legacy organization
// aliases with the session token answered 404 for dashboard-created databases.
// Restore is confirmed by reading the new database back before dashboard state is updated.
public sealed class BackupBackend(IHttpClientFactory factory, IProjectAccessTokens tokens, IDatabaseBackend databases) : IBackupBackend
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    { RespectRequiredConstructorParameters = true, RespectNullableAnnotations = true };

    public async Task<IReadOnlyList<ProducerBackup>> ListAsync(DatabaseScope scope, DatabaseRegion home, string databaseId, CancellationToken ct)
    {
        var path = await PathAsync(scope, home, databaseId, ct);
        var result = new List<ProducerBackup>();
        var cursors = new HashSet<string>(StringComparer.Ordinal);
        string? after = null;
        do
        {
            var page = await SendAsync<BackupResponse>(scope, home, "databases:read", HttpMethod.Get, path + "/backups?limit=100" +
                (string.IsNullOrEmpty(after) ? "" : "&after=" + Segment(after)), null, ct);
            result.AddRange(page.Items);
            after = page.Next;
            if (!string.IsNullOrEmpty(after) && !cursors.Add(after)) throw new HttpRequestException("Backup pagination did not advance.");
        } while (!string.IsNullOrEmpty(after));
        return result;
    }

    public async Task<ProducerBackup> CreateAsync(DatabaseScope scope, DatabaseRegion home, string databaseId, CancellationToken ct) =>
        await SendAsync<ProducerBackup>(scope, home, "databases:write", HttpMethod.Post, await PathAsync(scope, home, databaseId, ct) + "/backups", null, ct);
    public async Task<ProducerBackupPolicy> PolicyAsync(DatabaseScope scope, DatabaseRegion home, string databaseId, CancellationToken ct) =>
        await SendAsync<ProducerBackupPolicy>(scope, home, "databases:read", HttpMethod.Get, await PathAsync(scope, home, databaseId, ct) + "/backup-policy", null, ct);
    public async Task<ProducerBackupPolicy> SetPolicyAsync(DatabaseScope scope, DatabaseRegion home, string databaseId, string? fullBackupCron,
        int retentionDays, bool disabled, CancellationToken ct) =>
        await SendAsync<ProducerBackupPolicy>(scope, home, "databases:write", HttpMethod.Put, await PathAsync(scope, home, databaseId, ct) + "/backup-policy",
            new { fullBackupCron, retentionDays, disabled }, ct);

    // The destination is the project's; mode both keeps platform backups and mirrors them to the customer's bucket.
    public async Task<BackupDestinationView?> DestinationAsync(DatabaseScope scope, DatabaseRegion home, CancellationToken ct)
    {
        try { return await SendAsync<BackupDestinationView>(scope, home, "databases:read", HttpMethod.Get, ProjectPath(scope) + "/backup-destination", null, ct); }
        catch (DatabaseProviderException error) when (error.StatusCode == HttpStatusCode.NotFound) { return null; }
    }
    public Task<BackupDestinationView> SetDestinationAsync(DatabaseScope scope, DatabaseRegion home, string endpoint, string region, string bucket,
        string accessKeyId, string secretKey, CancellationToken ct) =>
        SendAsync<BackupDestinationView>(scope, home, "databases:write", HttpMethod.Put, ProjectPath(scope) + "/backup-destination",
            new { endpoint, region, bucket, accessKeyId, secretKey, mode = "both" }, ct);
    public Task RemoveDestinationAsync(DatabaseScope scope, DatabaseRegion home, CancellationToken ct) =>
        SendAsync<JsonElement?>(scope, home, "databases:write", HttpMethod.Delete, ProjectPath(scope) + "/backup-destination", null, ct);
    public async Task<ExportScheduleView> ExportScheduleAsync(DatabaseScope scope, DatabaseRegion home, string databaseId, CancellationToken ct) =>
        await SendAsync<ExportScheduleView>(scope, home, "databases:read", HttpMethod.Get, await PathAsync(scope, home, databaseId, ct) + "/export-schedule", null, ct);
    public async Task<ExportScheduleView> SetExportScheduleAsync(DatabaseScope scope, DatabaseRegion home, string databaseId, string? cron, CancellationToken ct) =>
        await SendAsync<ExportScheduleView>(scope, home, "databases:write", HttpMethod.Put, await PathAsync(scope, home, databaseId, ct) + "/export-schedule", new { cron }, ct);

    public async Task<ManagedDatabase> RestoreAsync(DatabaseScope scope, DatabaseRegion home, string databaseId, string backupId, string? name, CancellationToken ct)
    {
        var restored = await SendAsync<ManagedDatabase>(scope, home, "databases:write", HttpMethod.Post,
            await PathAsync(scope, home, databaseId, ct) + "/backups/" + Segment(backupId) + "/restore", new { name }, ct);
        if (!TerraScale.DotNet.TerraScaleIds.IsValid(restored.Id) || restored.ProjectId != scope.ProjectId ||
            restored.Id == databaseId || restored.BranchedFrom != databaseId)
            throw new HttpRequestException("Backup restoration returned an invalid database identity.");
        // The restore route returns the raw catalog record: null replicas is legitimate there.
        // The managed GET body normalizes the actual current placement.
        var placed = await SendAsync<ManagedDatabase>(scope, home, "databases:read", HttpMethod.Get, ProjectPath(scope) + "/databases/" + Segment(restored.Name), null, ct);
        if (placed.Id != restored.Id || placed.ProjectId != scope.ProjectId || placed.Name != restored.Name ||
            placed.BranchedFrom != databaseId || placed.CreatedAt != restored.CreatedAt ||
            placed.HomeRegion != home.Id || placed.ReplicaRegions is null)
            throw new HttpRequestException("Backup restoration could not be confirmed in its project.");
        return placed;
    }

    private async Task<T> SendAsync<T>(DatabaseScope scope, DatabaseRegion home, string permission, HttpMethod method, string path, object? body, CancellationToken ct)
    {
        var session = scope.Session ?? throw new HttpRequestException("Current user authorization is required.");
        var token = await tokens.GetAsync(session, scope.OrganizationId, scope.ProjectId, [permission], ct);
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(TimeSpan.FromSeconds(30));
        using var request = new HttpRequestMessage(method, new Uri(home.Api, path));
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        request.Headers.Accept.ParseAdd("application/json");
        request.Headers.Add("X-TerraScale-Region-Pin", home.Id);
        if (body is not null) request.Content = JsonContent.Create(body, options: Json);
        using var response = await factory.CreateClient("platform").SendAsync(request, HttpCompletionOption.ResponseHeadersRead, timeout.Token);
        if (response.StatusCode == HttpStatusCode.MisdirectedRequest) throw DatabaseWrongRegionException.From(response);
        if (!response.IsSuccessStatusCode) throw await FailureAsync(response, path, timeout.Token);
        if (response.StatusCode == HttpStatusCode.NoContent && default(T) is null) return default!;
        using var document = await PlatformJson.ReadAsync(response.Content, timeout.Token);
        try { return document.RootElement.Deserialize<T>(Json) ?? throw new HttpRequestException("The backup provider returned an invalid response."); }
        catch (JsonException error) { throw new HttpRequestException("The backup provider returned an invalid response.", error); }
    }

    private static async Task<Exception> FailureAsync(HttpResponseMessage response, string path, CancellationToken ct)
    {
        // Read a bounded envelope, keep only a recognized code, and never retain upstream text in exceptions or logs.
        string? code = null;
        try
        {
            using var json = await PlatformJson.ReadAsync(response.Content, ct);
            if (json.RootElement.ValueKind == JsonValueKind.Object && json.RootElement.TryGetProperty("error", out var error) &&
                error.ValueKind == JsonValueKind.Object && error.TryGetProperty("code", out var value) && value.ValueKind == JsonValueKind.String)
                code = value.GetString();
        }
        catch (Exception error) when (error is JsonException or HttpRequestException or IOException) { }
        var message = ((int)response.StatusCode, code) switch
        {
            (409, "database_limit") => "Your organization has reached its database limit. Remove an unused database or upgrade your plan, then restore again.",
            (409, "name_taken") => "That database name is already in use. Choose a different name for the restored database.",
            (404, "not_found") when path.EndsWith("/restore", StringComparison.Ordinal) => "That snapshot could not be opened. Refresh the backup list and try again. If it is still listed, contact support.",
            (429 or 503, "capacity") => "Backup capacity is temporarily unavailable. Wait a moment, then try again.",
            (400, "invalid_request") when path.EndsWith("/backup-policy", StringComparison.Ordinal) => "Set snapshot retention from 1 to 35 days and use a valid 5-field UTC cron schedule.",
            (400, "invalid_request") when path.EndsWith("/restore", StringComparison.Ordinal) => "Choose a valid name for the restored database: 1 to 63 lowercase letters, numbers, hyphens or underscores, starting with a letter.",
            _ => null
        };
        return message is null ? new DatabaseProviderException(response.StatusCode)
            : new AccountAccessException(message, (int)response.StatusCode, code!);
    }

    // UI URLs use database IDs; the authoritative project list resolves them to the route names TerraBase expects.
    private async Task<string> PathAsync(DatabaseScope scope, DatabaseRegion home, string databaseId, CancellationToken ct) =>
        ProjectPath(scope) + "/databases/" + Segment((await databases.ListAsync(scope, home, ct)).SingleOrDefault(item => item.Id == databaseId)?.Name
            ?? throw new DatabaseProviderException(HttpStatusCode.NotFound));
    private static string Segment(string value) => Uri.EscapeDataString(value);
    private static string ProjectPath(DatabaseScope scope) => "/v1/projects/" + Segment(scope.ProjectId);
    private sealed record BackupResponse(IReadOnlyList<ProducerBackup> Items, string? Next);
}
