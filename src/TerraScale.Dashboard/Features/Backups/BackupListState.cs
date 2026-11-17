using Microsoft.AspNetCore.WebUtilities;
using TerraScale.Dashboard.Common.UI;

namespace TerraScale.Dashboard.Features.Backups;

public sealed record BackupListState(string Error = "", string RestoreId = "", string Name = "", string? Through = null)
{
    public static BackupListState Read(HttpRequest request) =>
        new(request.Query["listError"].ToString(), request.Query["restoreId"].ToString(),
            request.Query["name"].ToString(), request.Query["through"].ToString() is { Length: > 0 } through ? through : null);

    public string Path(AccountContext context, string path) =>
        QueryHelpers.AddQueryString(WorkspaceUrl.For(context, path), new Dictionary<string, string?>
        {
            ["listError"] = Error,
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
