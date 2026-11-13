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

    public static string Project(string projectId) => Kind + "/" + projectId;
}
