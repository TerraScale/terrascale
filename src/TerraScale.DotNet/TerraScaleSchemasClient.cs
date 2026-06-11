namespace TerraScale.DotNet;

/// <summary>
/// Client for repository schema endpoints.
/// </summary>
public sealed class TerraScaleSchemasClient
{
    private readonly TerraScaleClient client;

    internal TerraScaleSchemasClient(TerraScaleClient client)
    {
        this.client = client;
    }

    /// <summary>
    /// Gets the schema configured for a repository.
    /// </summary>
    public Task<TerraScaleRepositorySchema> GetAsync(
        string repositoryId,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(repositoryId);
        return client.SendAsync<TerraScaleRepositorySchema>(
            HttpMethod.Get,
            $"/api/v1/repositories/{Escape(repositoryId)}/schema",
            body: null,
            cancellationToken);
    }

    /// <summary>
    /// Configures schema validation for a repository.
    /// </summary>
    public Task<TerraScaleRepositorySchema> ConfigureAsync(
        string repositoryId,
        bool enabled,
        string? schema,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(repositoryId);
        return client.SendAsync<TerraScaleRepositorySchema>(
            HttpMethod.Put,
            $"/api/v1/repositories/{Escape(repositoryId)}/schema",
            new TerraScaleConfigureRepositorySchemaRequest(enabled, NormalizeOptional(schema)),
            cancellationToken);
    }

    /// <summary>
    /// Starts repository schema validation.
    /// </summary>
    public Task<TerraScaleSchemaValidationOperation> ValidateAsync(
        string repositoryId,
        int? maxErrorMessages = null,
        long? maxDocumentsToValidate = null,
        long? startEtag = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(repositoryId);
        return client.SendAsync<TerraScaleSchemaValidationOperation>(
            HttpMethod.Post,
            $"/api/v1/repositories/{Escape(repositoryId)}/schema/validate",
            new TerraScaleValidateRepositorySchemaRequest(maxErrorMessages, maxDocumentsToValidate, startEtag),
            cancellationToken);
    }

    private static string Escape(string value) =>
        Uri.EscapeDataString(value.Trim());

    private static string? NormalizeOptional(string? value) =>
        string.IsNullOrWhiteSpace(value) ? null : value.Trim();
}

internal sealed record TerraScaleConfigureRepositorySchemaRequest(
    bool Enabled,
    string? Schema);

internal sealed record TerraScaleValidateRepositorySchemaRequest(
    int? MaxErrorMessages,
    long? MaxDocumentsToValidate,
    long? StartEtag);

/// <summary>
/// Repository schema configuration returned by TerraScale.
/// </summary>
public sealed record TerraScaleRepositorySchema(
    string RepositoryId,
    string Repository,
    string DatabaseId,
    string? EnvironmentId,
    string Collection,
    bool Enabled,
    string Schema);

/// <summary>
/// Schema validation operation started by TerraScale.
/// </summary>
public sealed record TerraScaleSchemaValidationOperation(
    string RepositoryId,
    string Repository,
    string DatabaseId,
    string? EnvironmentId,
    string Collection,
    long OperationId,
    string? OperationNodeTag,
    string? ResponsibleNode);
