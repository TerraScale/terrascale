using TerraBase.Engine;
using TerraBase.Errors;
using TerraBase.Tenancy;

namespace TerraBase.Features.Management;

public sealed record DeletedDatabaseBody(string Id, string ProjectId, string Name, DateTimeOffset DeletedAt, DateTimeOffset PurgeAt);

public readonly union ListDeletedDatabasesResult(ManagementPage<DeletedDatabaseBody>, NotFound, Unavailable);

internal sealed record DeletedDatabaseRecord(TenantContext Storage, ManagedDatabase Database, DateTimeOffset DeletedAt, DateTimeOffset PurgeAt)
{
    public DeletedDatabaseBody Body => new(Database.Id, Database.ProjectId, Database.Name, DeletedAt, PurgeAt);
}

internal static class DeletedDatabases
{
    public const string Kind = "deleted_databases";

    public static async ValueTask RemoveAsync(IStorageTransaction transaction, DeletedDatabaseRecord record, CancellationToken token)
    {
        _ = await transaction.DeleteAsync(Kind, record.Database.Id, token).ConfigureAwait(false);
        _ = await transaction.DeleteAsync(Project(record.Database.ProjectId), record.Database.Id, token).ConfigureAwait(false);
    }

    public static async ValueTask<bool> IsRetainedAsync(IStorageTransaction transaction, string databaseId, CancellationToken token) =>
        await transaction.GetAsync(Kind, databaseId, token).ConfigureAwait(false) is not null;

    public static string Project(string projectId) => Kind + "/" + projectId;
}
