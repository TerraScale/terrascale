using System.Net.Http.Headers;
using System.Text.Json;
using TerraScale.Dashboard.Features.Auth;
using TerraScale.Dashboard.Platform.Persistence;
using TerraScale.DotNet;

namespace TerraScale.Dashboard.Features.Logs;

public sealed record DatabaseLogEntry(string Id, DateTimeOffset Time, string Region, string Method, string Route,
    int Status, double LatencyMs, long Operations, bool IsQuery);
public sealed record DatabaseLogBatch(string DatabaseId, IReadOnlyList<DatabaseLogEntry> Items, bool Truncated, bool RecordingLimited);

public sealed class DatabaseLogBackend(IHttpClientFactory factory, IProjectAccessTokens tokens, IDatabaseBackend databases)
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

    internal static void Validate(DatabaseLogBatch batch, string databaseId, string status)
    {
        if (batch.DatabaseId != databaseId || batch.Items is null || batch.Items.Count > 100 ||
            batch.Items.Any(item => item is null || !TerraScaleIds.IsValid(item.Id) || item.Time == default ||
                DatabaseRegion.FromId(item.Region) is null || item.Method is not ("GET" or "POST" or "PUT" or "PATCH" or "DELETE" or "HEAD" or "OPTIONS" or "OTHER") ||
                item.Route is not { Length: > 0 and <= 512 } || !item.Route.StartsWith('/') || item.Route.Contains('?') || item.Route.Any(char.IsControl) ||
                item.Status is < 100 or > 599 || !double.IsFinite(item.LatencyMs) || item.LatencyMs < 0 || item.Operations < 0 ||
                status.Length > 0 && item.Status / 100 != status[0] - '0') ||
            batch.Items.Select(item => item.Id).Distinct(StringComparer.Ordinal).Count() != batch.Items.Count)
            throw new JsonException("Request logs returned invalid measurements or a different database.");
        if (!batch.Items.SequenceEqual(batch.Items.OrderByDescending(item => item.Time).ThenByDescending(item => item.Id, StringComparer.Ordinal)))
            throw new JsonException("Request logs are not in recent-first order.");
    }
}
