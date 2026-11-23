namespace TerraScale.Platform.Features.FileStorage;

public sealed record FileProjectOrganizationMismatch;

public readonly union CloseFileProjectResult(Done, FileProjectOrganizationMismatch, Unavailable);

public sealed class ProjectFileErasure(PlatformStore store, S3FileObjectStore objects)
{
    internal const string RegionsQuery = "SELECT region FROM platform_file_regions ORDER BY region";

    public async ValueTask<CloseFileProjectResult> CloseAsync(Ulid project, Ulid organization, CancellationToken cancellationToken)
    {
        var projectId = SqlValues.Text(project.ToString());
        var organizationId = SqlValues.Text(organization.ToString());
        const string bound = "EXISTS (SELECT 1 FROM platform_closed_file_projects WHERE project_id = ? AND organization_id = ?)";
        IReadOnlyList<SqlHttpStatement> statements =
        [
            SqlValues.Statement(
                "INSERT OR IGNORE INTO platform_closed_file_projects (project_id, organization_id) SELECT ?, ? "
                + "WHERE NOT EXISTS (SELECT 1 FROM platform_buckets WHERE project_id = ? AND organization_id <> ?)",
                projectId, organizationId, projectId, organizationId),
            SqlValues.Statement("DELETE FROM platform_files WHERE project_id = ? AND " + bound, projectId, projectId, organizationId),
            SqlValues.Statement("DELETE FROM platform_buckets WHERE project_id = ? AND " + bound, projectId, projectId, organizationId),
            SqlValues.Statement("SELECT 1 WHERE " + bound, projectId, organizationId),
            SqlValues.Statement(RegionsQuery),
        ];
        var closed = await store.ExecuteAsync(statements, SqlContext.Strong, cancellationToken);
        return closed switch
        {
            IReadOnlyList<SqlHttpStatementResult> results when results[3].Rows.Count == 0 => new FileProjectOrganizationMismatch(),
            IReadOnlyList<SqlHttpStatementResult> results =>
                ToCloseResult(await SweepAsync(project, organization, Regions(results[4]), cancellationToken)),
            Conflict conflict => new Unavailable(conflict.Message, "platform_state"),
            Unavailable unavailable => unavailable,
        };
    }

    internal async ValueTask<ProjectPrefixErasure> SweepAsync(
        Ulid project, Ulid organization, IReadOnlyList<string> regions, CancellationToken cancellationToken)
    {
        Unavailable? failure = null;
        foreach (var region in regions)
        {
            var erased = await objects.EraseProjectAsync(region, organization, project, cancellationToken);
            failure = erased switch
            {
                Done => failure,
                Unavailable unavailable => unavailable,
            };
        }

        return failure is null ? new Done() : failure;
    }

    internal static IReadOnlyList<string> Regions(SqlHttpStatementResult result) => [.. result.RowsOf().Select(static row => row.Text(0))];

    private static CloseFileProjectResult ToCloseResult(ProjectPrefixErasure erased) => erased switch
    {
        Done done => done,
        Unavailable unavailable => unavailable,
    };
}
