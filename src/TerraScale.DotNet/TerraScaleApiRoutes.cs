using System.Globalization;
using Cysharp.Text;

namespace TerraScale.DotNet;

/// <summary>A fully-bound public API route: the request path with query string.</summary>
internal readonly record struct TerraScaleBoundRoute(string RequestUri)
{
    public override string ToString() => RequestUri;
}

internal static class TerraScaleApiRoutes
{



















    public static TerraScaleBoundRoute ProjectSavedQueriesList(string projectId, int skip, int take) =>
        new TerraScaleBoundRoute(WithQuery($"/api/v1/projects/{Escape(projectId)}/saved-queries", ("skip", Number(skip)), ("take", Number(take))));
    public static TerraScaleBoundRoute ProjectSavedQueriesCreate(string projectId) =>
        new TerraScaleBoundRoute($"/api/v1/projects/{Escape(projectId)}/saved-queries");
    public static TerraScaleBoundRoute ProjectSavedQueryGet(string projectId, string savedQueryId) =>
        new TerraScaleBoundRoute($"/api/v1/projects/{Escape(projectId)}/saved-queries/{Escape(savedQueryId)}");
    public static TerraScaleBoundRoute ProjectSavedQueryUpdate(string projectId, string savedQueryId) =>
        new TerraScaleBoundRoute($"/api/v1/projects/{Escape(projectId)}/saved-queries/{Escape(savedQueryId)}");
    public static TerraScaleBoundRoute ProjectSavedQueryDelete(string projectId, string savedQueryId) =>
        new TerraScaleBoundRoute($"/api/v1/projects/{Escape(projectId)}/saved-queries/{Escape(savedQueryId)}");
    public static TerraScaleBoundRoute RegionsList() =>
        new TerraScaleBoundRoute("/api/v1/regions");

    public static TerraScaleBoundRoute RegionGet(string regionCode) =>
        new TerraScaleBoundRoute($"/api/v1/regions/{Escape(regionCode)}");















    public static TerraScaleBoundRoute DatabaseBranchesCreate(string projectId) =>
        new TerraScaleBoundRoute($"/api/v1/projects/{Escape(projectId)}/database-branches");

    public static TerraScaleBoundRoute DatabaseBranchesCapabilityGet(string projectId) =>
        new TerraScaleBoundRoute($"/api/v1/projects/{Escape(projectId)}/database-branches/capability");








    public static TerraScaleBoundRoute DocumentsList(string repositoryId, int skip, int take) =>
        new TerraScaleBoundRoute(WithQuery(
                $"/api/v1/repositories/{Escape(repositoryId)}/documents",
                ("skip", Number(skip)),
                ("take", Number(take))));

    public static TerraScaleBoundRoute DocumentsCreate(string repositoryId) =>
        new TerraScaleBoundRoute($"/api/v1/repositories/{Escape(repositoryId)}/documents");

    public static TerraScaleBoundRoute DocumentGet(string repositoryId, string documentId) =>
        new TerraScaleBoundRoute($"/api/v1/repositories/{Escape(repositoryId)}/documents/{EscapeDocumentId(documentId)}");

    public static TerraScaleBoundRoute DocumentPut(string repositoryId, string documentId) =>
        new TerraScaleBoundRoute($"/api/v1/repositories/{Escape(repositoryId)}/documents/{EscapeDocumentId(documentId)}");

    public static TerraScaleBoundRoute DocumentPatch(string repositoryId, string documentId) =>
        new TerraScaleBoundRoute($"/api/v1/repositories/{Escape(repositoryId)}/documents/{EscapeDocumentId(documentId)}");

    public static TerraScaleBoundRoute DocumentsBatch(string repositoryId) =>
        new TerraScaleBoundRoute($"/api/v1/repositories/{Escape(repositoryId)}/documents/batch");

    public static TerraScaleBoundRoute DocumentDelete(string repositoryId, string documentId) =>
        new TerraScaleBoundRoute($"/api/v1/repositories/{Escape(repositoryId)}/documents/{EscapeDocumentId(documentId)}");


    public static TerraScaleBoundRoute SchemaGet(string repositoryId) =>
        new TerraScaleBoundRoute($"/api/v1/repositories/{Escape(repositoryId)}/schema");

    public static TerraScaleBoundRoute SchemaValidate(string repositoryId) =>
        new TerraScaleBoundRoute($"/api/v1/repositories/{Escape(repositoryId)}/schema/validate");

