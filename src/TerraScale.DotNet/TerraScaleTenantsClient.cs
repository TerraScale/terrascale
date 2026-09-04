using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.Json.Serialization.Metadata;

namespace TerraScale.DotNet;

/// <summary>
/// Client for database-per-tenant lifecycle endpoints.
/// </summary>
public sealed class TerraScaleTenantsClient
{
    private const int MaximumTenantKeyBytes = 64;

    private readonly TerraScaleClient client;

    internal TerraScaleTenantsClient(TerraScaleClient client)
    {
        this.client = client;
    }

    /// <summary>
    /// Provisions a database for one tenant.
    /// </summary>
    /// <remarks>
    /// A tenant inherits placement and key policy from the selected template.
    /// Explicit regions are checked here so a client cannot submit a duplicate
    /// or non-launch region to the placement lock.
    /// </remarks>
    public Task<TerraScaleTenant> CreateAsync(
        TerraScaleCreateTenantRequest request,
        CancellationToken cancellationToken = default)
    {
        ValidateCreateRequest(request);
        return client.SendJsonAsync(
            HttpMethod.Post,
            "/api/v1/tenants",
            request,
            TypeInfo<TerraScaleCreateTenantRequest>(),
            TypeInfo<TerraScaleTenant>(),
            cancellationToken);
    }

    /// <summary>
    /// Provisions a database for one tenant using a tenant key and optional template.
    /// </summary>
    public Task<TerraScaleTenant> CreateAsync(
        string key,
        string? template = null,
        IReadOnlyList<string>? regions = null,
        CancellationToken cancellationToken = default) =>
        CreateAsync(new TerraScaleCreateTenantRequest(key, template, regions), cancellationToken);

    /// <summary>
    /// Provisions a tenant with no template or explicit placement.
    /// </summary>
    public Task<TerraScaleTenant> CreateAsync(string key, CancellationToken cancellationToken) =>
        CreateAsync(key, template: null, regions: null, cancellationToken: cancellationToken);

    /// <summary>
    /// Gets one tenant by its tenant key.
    /// </summary>
    public Task<TerraScaleTenant> GetAsync(
        string tenantKey,
        CancellationToken cancellationToken = default)
    {
        ValidateTenantKey(tenantKey, nameof(tenantKey));
        return client.SendJsonAsync(
            HttpMethod.Get,
            $"/api/v1/tenants/{Escape(tenantKey)}",
            TypeInfo<TerraScaleTenant>(),
            cancellationToken);
    }

    /// <summary>
    /// Lists tenants using the contract's opaque cursor.
    /// </summary>
    public Task<TerraScaleTenantPage> ListAsync(
        int limit = 100,
        string? cursor = null,
        CancellationToken cancellationToken = default)
    {
        TerraScalePagination.ValidateLimit(limit);
        var requestUri = $"/api/v1/tenants?limit={limit.ToString(System.Globalization.CultureInfo.InvariantCulture)}";
        if (!string.IsNullOrWhiteSpace(cursor))
        {
            requestUri += $"&cursor={Uri.EscapeDataString(cursor.Trim())}";
        }

        return client.SendJsonAsync(
            HttpMethod.Get,
            requestUri,
            TypeInfo<TerraScaleTenantPage>(),
            cancellationToken);
    }

    /// <summary>
    /// Lists tenants from the first page.
    /// </summary>
    public Task<TerraScaleTenantPage> ListAsync(CancellationToken cancellationToken) =>
        ListAsync(100, cursor: null, cancellationToken: cancellationToken);

    /// <summary>
    /// Deletes one tenant database after acknowledging its tenant key.
    /// </summary>
    public async Task<TerraScaleDeletedTenant> DeleteAsync(
        string tenantKey,
        CancellationToken cancellationToken = default)
    {
        ValidateTenantKey(tenantKey, nameof(tenantKey));
        var confirmation = tenantKey.Trim();
        try
        {
            return await client.SendAsync<TerraScaleDeletedTenant>(
                HttpMethod.Delete,
                $"/api/v1/tenants/{Escape(tenantKey)}",
                body: null,
                request => request.Headers.TryAddWithoutValidation(
                    "X-TerraScale-Delete-Confirmation",
                    confirmation),
                cancellationToken).ConfigureAwait(false);
        }
        catch (TerraScaleApiException exception) when (exception.StatusCode == System.Net.HttpStatusCode.NoContent)
        {
            // The draft contract permits a 204 response for a successful delete.
            return new TerraScaleDeletedTenant(confirmation, string.Empty, Deleted: true);
        }
    }

    /// <summary>
    /// Mints a tenant-scoped API key from the tenant template.
    /// </summary>
    public Task<TerraScaleTenantApiKey> CreateKeyAsync(
        string tenantKey,
        CancellationToken cancellationToken = default)
    {
        ValidateTenantKey(tenantKey, nameof(tenantKey));
        return client.SendJsonAsync(
            HttpMethod.Post,
            $"/api/v1/tenants/{Escape(tenantKey)}/keys",
            new TerraScaleEmptyTenantRequest(),
            TypeInfo<TerraScaleEmptyTenantRequest>(),
            TypeInfo<TerraScaleTenantApiKey>(),
            cancellationToken);
    }

