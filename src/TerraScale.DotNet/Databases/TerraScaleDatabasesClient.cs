using System.Text.Json;
using System.Text.Json.Serialization;
using TerraScale.DotNet.Generated;

#pragma warning disable CS1591

namespace TerraScale.DotNet;

/// <summary>
/// Client for TerraScale database endpoints.
/// </summary>
public sealed class TerraScaleDatabasesClient
{
    private readonly TerraScaleClient client;

    internal TerraScaleDatabasesClient(TerraScaleClient client)
    {
        this.client = client;
    }

    /// <summary>
    /// Lists databases visible to the current credentials.
    /// </summary>
    public async Task<IReadOnlyList<TerraScaleDatabaseRecord>> ListAsync(
        int skip = 0,
        int take = 100,
        CancellationToken cancellationToken = default)
    {
        TerraScalePagination.Validate(skip, take);
        var response = await client.SendAsync<TerraScaleDatabaseListResponse>(
            HttpMethod.Get,
            "/api/v1/databases",
            body: null,
            cancellationToken).ConfigureAwait(false);
        return response.Databases;
    }

    /// <summary>Creates a database with explicit placement and residency.</summary>
    public Task<TerraScaleDatabaseCreationResult> CreateAsync(
        string name,
        IReadOnlyList<string> placement,
        string operationId,
        string residency,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        ArgumentNullException.ThrowIfNull(placement);
        ArgumentException.ThrowIfNullOrWhiteSpace(operationId);
        ArgumentException.ThrowIfNullOrWhiteSpace(residency);
        var normalizedPlacement = placement.Select(region => TerraScaleRegionCodes.Validate(region, nameof(placement))).ToArray();
        if (normalizedPlacement.Length is < 1 or > 20)
        {
            throw new ArgumentException("Placement must contain between 1 and 20 canonical TerraScale regions.", nameof(placement));
        }
        if (normalizedPlacement.Distinct(StringComparer.Ordinal).Count() != normalizedPlacement.Length)
            throw new ArgumentException("Placement cannot contain duplicate regions.", nameof(placement));

        string[] allowedRegions;
        if (residency == "eu-only")
            allowedRegions = [TerraScaleRegions.Fra];
        else if (residency == "br-only")
            allowedRegions = [TerraScaleRegions.Gru];
        else
        {
            allowedRegions = residency.Split(',', StringSplitOptions.TrimEntries);
            if (allowedRegions.Length == 0 || allowedRegions.Any(region => !TerraScaleRegionCodes.IsKnown(region)))
                throw new ArgumentException("Residency must be eu-only, br-only, or a comma-separated canonical region list.", nameof(residency));
            if (allowedRegions.Distinct(StringComparer.Ordinal).Count() != allowedRegions.Length)
                throw new ArgumentException("Residency cannot contain duplicate regions.", nameof(residency));
            residency = string.Join(',', allowedRegions);
        }
        if (normalizedPlacement.Any(region => !allowedRegions.Contains(region, StringComparer.Ordinal)))
            throw new ArgumentException("Placement must stay within its residency lock.", nameof(placement));
        return client.CreateDatabaseAsync(new CreateDatabaseRequest(Name: name.Trim(), Placement: normalizedPlacement, Residency: residency, OperationId: operationId), operationId, cancellationToken);
    }

    /// <summary>
    /// Gets database details by TerraScale database id.
    /// </summary>
    public Task<TerraScaleDatabaseRecord> GetAsync(
        string databaseId,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(databaseId);
        return client.SendAsync<TerraScaleDatabaseRecord>(
            HttpMethod.Get,
            $"/api/v1/databases/{Escape(databaseId)}",
            body: null,
            cancellationToken);
    }

    /// <summary>Gets the direct TerraBase record by database name.</summary>
    public Task<TerraScaleDatabaseRecord> GetRecordAsync(string database, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(database);
        return client.SendAsync<TerraScaleDatabaseRecord>(HttpMethod.Get, $"/api/v1/databases/{Escape(database)}", body: null, cancellationToken);
    }

