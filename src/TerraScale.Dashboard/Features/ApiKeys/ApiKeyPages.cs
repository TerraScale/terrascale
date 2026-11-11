using System.Globalization;
using TerraScale.Dashboard.Features.Auth;
using TerraScale.Dashboard.Features.Databases;
using TerraScale.Dashboard.Features.NotFound;
using TerraScale.Dashboard.Common.UI;

namespace TerraScale.Dashboard.Features.ApiKeys
{
    internal static class ApiKeyPages
    {
        public const string DetailsView = "details";
        public const string IssuerUnavailable = "The live credential issuer is unavailable. Try again shortly.";

        public static async Task RecordRevokeAsync(HttpContext http, AccountContext context, AccountService accounts, ApiKeyService keys, string keyId)
        {
            if (await keys.RevokeAsync(context, keyId, http.RequestAborted))
            {
                await keys.RecordAsync(accounts, context, keyId, false, http.RequestAborted);
            }
        }

        public static async Task<IResult> PageAsync(HttpContext http, AccountContext context, ApiKeyService service, DatabaseService databases, string error = "", string create = "", string name = "", CreatedProjectCredential? revealed = null, int status = 200, string draftScopes = "", string draftDatabaseId = "", string draftExpiresInDays = "", string draftAllowedCidrs = "", string draftKeyCidrs = "")
        {
            var warning = "";
            IReadOnlyList<ProjectCredential> keys = [];
            var hasMore = false;
            var cursor = http.Request.Query["cursor"].ToString();
            var viewerOnly = false;
            try { (keys, var next, viewerOnly) = await service.ListPageAsync(context, cursor.Length == 0 ? null : cursor, ApiKeyService.IssuerPageSize, http.RequestAborted); hasMore = next is not null; cursor = next ?? ""; }
            catch (Exception failure) when (!http.RequestAborted.IsCancellationRequested && (failure is HttpRequestException || revealed is not null && ApiKeyService.FollowUpFailure(failure)))
            {
                if (revealed is null)
                {
                    if (error.Length == 0)
                    {
                        error = IssuerUnavailable;
                    }
                }
                else { keys = [revealed.Key]; warning = (warning + " The key change succeeded, but the complete key list is unavailable. Save this secret now; do not retry the change.").Trim(); }
            }
            var now = http.RequestServices.GetRequiredService<TimeProvider>().GetUtcNow();
            await databases.SyncAsync(context, http.RequestAborted);
            try { await ApiKeyExpiryNotifications.AddAsync(http.RequestServices.GetRequiredService<AccountService>(), context, keys, now, http.RequestAborted); }
            catch (Exception failure) when (!http.RequestAborted.IsCancellationRequested && revealed is not null && ApiKeyService.FollowUpFailure(failure))
            { warning = (warning + " Expiry reminders could not be updated; the key change succeeded.").Trim(); }
            var form = http.Request.HasFormContentType ? await FormRequests.ReadAsync(http, http.RequestAborted) : null;
            var dialog = form?["dialog"].ToString() ?? "";
            var reopen = error.Length > 0 ? dialog : "";
            var rotated = revealed is not null && dialog.StartsWith("rotate-", StringComparison.Ordinal);
            var grace = form?["previousSecretTtl"].ToString() ?? "";
            if (form is not null && form["view"].ToString() == DetailsView)
            {
                var keyId = form["keyId"].ToString();
                var key = revealed?.Key ?? keys.FirstOrDefault(item => item.Id == keyId);
                try { key ??= await service.GetAsync(context, keyId, http.RequestAborted) ?? throw new KeyNotFoundException(); }
                catch (KeyNotFoundException) { return NotFoundEndpoints.Page(http, context); }
                catch (HttpRequestException) when (!http.RequestAborted.IsCancellationRequested) { }
                if (key is not null)
                {
                    var details = ApiKeyDetailsView.Create(new(ShellModel.For(http, context, key.Name), key, now)
                    {
                        Error = error,
                        Warning = warning,
                        Reopen = reopen,
                        DraftAllowedCidrs = draftKeyCidrs == key.Id ? draftAllowedCidrs : null,
                        DraftGrace = grace,
                        Revealed = revealed,
                        Rotated = rotated
                    });
                    details.StatusCode = status;
                    return details;
                }
            }
            var result = ApiKeysView.Create(new(ShellModel.For(http, context, "API keys"), keys, http.Request.Query["q"].ToString(), http.Request.Query["databaseId"].ToString(), error, create.Length > 0 ? create : http.Request.Query["create"].ToString(), name, revealed, http.Request.Query["status"].ToString(), draftScopes.Length > 0 ? draftScopes : http.Request.Query["scopes"].ToString(), draftDatabaseId, DraftExpiresInDays: draftExpiresInDays, DraftAllowedCidrs: draftAllowedCidrs, Now: now, Cursor: cursor, HasMore: hasMore) { Warning = warning, ViewerOnly = viewerOnly, Reopen = reopen, DraftGrace = grace, Rotated = rotated });
            result.StatusCode = status;
            return result;
        }

