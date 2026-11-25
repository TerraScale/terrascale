using Microsoft.AspNetCore.WebUtilities;
using TerraScale.Dashboard.Common.UI;

namespace TerraScale.Dashboard.Features.Backups;

public sealed record BackupListState(string Error = "", string RestoreId = "", string Name = "", string? Through = null)
{
    private const string ErrorPurpose = "backups/list-error";

    public static BackupListState Read(HttpRequest request) =>
        new(Signed(request), request.Query["restoreId"].ToString(),
            request.Query["name"].ToString(), request.Query["through"].ToString() is { Length: > 0 } through ? through : null);

    private static string Signed(HttpRequest request)
    {
        var error = request.Query["listError"].ToString();
        return error.Length is > 0 and <= 500 && SignedNotice.Verify(request.HttpContext, ErrorPurpose, error, request.Query["listErrorSig"].ToString()) ? error : "";
    }

    public string Path(string csrfToken, AccountContext context, string path) =>
        QueryHelpers.AddQueryString(WorkspaceUrl.For(context, path), new Dictionary<string, string?>
        {
            ["listError"] = Error,
            ["listErrorSig"] = Error.Length == 0 ? "" : SignedNotice.Sign(csrfToken, ErrorPurpose, Error),
            ["restoreId"] = RestoreId,
            ["name"] = Name,
            ["through"] = Through
        });

    public BackupPage Page(HttpContext http, AccountContext context, DatabaseRecord database, BackupListing listing) =>
        new(ShellModel.For(http, context, database.Name + " · Backups"), database, listing, Error, RestoreId, Name)
        { VisibleThrough = Through };

    public RestorePointsModel Points(BackupPage page)
    {
        var backups = page.Listing!.Backups;
        var count = Math.Min(backups.Count, RestorePointsModel.PageSize);
        for (var index = count; index < backups.Count; index++)
        {
            if (backups[index].Id == Through)
            {
                count = index + 1;
                break;
            }
        }

        return new(page.Shell, page.Database, Error, RestoreId, Name, [.. backups.Take(count)],
            count < backups.Count ? backups[count - 1].Id : null)
        { State = this };
    }
}
