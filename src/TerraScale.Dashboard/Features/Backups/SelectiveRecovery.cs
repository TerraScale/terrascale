using TerraScale.Dashboard.Platform.Rpc;
using TerraScale.TerraBase.Contracts;
using TerraScale.TerraBase.Contracts.Backups;
using System.Globalization;
using System.Net;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.Json.Serialization.Metadata;
using TerraScale.Dashboard.Features.Auth;
using TerraScale.Dashboard.Features.Data;
using TerraScale.Dashboard.Common.UI;

namespace TerraScale.Dashboard.Features.Backups
{
    public sealed record RecoverySelection(string Collection, [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string[]? Ids);
    public sealed record RecoveryPreviewRequest(DateTimeOffset PointInTime, RecoverySelection[] Selections);
    public sealed record RecoveryRecord(string Collection, string Id, JsonElement Historical, JsonElement? Current, string? CurrentVersion);
    public sealed record RecoveryPreview(string Approval, DateTimeOffset ExpiresAt, IReadOnlyList<RecoveryRecord> Records);
    public sealed record RecoveryIdentity(string Collection, string Id);
    public sealed record RecoveryApplyRequest(string Approval, RecoveryIdentity[] Records);
    public sealed record RecoveryWritten(string Id, string Version);
    public sealed record RecoveryApplied(string Offset, string Durability, IReadOnlyList<RecoveryWritten> Results);
    public sealed record RecoveryWindow(DateTimeOffset Earliest, DateTimeOffset Latest)
    {
        public static string Input(DateTimeOffset point)
        {
            return point.UtcDateTime.ToString("yyyy-MM-dd'T'HH:mm:ss.fff", CultureInfo.InvariantCulture);
        }
    }
    public sealed record SelectiveRecoveryPage(ShellModel Shell, DatabaseRecord Database, RecoveryWindow? Window = null,
        string Point = "", string Collection = "", string Ids = "", RecoveryPreview? Preview = null, string Error = "", int? Applied = null);

    // Recovery uses current project authority in the database home region.
    public sealed class SelectiveRecovery(TerraBaseRpcClient rpc, IProjectAccessTokens tokens, DatabaseService databases)
    {
        public async Task<RecoveryWindow?> WindowAsync(AccountContext context, DatabaseRecord database, CancellationToken ct)
        {
            RecoveryWindow window;
            try
            {
                window = await SendAsync<RecoveryPreviewRequest, RecoveryWindow>(context, database, "", null,
                RecoveryJson.Default.RecoveryPreviewRequest, RecoveryJson.Default.RecoveryWindow, ct);
            }
            catch (DatabaseProviderException error) when (error.StatusCode == HttpStatusCode.NotFound) { return null; }
            if (window.Earliest == default || window.Latest < window.Earliest)
            {
                throw new JsonException("Invalid PITR window.");
            }

            return window;
        }

        public async Task<RecoveryPreview> PreviewAsync(AccountContext context, DatabaseRecord database, RecoveryWindow? window, string point, string collection, string ids, CancellationToken ct)
        {
            DataService.ValidCollection(collection);
            if (window is null || !DateTimeOffset.TryParseExact(point, ["yyyy-MM-dd'T'HH:mm", "yyyy-MM-dd'T'HH:mm:ss", "yyyy-MM-dd'T'HH:mm:ss.FFFFFFF"],
                    CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out var instant) || instant < window.Earliest || instant > window.Latest)
            {
                throw new AccountAccessException("Choose a UTC timestamp inside the current PITR window.", 422);
            }

            if (ids.Length > 2800)
            {
                throw new AccountAccessException("Enter up to 100 record IDs.", 422);
            }

            var selected = ids.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
            if (selected.Length > 100 || selected.Distinct(StringComparer.Ordinal).Count() != selected.Length)
            {
                throw new AccountAccessException("Enter up to 100 distinct record IDs, separated by whitespace.", 422);
            }

            foreach (var id in selected)
            {
                DataService.ValidDocumentId(id);
            }

            var result = await SendAsync(context, database, "recovery-preview", new RecoveryPreviewRequest(instant,
                [new(collection, selected.Length == 0 ? null : selected)]), RecoveryJson.Default.RecoveryPreviewRequest, RecoveryJson.Default.RecoveryPreview, ct);
            if (string.IsNullOrWhiteSpace(result.Approval) || result.Approval.Length > 131072 || result.ExpiresAt == default || result.Records is null ||
                result.Records.Count > 100 || result.Records.Any(record => record is null) ||
                result.Records.Select(record => record.Id).Distinct(StringComparer.Ordinal).Count() != result.Records.Count ||
                result.Records.Any(record => record.Collection != collection || !TerraScaleIds.IsValid(record.Id) ||
                    selected.Length > 0 && !selected.Contains(record.Id, StringComparer.Ordinal) || record.Historical.ValueKind != JsonValueKind.Object ||
                    record.Current is { ValueKind: not (JsonValueKind.Null or JsonValueKind.Object) }) ||
                result.Records.Sum(record => System.Text.Encoding.UTF8.GetByteCount(record.Historical.GetRawText()) +
                    (record.Current is { } current ? System.Text.Encoding.UTF8.GetByteCount(current.GetRawText()) : 0)) > 1024 * 1024)
            {
                throw new JsonException("Invalid recovery preview.");
            }

            return result;
        }

        public async Task<int> ApplyAsync(AccountContext context, DatabaseRecord database, string approval, string collection, string[] ids, CancellationToken ct)
        {
            DataService.ValidCollection(collection);
            if (approval.Length is 0 or > 131072 || ids.Length is 0 or > 100 || ids.Distinct(StringComparer.Ordinal).Count() != ids.Length)
            {
                throw new AccountAccessException("Select at least one previewed record to approve. Start a new preview if the approval expired.", 422);
            }

            foreach (var id in ids)
            {
                DataService.ValidDocumentId(id);
            }

            var result = await SendAsync(context, database, "recover", new RecoveryApplyRequest(approval, [.. ids.Select(id => new RecoveryIdentity(collection, id))]),
                RecoveryJson.Default.RecoveryApplyRequest, RecoveryJson.Default.RecoveryApplied, ct);
            if (string.IsNullOrEmpty(result.Offset) || result.Durability is not ("local_durable" or "remote_durable") || result.Results is null || result.Results.Any(record => record is null) ||
                !result.Results.Select(record => record.Id).Order(StringComparer.Ordinal).SequenceEqual(ids.Order(StringComparer.Ordinal)) ||
                result.Results.Any(record => string.IsNullOrEmpty(record.Version)))
            {
                throw new HttpRequestException("Recovery could not be confirmed. Check the current records before previewing again.");
            }

            return result.Results.Count;
        }

        private async Task<TResponse> SendAsync<TRequest, TResponse>(AccountContext context, DatabaseRecord database, string operation, TRequest? body,
            JsonTypeInfo<TRequest> requestType, JsonTypeInfo<TResponse> responseType, CancellationToken ct)
        {
            var scope = DatabaseService.Scope(context);
            var home = await databases.HomeAsync(context, database, ct);
            var access = await tokens.GetAsync(scope.Session!, scope.OrganizationId, scope.ProjectId, operation.Length == 0 ? ["databases:read"] :
                ["databases:write", operation == "recovery-preview" ? "documents:read" : "documents:write"], ct);
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(TimeSpan.FromSeconds(30));
            var client = rpc.User<IBackupsService>(access, home.Id).WithCancellationToken(timeout.Token);
            try
            {
                var request = body is null ? default : JsonSerializer.SerializeToElement(body, requestType);
                var result = operation switch
                {
                    "" => JsonSerializer.SerializeToElement(await client.GetRestoreWindowAsync(scope.ProjectId, database.Name, null), BackupsJson.Default.TerraBaseRestoreWindow),
                    "recovery-preview" => await client.PreviewRecoveryAsync(scope.ProjectId, database.Name, request),
                    "recover" => await client.RecoverAsync(scope.ProjectId, database.Name, request),
                    _ => throw new InvalidOperationException("Unknown recovery operation.")
                };
                return result.Deserialize(responseType) ?? throw new JsonException("Invalid recovery response.");
            }
            catch (TerraBaseWrongRegionException error) { throw new DatabaseWrongRegionException(error.Region, error.HomeRegion, error.Regions); }
            catch (TerraBaseException error) when (error.Status is 409 or 412 or 410)
            {
                throw new AccountAccessException("Recovery approval is stale, expired, or unavailable on this node. No records were restored. Preview again and review current differences.", 409);
            }
            catch (TerraBaseException error) when (error.Status is 400 or 422 or 413)
            {
                throw new AccountAccessException("Recovery was refused. Use a retained point and smaller explicit ID batches (100 records / 1 MiB). The collection must currently exist; recreate a dropped collection explicitly before previewing again.", 422);
            }
            catch (TerraBaseException error) when (error.Status == 503)
            {
                throw new AccountAccessException("Recovery is busy or temporarily unavailable. " +
                    (error.RetryAfter is { } delay ? $"Retry after {Math.Ceiling(delay.TotalSeconds)} seconds. " : "Try again shortly. ") +
                    "Preview again before approving records.", 503);
            }
            catch (TerraBaseException error) { throw new DatabaseProviderException((HttpStatusCode)error.Status); }
        }
    }

    public static class SelectiveRecoveryEndpoints
    {
        public static IEndpointRouteBuilder MapSelectiveRecovery(this IEndpointRouteBuilder endpoints)
        {
            endpoints.MapMethods("/databases/{databaseId}/recovery", ["GET", "POST"], async Task<IResult> (HttpContext http, string databaseId,
                DashboardSessions sessions, AccountService accounts, DatabaseService databases, SelectiveRecovery recovery) =>
            {
                var context = await AccountRequest.RequireAsync(http, sessions, accounts);
                if (context is null)
                {
                    return AccountRequest.LoginRedirect(http);
                }

                try
                {
                    context = await databases.CurrentAsync(context, http.RequestAborted);
                    var database = DatabaseService.Database(context, databaseId);
                    if (!context.CanManage)
                    {
                        throw new AccountAccessException("Organization management access is required.");
                    }

                    if (PlatformDatabases.IsPlatform(context.Organization!.Id))
                    {
                        throw PlatformDatabases.ReadOnly();
                    }

                    if (database.Status != "ready" || context.Organization.DeletionRequested)
                    {
                        throw new AccountAccessException("Recovery requires an active, ready database.", 409);
                    }

                    http.Response.Headers.CacheControl = "no-store";
                    var page = new SelectiveRecoveryPage(ShellModel.For(http, context, "Selective recovery"), database);
                    var status = 200;
                    try
                    {
                        var window = await recovery.WindowAsync(context, database, http.RequestAborted);
                        page = page with { Window = window, Point = window is null ? "" : RecoveryWindow.Input(window.Latest) };
                        if (HttpMethods.IsPost(http.Request.Method))
                        {
                            var form = await http.Request.ReadFormAsync(http.RequestAborted);
                            if (!Csrf.Validate(http, form))
                            {
                                return Results.BadRequest("Invalid form token.");
                            }

                            page = page with { Point = form["point"].ToString(), Collection = form["collection"].ToString(), Ids = form["ids"].ToString() };
                            page = form["operation"] == "apply"
                                ? page with
                                {
                                    Applied = await recovery.ApplyAsync(context, database, form["approval"].ToString(), page.Collection,
                                    [.. form["approved"].Select(value => value ?? "")], http.RequestAborted)
                                }
                                : page with { Preview = await recovery.PreviewAsync(context, database, page.Window, page.Point, page.Collection, page.Ids, http.RequestAborted) };
                        }
                    }
                    catch (Exception error) when (BackupEndpoints.KnownFailure(error) && (error is not OperationCanceledException || !http.RequestAborted.IsCancellationRequested))
                    {
                        status = DatabaseResults.Status(error);
                        page = page with { Error = error is JsonException ? "Recovery returned an invalid response. Preview again before approving any records." : DatabaseResults.Message(error) };
                    }
                    var view = SelectiveRecoveryView.Create(page);
                    view.StatusCode = status;
                    return view;
                }
                catch (Exception error) when (BackupEndpoints.KnownFailure(error)) { return DatabaseResults.PageFailure(http, context, error); }
            });
            return endpoints;
        }
    }

    [JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase, RespectRequiredConstructorParameters = true, RespectNullableAnnotations = true)]
    [JsonSerializable(typeof(RecoveryPreviewRequest))]
    [JsonSerializable(typeof(RecoveryPreview))]
    [JsonSerializable(typeof(RecoveryApplyRequest))]
    [JsonSerializable(typeof(RecoveryApplied))]
    [JsonSerializable(typeof(RecoveryWindow))]
    internal sealed partial class RecoveryJson : JsonSerializerContext;
}