        public static async Task<IResult> DetailsAsync(HttpContext http, AccountContext context, ApiKeyService service, DatabaseService databases, string keyId)
        {
            await databases.SyncAsync(context, http.RequestAborted);
            ProjectCredential? key;
            try { key = await service.GetAsync(context, keyId, http.RequestAborted); }
            catch (HttpRequestException) when (!http.RequestAborted.IsCancellationRequested) { return RequestError.Response(http, StatusCodes.Status503ServiceUnavailable, IssuerUnavailable); }
            if (key is null)
            {
                return NotFoundEndpoints.Page(http, context);
            }

            return ApiKeyDetailsView.Create(new(ShellModel.For(http, context, key.Name), key, http.RequestServices.GetRequiredService<TimeProvider>().GetUtcNow()));
        }
    }

    public static class ApiKeyFormat
    {
        public static readonly IReadOnlyList<string> FullAccessScopes = ["sql:read", "sql:write", "databases:read", "databases:write"];
        public static readonly IReadOnlyList<string> ReadOnlyScopes = ["sql:read", "databases:read"];

        public static bool FullAccess(IReadOnlyCollection<string> scopes)
        {
            return FullAccessScopes.All(scopes.Contains);
        }

        public static string Preset(IReadOnlyCollection<string> scopes)
        {
            return FullAccess(scopes) ? "full" : scopes.Count == ReadOnlyScopes.Count && ReadOnlyScopes.All(scopes.Contains) ? "read" : "custom";
        }

        public static string Access(IReadOnlyCollection<string> scopes)
        {
            return Preset(scopes) switch { "full" => "Full", "read" => "Read", _ => "Custom" };
        }

        public static string Hint(ProjectCredential key)
        {
            return key.Id[..10] + "…";
        }

        public static string Date(DateTimeOffset value)
        {
            return value.UtcDateTime.ToString("MMM d, yyyy", CultureInfo.InvariantCulture);
        }

        public static string Instant(DateTimeOffset value)
        {
            return value.UtcDateTime.ToString("yyyy-MM-dd HH:mm 'UTC'", CultureInfo.InvariantCulture);
        }

        public static string Machine(DateTimeOffset value)
        {
            return value.ToString("O", CultureInfo.InvariantCulture);
        }

        public static string Lines(IReadOnlyList<string>? cidrs)
        {
            return string.Join('\n', cidrs ?? []);
        }

        public static string CreatedBy(ProjectCredential key)
        {
            return key.CreatedByEmail.Length > 0 ? key.CreatedByEmail : key.CreatedByUserId.Length > 0 ? key.CreatedByUserId : "Unknown";
        }
    }