    /// <summary>
    /// Deletes a TerraScale database after explicit acknowledgement of its id.
    /// </summary>
    public Task<DeletionOperation> DeleteAsync(
        string databaseId,
        TerraScaleDeleteDatabaseOptions options,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(databaseId);
        ArgumentNullException.ThrowIfNull(options);
        var database = databaseId.Trim();
        var confirmation = options.Confirmation?.Trim();
        if (!string.Equals(database, confirmation, StringComparison.Ordinal))
        {
            throw new ArgumentException(
                "The deletion confirmation must exactly match the target database id.",
                nameof(options));
        }

        var operationId = ValidateOperationId(options.IdempotencyKey, nameof(options));
        return client.SendAsync<DeletionOperation, DeletionOperation>(
            HttpMethod.Delete,
            $"/api/v1/databases/{Escape(database)}",
            body: null,
            preferredRegion: null,
            request => request.Headers.TryAddWithoutValidation(
                "X-TerraScale-Delete-Confirmation",
                database),
            (operation, response) => ValidateAcceptedDeletion(operation, response, database, operationId),
            cancellationToken,
            idempotencyKeyOverride: operationId,
            allowUnkeyedPutRetry: false,
            expectedStatusCode: System.Net.HttpStatusCode.Accepted);
    }

    /// <summary>Reads durable deletion progress without resubmitting the deletion.</summary>
    public Task<DeletionOperation> GetDeletionStatusAsync(
        string database,
        string operationId,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(database);
        var normalizedDatabase = database.Trim();
        var normalizedOperationId = ValidateOperationId(operationId, nameof(operationId));
        return client.SendAsync<DeletionOperation, DeletionOperation>(
            HttpMethod.Get,
            $"/api/v1/databases/{Escape(normalizedDatabase)}/deletions/{Escape(normalizedOperationId)}",
            body: null,
            preferredRegion: null,
            configureRequest: null,
            (operation, response) => ValidateDeletionStatus(operation, response, normalizedDatabase, normalizedOperationId),
            cancellationToken,
            idempotencyKeyOverride: null,
            expectedStatusCode: System.Net.HttpStatusCode.OK);
    }

    /// <summary>Restores a soft-deleted database within the retention window.</summary>
    public Task<TerraScaleDatabaseRecord> RestoreAsync(string database, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(database);
        return client.SendAsync<TerraScaleDatabaseRecord>(HttpMethod.Post, $"/api/v1/databases/{Escape(database)}/restore", body: null, cancellationToken);
    }

    /// <summary>Lists branches whose parent is the named database.</summary>
    public async Task<IReadOnlyList<DatabaseRecord>> ListBranchesAsync(
        string database,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(database);
        var response = await client.SendJsonAsync(
            HttpMethod.Get,
            $"/api/v1/databases/{Escape(database)}/branches",
            TerraScaleContractJsonContext.Default.BranchListResponse,
            cancellationToken).ConfigureAwait(false);
        return response.Branches;
    }

    /// <summary>Creates a branch from a named database at an optional history point.</summary>
    public Task<DatabaseRecord> CreateBranchAsync(
        string database,
        RestoreRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(database);
        ArgumentNullException.ThrowIfNull(request);
        ArgumentException.ThrowIfNullOrWhiteSpace(request.Name);
        return client.SendJsonAsync(
            HttpMethod.Post,
            $"/api/v1/databases/{Escape(database)}/branches",
            request,
            TerraScaleContractJsonContext.Default.RestoreRequest,
            TerraScaleContractJsonContext.Default.DatabaseRecord,
            cancellationToken);
    }

    private static string Escape(string value) =>
        Uri.EscapeDataString(value.Trim());

    private static DeletionOperation ValidateAcceptedDeletion(
        DeletionOperation operation,
        HttpResponseMessage response,
        string database,
        string operationId)
    {
        ValidateDeletionIdentity(operation, database, operationId, "accepted database deletion");
        var location = response.Headers.TryGetValues("Location", out var values)
            ? values.ToArray()
            : [];
        if (location.Length != 1 || string.IsNullOrWhiteSpace(location[0]))
        {
            throw new JsonException("Accepted database deletion requires a single Location header.");
        }

        return operation;
    }

    private static DeletionOperation ValidateDeletionStatus(
        DeletionOperation operation,
        HttpResponseMessage response,
        string database,
        string operationId)
    {
        ValidateDeletionIdentity(operation, database, operationId, "deletion status");
        return operation;
    }

