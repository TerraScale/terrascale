using System.Net.Http.Headers;
using System.Text.Json;
using TerraScale.Dashboard.Features.Auth;
using TerraScale.Dashboard.Platform.Persistence;
using TerraScale.DotNet;
using TerraScale.DotNet.TerraBase;

namespace TerraScale.Dashboard.Features.Logs;

public sealed record DatabaseLogEntry(string Id, DateTimeOffset Time, string Region, string Method, string Route,
    int Status, double LatencyMs, long Operations, bool IsQuery, string? RequestId = null);
// A cursor read (after=) lists settled requests oldest first, returns NextCursor, and sets Gap when requests after the cursor may have been discarded unread.
public sealed record DatabaseLogBatch(string DatabaseId, IReadOnlyList<DatabaseLogEntry> Items, bool Truncated, bool RecordingLimited,
    string? NextCursor = null, bool Gap = false);

public sealed class DatabaseLogBackend(IHttpClientFactory factory, IProjectAccessTokens tokens, IDatabaseBackend databases, TimeProvider time)
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    { RespectRequiredConstructorParameters = true, RespectNullableAnnotations = true };

    public async Task<DatabaseLogBatch> ReadAsync(DatabaseScope scope, DatabaseRegion home, string databaseId, string status, CancellationToken cancellationToken)
    {
        if (status is not ("" or "2xx" or "3xx" or "4xx" or "5xx")) throw new AccountAccessException("Choose a valid status filter.", 400);
        if (PlatformDatabases.IsPlatform(scope.OrganizationId)) throw PlatformDatabases.ReadOnly();
        // Resolve the UI's ULID through the official SDK rather than trusting a cached route name.
        var database = (await databases.ListAsync(scope, home, cancellationToken)).SingleOrDefault(item => item.Id == databaseId)
            ?? throw new AccountAccessException("Database is unavailable.", 404);
        var session = scope.Session ?? throw new HttpRequestException("Current user authorization is required.");
        var access = await tokens.GetAsync(session, scope.OrganizationId, scope.ProjectId, ["databases:read"], cancellationToken);
        var path = $"/v1/projects/{Uri.EscapeDataString(scope.ProjectId)}/databases/{Uri.EscapeDataString(database.Name)}/logs" +
            (status.Length == 0 ? "" : "?status=" + status);
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(30));
        // The SDK has no logs wrapper yet; use the existing platform HTTP extension pattern with current project authority.
        using var request = new HttpRequestMessage(HttpMethod.Get, new Uri(home.Api, path));
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", access);
        request.Headers.Add("X-TerraScale-Region-Pin", home.Id);
        using var response = await factory.CreateClient("platform").SendAsync(request, HttpCompletionOption.ResponseHeadersRead, timeout.Token);
        response.EnsureSuccessStatusCode();
        using var document = await PlatformJson.ReadAsync(response.Content, timeout.Token);
        var batch = document.RootElement.Deserialize<DatabaseLogBatch>(Json) ?? throw new JsonException("Request logs are missing.");
        Validate(batch, databaseId, status);
        return batch;
    }

    public async Task<IReadOnlyList<TerraBaseQueryShape>> ReadQueryShapesAsync(DatabaseScope scope, DatabaseRegion home, string databaseId, CancellationToken cancellationToken)
    {
        if (PlatformDatabases.IsPlatform(scope.OrganizationId)) throw PlatformDatabases.ReadOnly();
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(TimeSpan.FromSeconds(5));
        var database = (await databases.ListAsync(scope, home, deadline.Token)).SingleOrDefault(item => item.Id == databaseId)
            ?? throw new AccountAccessException("Database is unavailable.", 404);
        var session = scope.Session ?? throw new HttpRequestException("Current user authorization is required.");
        var access = await tokens.GetAsync(session, scope.OrganizationId, scope.ProjectId, ["databases:read"], deadline.Token);
        using var http = factory.CreateClient("platform");
        var sdk = new TerraBaseDocumentsClient(http, new TerraBaseDocumentsClientOptions
        {
            BaseAddress = home.Api,
            Region = home.Id,
            AccessTokenProvider = _ => ValueTask.FromResult(access)
        });
        var shapes = await sdk.Database(scope.ProjectId, database.Name).ListQueryShapesAsync(deadline.Token);
        if (shapes.Items is null || shapes.Items.Any(item => item is null || string.IsNullOrEmpty(item.Collection) || string.IsNullOrEmpty(item.Shape) ||
            item.Queries < 0 || item.RowsScanned < 0 || item.LastSeenAt == default ||
            !double.IsFinite(item.TotalLatencyMs) || item.TotalLatencyMs < 0 || !double.IsFinite(item.MaxLatencyMs) || item.MaxLatencyMs < 0 ||
            item.P95LatencyMs is { } p95 && (!double.IsFinite(p95) || p95 < 0)))
            throw new JsonException("Query shapes returned invalid measurements.");
        var now = time.GetUtcNow();
        return [.. shapes.Items.Where(item => item.LastSeenAt >= now.AddHours(-24) && item.LastSeenAt <= now)
            .OrderByDescending(item => item.P95LatencyMs ?? item.MaxLatencyMs)
            .ThenBy(item => item.Collection, StringComparer.Ordinal).ThenBy(item => item.Id, StringComparer.Ordinal)];
    }

    internal static void Validate(DatabaseLogBatch batch, string databaseId, string status, bool cursor = false)
    {
        if (batch.DatabaseId != databaseId || batch.Items is null || batch.Items.Count > 100 ||
            batch.Items.Any(item => item is null || !TerraScaleIds.IsValid(item.Id) || item.RequestId is not null && !TerraScaleIds.IsValid(item.RequestId) || item.Time == default ||
                DatabaseRegion.FromId(item.Region) is null || item.Method is not ("GET" or "POST" or "PUT" or "PATCH" or "DELETE" or "HEAD" or "OPTIONS" or "OTHER") ||
                item.Route is not { Length: > 0 and <= 512 } || !item.Route.StartsWith('/') || item.Route.Contains('?') || item.Route.Any(char.IsControl) ||
                item.Status is < 100 or > 599 || !double.IsFinite(item.LatencyMs) || item.LatencyMs < 0 || item.Operations < 0 ||
                status.Length > 0 && item.Status / 100 != status[0] - '0') ||
            batch.Items.Select(item => item.Id).Distinct(StringComparer.Ordinal).Count() != batch.Items.Count)
            throw new JsonException("Request logs returned invalid measurements or a different database.");
        if (cursor)
        {
            if (!TerraScaleIds.IsValid(batch.NextCursor) || !batch.Items.SequenceEqual(batch.Items.OrderBy(item => item.Id, StringComparer.Ordinal)) ||
                batch.Items.Count > 0 && string.CompareOrdinal(batch.Items[^1].Id, batch.NextCursor) > 0)
                throw new JsonException("Request log cursor page is not in cursor order.");
            return;
        }
        if (!batch.Items.SequenceEqual(batch.Items.OrderByDescending(item => item.Time).ThenByDescending(item => item.Id, StringComparer.Ordinal)))
            throw new JsonException("Request logs are not in recent-first order.");
    }
}