    public sealed record ApiKeyActions(ShellModel Shell, ProjectCredential Key, string Query, string View, string Reopen, string Error, string DraftGrace);

    public sealed record ApiKeySecret(CreatedProjectCredential Revealed, bool Rotated, string DoneUrl, string Warning);

    public sealed record ApiKeysPage(ShellModel Shell, IReadOnlyList<ProjectCredential> Keys, string Search = "", string DatabaseId = "", string Error = "", string Create = "", string Name = "", CreatedProjectCredential? Revealed = null, string Status = "", string DraftScopes = "", string DraftDatabaseId = "", string DialogKind = "", string DraftExpiresInDays = "", string DraftAllowedCidrs = "", DateTimeOffset Now = default, string Cursor = "", bool HasMore = false)
    {
        public string Warning { get; init; } = "";
        public bool ViewerOnly { get; init; }
        public string Reopen { get; init; } = "";
        public string DraftGrace { get; init; } = "";
        public bool Rotated { get; init; }

        public AccountContext Context => Shell.Context;
        public IReadOnlyList<ProjectCredential> Filtered => [.. Keys.Where(key => (Status.Length == 0 || Status == "active" == key.IsActive) && (DatabaseId.Length == 0 || key.DatabaseId == DatabaseId) && (Search.Length == 0 || key.Name.Contains(Search, StringComparison.OrdinalIgnoreCase) || key.Id.Contains(Search, StringComparison.OrdinalIgnoreCase)))];
        public string DatabaseFilterTitle => Context.Project?.Databases.GetValueOrDefault(DatabaseId)?.Name ?? "All databases";
        public bool ErrorInDialog => Create.Length > 0 || Filtered.Any(key => Reopen == "rotate-" + key.Id || Reopen == "revoke-" + key.Id);
        public ApiKeySecret? Secret => Revealed is null ? null : new(Revealed, Rotated, WorkspaceUrl.For(Context, With("/api-keys", clear: true)), Warning);
        public ApiKeyActions Actions(ProjectCredential key) => new(Shell, key, With(""), "", Reopen, Error, DraftGrace);

        public string With(string path, bool clear = false, string? create = null)
        {
            var values = new SortedDictionary<string, string?>();
            if (!clear) { values["databaseId"] = DatabaseId; values["q"] = Search; values["status"] = Status; if (HasMore || Cursor.Length > 0) { values["cursor"] = Cursor; } }
            if (create is not null)
            {
                values["create"] = create;
            }

            var query = string.Join('&', values.Where(value => !string.IsNullOrEmpty(value.Value)).Select(value => System.Net.WebUtility.UrlEncode(value.Key) + "=" + System.Net.WebUtility.UrlEncode(value.Value)));
            return query.Length == 0 ? path : path + "?" + query;
        }
    }

    public sealed record ApiKeyDetailsPage(ShellModel Shell, ProjectCredential Key, DateTimeOffset Now)
    {
        public string Error { get; init; } = "";
        public string Warning { get; init; } = "";
        public string Reopen { get; init; } = "";
        public string? DraftAllowedCidrs { get; init; }
        public string DraftGrace { get; init; } = "";
        public CreatedProjectCredential? Revealed { get; init; }
        public bool Rotated { get; init; }

        public AccountContext Context => Shell.Context;
        public string Path => "/api-keys/" + Key.Id;
        public string CidrsDialog => "cidrs-" + Key.Id;
        public bool CidrsFailed => Error.Length > 0 && Reopen == CidrsDialog;
        public bool ErrorInDialog => Error.Length > 0 && (CidrsFailed || Reopen == "rotate-" + Key.Id || Reopen == "revoke-" + Key.Id);
        public ApiKeySecret? Secret => Revealed is null ? null : new(Revealed, Rotated, Path, Warning);
        public ApiKeyActions Actions => new(Shell, Key, "", ApiKeyPages.DetailsView, Reopen, Error, DraftGrace);
    }
}