    /// <summary>
    /// Mints a tenant-scoped API key. This name mirrors the management vocabulary.
    /// </summary>
    public Task<TerraScaleTenantApiKey> MintKeyAsync(
        string tenantKey,
        CancellationToken cancellationToken = default) =>
        CreateKeyAsync(tenantKey, cancellationToken);

    /// <summary>
    /// Opens a support branch for one tenant.
    /// </summary>
    public Task<TerraScaleTenantBranch> CreateBranchAsync(
        string tenantKey,
        CancellationToken cancellationToken = default)
    {
        ValidateTenantKey(tenantKey, nameof(tenantKey));
        return client.SendJsonAsync(
            HttpMethod.Post,
            $"/api/v1/tenants/{Escape(tenantKey)}/branches",
            new TerraScaleEmptyTenantRequest(),
            TypeInfo<TerraScaleEmptyTenantRequest>(),
            TypeInfo<TerraScaleTenantBranch>(),
            cancellationToken);
    }

    /// <summary>
    /// Opens a support branch for one tenant.
    /// </summary>
    public Task<TerraScaleTenantBranch> CreateSupportBranchAsync(
        string tenantKey,
        CancellationToken cancellationToken = default) =>
        CreateBranchAsync(tenantKey, cancellationToken);

    private static void ValidateCreateRequest(TerraScaleCreateTenantRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        ValidateTenantKey(request.Key, nameof(request));
        if (request.Template is not null && string.IsNullOrWhiteSpace(request.Template))
        {
            throw new ArgumentException("Tenant template must not be blank when supplied.", nameof(request));
        }

        if (request.Regions is null)
        {
            return;
        }

        if (request.Regions.Count == 0)
        {
            throw new ArgumentException("Explicit tenant placement must name at least one region.", nameof(request));
        }

        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var region in request.Regions)
        {
            TerraScaleRegionCodes.Validate(region, nameof(request));
            if (!seen.Add(region))
            {
                throw new ArgumentException(
                    $"Tenant placement region '{region}' is listed more than once.",
                    nameof(request));
            }
        }
    }

    private static void ValidateTenantKey(string tenantKey, string parameterName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(tenantKey, parameterName);
        if (Encoding.UTF8.GetByteCount(tenantKey.Trim()) > MaximumTenantKeyBytes)
        {
            throw new ArgumentException(
                $"Tenant keys must be at most {MaximumTenantKeyBytes} UTF-8 bytes.",
                parameterName);
        }
    }

    private static string Escape(string value) => Uri.EscapeDataString(value.Trim());

    private static JsonTypeInfo<T> TypeInfo<T>() =>
        TerraScaleTenantsJsonContext.Default.GetTypeInfo(typeof(T)) as JsonTypeInfo<T>
        ?? throw new InvalidOperationException($"JSON metadata for {typeof(T)} is not available in the tenants context.");
}

/// <summary>
/// Request body for provisioning one tenant database.
/// </summary>
public sealed record TerraScaleCreateTenantRequest(
    string Key,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? Template = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] IReadOnlyList<string>? Regions = null);

/// <summary>
/// A provisioned tenant database.
/// </summary>
public sealed record TerraScaleTenant(
    string Key,
    string DatabaseId,
    IReadOnlyList<string> Regions,
    DateTimeOffset? CreatedAt = null);

/// <summary>
/// A page of tenant databases.
/// </summary>
public sealed record TerraScaleTenantPage(
    IReadOnlyList<TerraScaleTenant> Tenants,
    string? NextCursor = null);

/// <summary>
/// Result of deleting a tenant database.
/// </summary>
public sealed record TerraScaleDeletedTenant(
    string Key,
    string DatabaseId,
    bool Deleted);

/// <summary>
/// A tenant-scoped API key. The token is returned only when the key is minted.
/// </summary>
public sealed record TerraScaleTenantApiKey(
    string KeyId,
    string? Token,
    string TenantKey);

/// <summary>
/// A support branch belonging to a tenant.
/// </summary>
public sealed record TerraScaleTenantBranch(
    string BranchId,
    string TenantKey,
    string? Status = null);

internal sealed record TerraScaleEmptyTenantRequest;

[JsonSourceGenerationOptions(JsonSerializerDefaults.Web)]
[JsonSerializable(typeof(TerraScaleCreateTenantRequest))]
[JsonSerializable(typeof(TerraScaleTenant))]
[JsonSerializable(typeof(TerraScaleTenantPage))]
[JsonSerializable(typeof(TerraScaleDeletedTenant))]
[JsonSerializable(typeof(TerraScaleTenantApiKey))]
[JsonSerializable(typeof(TerraScaleTenantBranch))]
[JsonSerializable(typeof(TerraScaleEmptyTenantRequest))]
internal partial class TerraScaleTenantsJsonContext : JsonSerializerContext;
