using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using TerraScale.Dashboard.Features.Databases;
using TerraScale.Dashboard.Platform.Persistence;
using TerraScale.DotNet.TerraBase;

namespace TerraScale.Dashboard.Features.Backups;

// The official SDK1309258 has no backup wrappers. These are the implemented
// producer b4919bd3 legacy aliases of its published project backup endpoints,
// authenticated as the signed-in user and scoped to the canonical org/project.
// Restore is confirmed with the official SDK before dashboard state is updated.
public sealed class BackupBackend(IHttpClientFactory factory) : IBackupBackend
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    { RespectRequiredConstructorParameters = true, RespectNullableAnnotations = true };

    public async Task<IReadOnlyList<ProducerBackup>> ListAsync(DatabaseScope scope, DatabaseRegion home, string databaseId, CancellationToken ct)
    {
        var result = new List<ProducerBackup>();
        var cursors = new HashSet<string>(StringComparer.Ordinal);
        string? after = null;
        do
        {
            var page = await SendAsync<BackupResponse>(scope, home, HttpMethod.Get, Path(scope, databaseId) + "/backups?limit=100" +
                (string.IsNullOrEmpty(after) ? "" : "&after=" + Segment(after)), null, ct);
            result.AddRange(page.Items);
            after = page.Next;
            if (!string.IsNullOrEmpty(after) && !cursors.Add(after)) throw new HttpRequestException("Backup pagination did not advance.");
        } while (!string.IsNullOrEmpty(after));
        return result;
    }

    public Task<ProducerBackup> CreateAsync(DatabaseScope scope, DatabaseRegion home, string databaseId, CancellationToken ct) =>
        SendAsync<ProducerBackup>(scope, home, HttpMethod.Post, Path(scope, databaseId) + "/backups", null, ct);
    public Task<ProducerBackupPolicy> PolicyAsync(DatabaseScope scope, DatabaseRegion home, string databaseId, CancellationToken ct) =>
        SendAsync<ProducerBackupPolicy>(scope, home, HttpMethod.Get, Path(scope, databaseId) + "/backup-policy", null, ct);
    public Task<ProducerBackupPolicy> SetPolicyAsync(DatabaseScope scope, DatabaseRegion home, string databaseId, string? fullBackupCron,
        int retentionDays, bool disabled, CancellationToken ct) =>
        SendAsync<ProducerBackupPolicy>(scope, home, HttpMethod.Put, Path(scope, databaseId) + "/backup-policy",
            new { fullBackupCron, retentionDays, disabled }, ct);

    public async Task<ManagedDatabase> RestoreAsync(DatabaseScope scope, DatabaseRegion home, string databaseId, string backupId, string? name, CancellationToken ct)
    {
        var restored = await SendAsync<ManagedDatabase>(scope, home, HttpMethod.Post,
            Path(scope, databaseId) + "/backups/" + Segment(backupId) + "/restore", new { name }, ct);
        if (!TerraScale.DotNet.TerraScaleIds.IsValid(restored.Id) || restored.ProjectId != scope.ProjectId ||
            restored.Id == databaseId || restored.BranchedFrom != databaseId)
            throw new HttpRequestException("Backup restoration returned an invalid database identity.");
        var client = new TerraBaseManagementClient(factory.CreateClient("platform"), new TerraBaseManagementClientOptions
        { BaseAddress = home.Api, AccessTokenProvider = _ => ValueTask.FromResult(scope.AccessToken) });
        var confirmed = await client.GetDatabaseAsync(scope.UserId, scope.OrganizationId, scope.ProjectId, restored.Id, ct);
        if (confirmed.Id != restored.Id || confirmed.ProjectId != scope.ProjectId || confirmed.Name != restored.Name ||
            confirmed.BranchedFrom != databaseId || confirmed.CreatedAt != restored.CreatedAt)
            throw new HttpRequestException("Backup restoration could not be confirmed in its project.");
        // The restore route returns the raw catalog record: null replicas is
        // legitimate there. The managed GET body normalizes the actual current
        // placement, which the official SDK's managed DTO does not retain.
        var placed = await SendAsync<ManagedDatabase>(scope, home, HttpMethod.Get, Path(scope, restored.Id), null, ct);
        if (placed.Id != confirmed.Id || placed.ProjectId != confirmed.ProjectId || placed.Name != confirmed.Name ||
            placed.BranchedFrom != confirmed.BranchedFrom || placed.CreatedAt != confirmed.CreatedAt ||
            placed.HomeRegion != home.Id || placed.ReplicaRegions is null)
            throw new HttpRequestException("Backup restoration did not return its confirmed canonical placement.");
        return placed;
    }

    private async Task<T> SendAsync<T>(DatabaseScope scope, DatabaseRegion home, HttpMethod method, string path, object? body, CancellationToken ct)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(TimeSpan.FromSeconds(30));
        using var request = new HttpRequestMessage(method, new Uri(home.Api, path));
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", scope.AccessToken);
        request.Headers.Add("X-TerraBase-Organization-Id", scope.OrganizationId);
        request.Headers.Add("X-TerraBase-User-Id", scope.UserId);
        if (body is not null) request.Content = JsonContent.Create(body, options: Json);
        using var response = await factory.CreateClient("platform").SendAsync(request, HttpCompletionOption.ResponseHeadersRead, timeout.Token);
        if (!response.IsSuccessStatusCode) throw new DatabaseProviderException(response.StatusCode);
        using var document = await PlatformJson.ReadAsync(response.Content, timeout.Token);
        try { return document.RootElement.Deserialize<T>(Json) ?? throw new HttpRequestException("The backup provider returned an invalid response."); }
        catch (JsonException error) { throw new HttpRequestException("The backup provider returned an invalid response.", error); }
    }

    private static string Segment(string value) => Uri.EscapeDataString(value);
    private static string Path(DatabaseScope scope, string databaseId) => "/v1/management/organizations/" + Segment(scope.OrganizationId) +
        "/projects/" + Segment(scope.ProjectId) + "/databases/" + Segment(databaseId);
    private sealed record BackupResponse(IReadOnlyList<ProducerBackup> Items, string? Next);
}