    private static void ValidateDeletionIdentity(
        DeletionOperation operation,
        string database,
        string operationId,
        string context)
    {
        if (operation is null || operation.Request is null ||
            string.IsNullOrWhiteSpace(operation.Request.Environment) ||
            string.IsNullOrWhiteSpace(operation.Request.TenantId) ||
            string.IsNullOrWhiteSpace(operation.Request.ProjectId) ||
            string.IsNullOrWhiteSpace(operation.Request.DatabaseId) ||
            string.IsNullOrWhiteSpace(operation.Request.OperationId) ||
            string.IsNullOrWhiteSpace(operation.RequestedAt) ||
            string.IsNullOrWhiteSpace(operation.Cutoff) ||
            operation.Targets is null or { Count: < 1 or > 3000 } ||
            !string.Equals(operation.Request.DatabaseId, database, StringComparison.Ordinal) ||
            !string.Equals(operation.Request.OperationId, operationId, StringComparison.Ordinal))
        {
            throw new JsonException($"The {context} response does not match the requested database or operation identity.");
        }

        if (operation.State is not ("preparing" or "stopping" or "edge_pending" or "cleanup_pending"))
        {
            throw new JsonException($"The {context} response contains an unsupported deletion state.");
        }

        foreach (var target in operation.Targets)
        {
            if (target is null || target.Stop is null ||
                string.IsNullOrWhiteSpace(target.Stop.DatabaseId) ||
                string.IsNullOrWhiteSpace(target.Stop.Environment) ||
                string.IsNullOrWhiteSpace(target.Stop.OrganizationId) ||
                string.IsNullOrWhiteSpace(target.Stop.OperationId) ||
                string.IsNullOrWhiteSpace(target.Stop.ProjectId) ||
                string.IsNullOrWhiteSpace(target.Stop.Region) ||
                string.IsNullOrWhiteSpace(target.Stop.Cutoff) ||
                !string.Equals(target.Stop.Environment, operation.Request.Environment, StringComparison.Ordinal) ||
                !string.Equals(target.Stop.OrganizationId, operation.Request.TenantId, StringComparison.Ordinal) ||
                !string.Equals(target.Stop.ProjectId, operation.Request.ProjectId, StringComparison.Ordinal) ||
                !string.Equals(target.Stop.OperationId, operation.Request.OperationId, StringComparison.Ordinal))
            {
                throw new JsonException($"The {context} response contains an inconsistent deletion target.");
            }
        }
    }

    private static string ValidateOperationId(string operationId, string parameterName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(operationId, parameterName);
        var normalized = operationId.Trim();
        if (System.Text.Encoding.UTF8.GetByteCount(normalized) > 128 ||
            normalized.Any(character => character is not (>= 'a' and <= 'z' or >= 'A' and <= 'Z' or >= '0' and <= '9' or '-' or '_')))
        {
            throw new ArgumentException(
                "Operation identity must contain 1 to 128 ASCII letters, digits, hyphens, or underscores.",
                parameterName);
        }

        return normalized;
    }
}

internal sealed record TerraScaleDatabaseListResponse([property: JsonRequired] IReadOnlyList<TerraScaleDatabaseRecord> Databases);
/// <summary>Creation can finish synchronously or accept durable provisioning work.</summary>
public abstract record TerraScaleDatabaseCreationResult;
/// <summary>A development database was created synchronously.</summary>
public sealed record TerraScaleDatabaseCreated(DatabaseRecord Database) : TerraScaleDatabaseCreationResult;
/// <summary>Durable provisioning was accepted; this does not imply serving readiness.</summary>
public sealed record TerraScaleDatabaseAccepted(ProvisioningOperation Operation) : TerraScaleDatabaseCreationResult;

/// <summary>Direct database record published by TerraBase.</summary>
public sealed record TerraScaleDatabaseRecord(
    [property: JsonRequired] string Name,
    [property: JsonRequired] string Placement,
    [property: JsonRequired] string Residency,
    [property: JsonRequired] bool Deleted,
    [property: JsonRequired, JsonPropertyName("created_at")] long CreatedAt,
    string? Parent);

#pragma warning restore CS1591
