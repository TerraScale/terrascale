using System.Text.Json.Serialization;
using System.Text.Json;
using System.Text.Json.Serialization.Metadata;

#pragma warning disable CS1591

namespace TerraScale.DotNet;

/// <summary>Named operations backed by the pinned TerraBase contract.</summary>
public sealed class TerraScaleManagementClient
{
    public TerraScaleManagementClient(TerraScaleClient client)
    {
        ArgumentNullException.ThrowIfNull(client);
        Databases = client.Databases;
        Collections = client.Collections;
        Backups = client.Backups;
        Exports = new TerraScaleManagementExportsClient(client);
        Usage = new TerraScaleUsageClient(client);
    }

    /// <summary>Database operations in the pinned contract.</summary>
    public TerraScaleDatabasesClient Databases { get; }
    /// <summary>Database collection operations in the pinned contract.</summary>
    public TerraScaleCollectionsClient Collections { get; }
    /// <summary>Backup operations in the pinned contract.</summary>
    public TerraScaleBackupsClient Backups { get; }
    /// <summary>Export operation, pending a published success contract.</summary>
    public TerraScaleManagementExportsClient Exports { get; }
    /// <summary>Usage operation, pending a published success contract.</summary>
    public TerraScaleUsageClient Usage { get; }
}

/// <summary>Logical database export operation.</summary>
public sealed class TerraScaleManagementExportsClient
{
    internal TerraScaleManagementExportsClient(TerraScaleClient client) => _ = client;

    public Task<TerraScaleExportJob> CreateAsync(
        string databaseId,
        TerraScaleCreateExportRequest? request = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(databaseId);
        if (request is not null && request.Format is not (null or "bundle" or "jsonl"))
            throw new ArgumentException("Export format must be bundle or jsonl.", nameof(request));
        return Gap<TerraScaleExportJob>("exports.create", cancellationToken);
    }

    private static Task<T> Gap<T>(string operation, CancellationToken cancellationToken) =>
        cancellationToken.IsCancellationRequested
            ? Task.FromCanceled<T>(cancellationToken)
            : Task.FromException<T>(new TerraScaleContractGapException(operation));
}

/// <summary>Database usage operation.</summary>
public sealed class TerraScaleUsageClient
{
    internal TerraScaleUsageClient(TerraScaleClient client) => _ = client;

    public Task<TerraScaleDatabaseUsage> GetAsync(string databaseId, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(databaseId);
        return Gap<TerraScaleDatabaseUsage>("usage.database.get", cancellationToken);
    }

    private static Task<T> Gap<T>(string operation, CancellationToken cancellationToken) =>
        cancellationToken.IsCancellationRequested
            ? Task.FromCanceled<T>(cancellationToken)
            : Task.FromException<T>(new TerraScaleContractGapException(operation));
}

/// <summary>Raised when an operation has no published TerraBase contract.</summary>
public sealed class TerraScaleContractGapException : NotSupportedException
{
    public TerraScaleContractGapException(string operation)
        : base($"TerraScale operation '{operation}' is not present in the pinned public contract.")
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(operation);
        Operation = operation.Trim();
    }

    /// <summary>Missing operation identity.</summary>
    public string Operation { get; }
    /// <summary>Stable machine-readable code.</summary>
    public string Code => "contract_gap";
    /// <summary>Contract artifact checked by this SDK.</summary>
    public string Contract => "terrabase/openapi.json";
}

public sealed record TerraScaleCreateExportRequest(
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? Format = null,
    bool IncludeHistory = true);

public sealed record TerraScaleExportJob(
    [property: JsonRequired] string ExportId,
    [property: JsonRequired] string DatabaseId,
    string? Format = null,
    string? Status = null,
    double? Progress = null,
    string? DownloadUrl = null) : IJsonOnDeserialized
{
    void IJsonOnDeserialized.OnDeserialized()
    {
        if (string.IsNullOrWhiteSpace(ExportId) || string.IsNullOrWhiteSpace(DatabaseId))
            throw new JsonException("Export responses require non-blank exportId and databaseId fields.");
    }
}

/// <summary>Usage facts returned by the usage contract.</summary>
public sealed record TerraScaleDatabaseUsage(
    [property: JsonRequired] long Writes,
    [property: JsonRequired] long StorageBytes,
    string? PeriodStart = null,
    string? PeriodEnd = null) : IJsonOnDeserialized
{
    void IJsonOnDeserialized.OnDeserialized()
    {
        if (Writes < 0 || StorageBytes < 0)
            throw new JsonException("Usage response counters must not be negative.");
    }
}