    public static TerraScaleBoundRoute SchemaConfigure(string repositoryId) =>
        new TerraScaleBoundRoute($"/api/v1/repositories/{Escape(repositoryId)}/schema");













    public static string ApiKeysList(string projectId, string? cursor, int limit) =>
        WithQuery($"/api/v1/projects/{Escape(projectId)}/auth/api-keys", ("cursor", NormalizeOptional(cursor)), ("limit", Number(limit)));

    public static string ApiKeysCreate(string projectId) =>
        $"/api/v1/projects/{Escape(projectId)}/auth/api-keys";

    public static string ApiKeyRotate(string projectId, string keyId) =>
        $"/api/v1/projects/{Escape(projectId)}/auth/api-keys/{Escape(keyId)}/rotate";

    public static string ApiKeyRevoke(string projectId, string keyId) =>
        $"/api/v1/projects/{Escape(projectId)}/auth/api-keys/{Escape(keyId)}";




























    public static TerraScaleBoundRoute BackupPolicyConfigure(string projectId, string databaseId) =>
        new TerraScaleBoundRoute($"/api/v1/projects/{Escape(projectId)}/databases/{Escape(databaseId)}/backups/policy");

    public static TerraScaleBoundRoute BackupsPlan(string projectId, string databaseId) =>
        new TerraScaleBoundRoute($"/api/v1/projects/{Escape(projectId)}/databases/{Escape(databaseId)}/backups:plan");

    public static TerraScaleBoundRoute BackupsExecute(string projectId, string databaseId) =>
        new TerraScaleBoundRoute($"/api/v1/projects/{Escape(projectId)}/databases/{Escape(databaseId)}/backups:execute");

    public static TerraScaleBoundRoute BackupsList(string projectId, string databaseId, int skip, int take) =>
        new TerraScaleBoundRoute(WithQuery(
                $"/api/v1/projects/{Escape(projectId)}/databases/{Escape(databaseId)}/backups",
                ("skip", Number(skip)),
                ("take", Number(take))));

    public static TerraScaleBoundRoute BackupGet(string projectId, string databaseId, string backupId) =>
        new TerraScaleBoundRoute($"/api/v1/projects/{Escape(projectId)}/databases/{Escape(databaseId)}/backups/{Escape(backupId)}");

    public static TerraScaleBoundRoute RestorePointsList(string projectId, string databaseId) =>
        new TerraScaleBoundRoute($"/api/v1/projects/{Escape(projectId)}/databases/{Escape(databaseId)}/restore-points");

    public static TerraScaleBoundRoute RestoresCreate(string projectId, string databaseId) =>
        new TerraScaleBoundRoute($"/api/v1/projects/{Escape(projectId)}/databases/{Escape(databaseId)}/restores");

    public static TerraScaleBoundRoute RestoresList(string projectId, string databaseId, int skip, int take) =>
        new TerraScaleBoundRoute(WithQuery(
                $"/api/v1/projects/{Escape(projectId)}/databases/{Escape(databaseId)}/restores",
                ("skip", Number(skip)),
                ("take", Number(take))));

    public static TerraScaleBoundRoute RestoreGet(string projectId, string databaseId, string restoreId) =>
        new TerraScaleBoundRoute($"/api/v1/projects/{Escape(projectId)}/databases/{Escape(databaseId)}/restores/{Escape(restoreId)}");



    private static string WithQuery(string path, params (string Name, string? Value)[] values)
    {
        using var builder = ZString.CreateStringBuilder();
        builder.Append(path);
        var hasQuery = false;
        foreach (var (name, value) in values)
        {
            if (string.IsNullOrWhiteSpace(value))
            {
                continue;
            }

            builder.Append(hasQuery ? '&' : '?');
            builder.Append(Uri.EscapeDataString(name));
            builder.Append('=');
            builder.Append(Uri.EscapeDataString(value));
            hasQuery = true;
        }

        return builder.ToString();
    }

    private static string Escape(string value)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(value);
        return Uri.EscapeDataString(value.Trim());
    }

    private static string EscapeDocumentId(string value)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(value);
        return string.Join(
            '/',
            value
                .Trim()
                .Split('/', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .Select(Uri.EscapeDataString));
    }

    private static string? NormalizeOptional(string? value) =>
        string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    private static string Number(int value) =>
        value.ToString(CultureInfo.InvariantCulture);

    private static string? Number(double? value) =>
        value?.ToString("R", CultureInfo.InvariantCulture);

    private static string Boolean(bool value) =>
        value.ToString().ToLowerInvariant();
}
