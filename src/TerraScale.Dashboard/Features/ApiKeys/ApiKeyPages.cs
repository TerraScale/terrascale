using TerraScale.Dashboard.Features.Auth;
using TerraScale.Dashboard.Features.Databases;
using TerraScale.Dashboard.Common.UI;

namespace TerraScale.Dashboard.Features.ApiKeys
{
    internal static class ApiKeyPages
    {
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
            IReadOnlyList<string>? projectCidrs = null;
            var viewerOnly = false;
            var network = context.Project is null ? Task.FromResult<IReadOnlyList<string>>([]) : service.ProjectAllowedCidrsAsync(context, http.RequestAborted);
            try { (keys, var next, viewerOnly) = await service.ListPageAsync(context, cursor.Length == 0 ? null : cursor, ApiKeyService.IssuerPageSize, http.RequestAborted); hasMore = next is not null; cursor = next ?? ""; }
            catch (Exception failure) when (!http.RequestAborted.IsCancellationRequested && (failure is HttpRequestException || revealed is not null && ApiKeyService.FollowUpFailure(failure)))
            {
                if (revealed is null)
                {
                    if (error.Length == 0)
                    {
                        error = "The live credential issuer is unavailable. Try again shortly.";
                    }
                }
                else { keys = [revealed.Key]; warning = (warning + " The key change succeeded, but the complete key list is unavailable. Save this secret now; do not retry the change.").Trim(); }
            }
            try { projectCidrs = await network; }
            catch (Exception failure) when (!http.RequestAborted.IsCancellationRequested && (failure is HttpRequestException or AccountAccessException || revealed is not null && ApiKeyService.FollowUpFailure(failure)))
            {
                if (revealed is not null)
                {
                    warning = (warning + " Network settings could not be refreshed; the key change succeeded.").Trim();
                }
            }
            var now = http.RequestServices.GetRequiredService<TimeProvider>().GetUtcNow();
            await databases.SyncAsync(context, http.RequestAborted);
            try { await ApiKeyExpiryNotifications.AddAsync(http.RequestServices.GetRequiredService<AccountService>(), context, keys, now, http.RequestAborted); }
            catch (Exception failure) when (!http.RequestAborted.IsCancellationRequested && revealed is not null && ApiKeyService.FollowUpFailure(failure))
            { warning = (warning + " Expiry reminders could not be updated; the key change succeeded.").Trim(); }
            var result = ApiKeysView.Create(new(ShellModel.For(http, context, "API keys"), keys, http.Request.Query["q"].ToString(), http.Request.Query["databaseId"].ToString(), error, create.Length > 0 ? create : http.Request.Query["create"].ToString(), name, revealed, http.Request.Query["status"].ToString(), draftScopes, draftDatabaseId, DraftExpiresInDays: draftExpiresInDays, DraftAllowedCidrs: draftAllowedCidrs, ProjectCidrs: projectCidrs, Now: now, Cursor: cursor, HasMore: hasMore) { Warning = warning, DraftKeyCidrs = draftKeyCidrs, ViewerOnly = viewerOnly });
            result.StatusCode = status;
            return result;
        }
    }
}
