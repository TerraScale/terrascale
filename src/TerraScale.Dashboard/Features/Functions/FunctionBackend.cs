using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.RegularExpressions;
using TerraScale.Dashboard.Features.Accounts;
using TerraScale.Dashboard.Features.Auth;
using TerraScale.Dashboard.Platform.Persistence;
using TerraScale.DotNet;
using TerraScale.DotNet.TerraBase;

namespace TerraScale.Dashboard.Features.Functions;

public sealed record FunctionTrigger(string Collection, IReadOnlyList<string>? Events);
public sealed record FunctionRecord(string Name, string? Source, FunctionTrigger? Trigger, string? Cron, DateTimeOffset CreatedAt, DateTimeOffset UpdatedAt);
public sealed record FunctionInvocation(string Id, DateTimeOffset At, string Trigger, string Status, double DurationMs, IReadOnlyList<string> Lines, string? Error);

// Functions belong to TerraBase (terrascale docs/functions/design.md). The SDK has no functions wrapper yet; use the
// platform HTTP pattern of the logs and backups pages with current project authority. A missing API (404) reads as empty.
public sealed partial class FunctionBackend(DatabaseService databases, IProjectAccessTokens tokens, IHttpClientFactory factory)
{
    // Source is at most 256 KiB and 25 invocations keep at most 50 lines of 1 KiB each; escaping can expand both.
    private const long MaxBytes = 8 * 1024 * 1024;
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    { RespectRequiredConstructorParameters = true, RespectNullableAnnotations = true };

    public static bool ValidName(string name) => NamePattern().IsMatch(name);

    public static Uri InvokeUrl(AccountContext context, DatabaseRecord database, string name) =>
        new(TerraBaseDiscovery.Origin, Path(DatabaseService.Scope(context).ProjectId, database.Name) + "/" + Uri.EscapeDataString(name) + "/invoke");

    public async Task<IReadOnlyList<FunctionRecord>> ListAsync(AccountContext context, DatabaseRecord database, CancellationToken ct) =>
        await SendAsync<IReadOnlyList<FunctionRecord>>(context, database, HttpMethod.Get, "", ct) ?? [];

    public Task<FunctionRecord?> ReadAsync(AccountContext context, DatabaseRecord database, string name, CancellationToken ct) =>
        SendAsync<FunctionRecord>(context, database, HttpMethod.Get, Segment(name), ct);

    public async Task<IReadOnlyList<FunctionInvocation>> LogsAsync(AccountContext context, DatabaseRecord database, string name, CancellationToken ct) =>
        await SendAsync<IReadOnlyList<FunctionInvocation>>(context, database, HttpMethod.Get, Segment(name) + "/logs?limit=25", ct) ?? [];

    public async Task<IReadOnlyList<TerraBaseChangeWebhookDelivery>> DeliveriesAsync(AccountContext context, DatabaseRecord database, string name, CancellationToken ct) =>
        await SendAsync<IReadOnlyList<TerraBaseChangeWebhookDelivery>>(context, database, HttpMethod.Get, Segment(name) + "/deliveries", ct) ?? [];

    // A function that is already gone (404) needs no further delete.
    public async Task DeleteAsync(AccountContext context, DatabaseRecord database, string name, CancellationToken ct)
    {
        await SendAsync<JsonElement?>(context, database, HttpMethod.Delete, Segment(name), ct);
    }

    private static string Segment(string name) => "/" + (ValidName(name) ? name : throw new AccountAccessException("This function does not exist.", 404));

    private static string Path(string project, string database) =>
        $"/v1/projects/{Uri.EscapeDataString(project)}/databases/{Uri.EscapeDataString(database)}/functions";

    // Returns default when TerraBase answers 404 for the route or the function.
    private async Task<T?> SendAsync<T>(AccountContext context, DatabaseRecord database, HttpMethod method, string suffix, CancellationToken ct)
    {
        context = await databases.CurrentAsync(context, ct);
        database = DatabaseService.Database(context, database.Id);
        if (method == HttpMethod.Delete && !context.CanWriteProjectData) throw new AccountAccessException("Write access is required.");
        var scope = DatabaseService.Scope(context);
        if (PlatformDatabases.IsPlatform(scope.OrganizationId)) throw PlatformDatabases.ReadOnly();
        var home = await databases.HomeAsync(context, database, ct);
        // Every functions route requires both document scopes (design: Auth).
        var access = await tokens.GetAsync(context.Session, scope.OrganizationId, scope.ProjectId, ["documents:read", "documents:write"], ct);
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(TimeSpan.FromSeconds(30));
        using var request = new HttpRequestMessage(method, new Uri(home.Api, Path(scope.ProjectId, database.Name) + suffix));
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", access);
        request.Headers.Add("X-TerraScale-Region-Pin", home.Id);
        using var response = await factory.CreateClient("platform").SendAsync(request, HttpCompletionOption.ResponseHeadersRead, timeout.Token);
        if (response.StatusCode == HttpStatusCode.NotFound) return default;
        response.EnsureSuccessStatusCode();
        if (method == HttpMethod.Delete) return default;
        if (response.Content.Headers.ContentLength > MaxBytes) throw new HttpRequestException("Functions response is too large.");
        await response.Content.LoadIntoBufferAsync(MaxBytes, timeout.Token);
        var value = await response.Content.ReadFromJsonAsync<T>(Json, timeout.Token) ?? throw new JsonException("Functions response is missing.");
        Validate(value);
        return value;
    }

    internal static void Validate(object? value)
    {
        var valid = value switch
        {
            FunctionRecord function => Valid(function),
            IReadOnlyList<FunctionRecord> functions => functions.Count <= 32 && functions.All(item => item is not null && Valid(item)),
            IReadOnlyList<FunctionInvocation> logs => logs.Count <= 100 && logs.All(item => item is not null && TerraScaleIds.IsValid(item.Id) &&
                item.Trigger is "http" or "change" or "cron" && item.Status is "ok" or "error" && double.IsFinite(item.DurationMs) && item.DurationMs >= 0 &&
                item.Lines is { Count: <= 50 } && item.Lines.All(line => line is not null)) &&
                logs.SequenceEqual(logs.OrderByDescending(item => item.At).ThenByDescending(item => item.Id, StringComparer.Ordinal)),
            IReadOnlyList<TerraBaseChangeWebhookDelivery> deliveries => deliveries.Count <= 100 && deliveries.All(item => item is not null),
            _ => false
        };
        if (!valid) throw new JsonException("Functions returned an invalid response.");
    }

    private static bool Valid(FunctionRecord function) => ValidName(function.Name) && (function.Source?.Length ?? 0) <= 256 * 1024 &&
        (function.Trigger is null || function.Trigger.Collection is { Length: > 0 } && (function.Trigger.Events ?? []).All(item => item is "upsert" or "delete"));

    [GeneratedRegex("^[a-z0-9][a-z0-9_-]{0,62}$")]
    private static partial Regex NamePattern();
}
