using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.Json.Serialization.Metadata;

#pragma warning disable CS1591

namespace TerraScale.DotNet;

/// <summary>Database-per-tenant lifecycle operations.</summary>
/// <remarks>
/// D-51 is not present in the pinned TerraBase OpenAPI document. Methods
/// validate locally and then return a typed contract gap; no draft route is
/// ever requested.
/// </remarks>
public sealed class TerraScaleTenantsClient
{
    private const int MaximumTenantKeyBytes = 64;
    private const int MaximumPlacementRegions = 20;

    internal TerraScaleTenantsClient(TerraScaleClient client) => _ = client;

    public Task<TerraScaleTenant> CreateAsync(TerraScaleCreateTenantRequest request, CancellationToken cancellationToken = default)
    {
        ValidateCreate(request);
        return Gap<TerraScaleTenant>("tenants.create", cancellationToken);
    }

    public Task<TerraScaleTenant> CreateAsync(string key, string? template = null, object? placementLock = null, CancellationToken cancellationToken = default) =>
        CreateAsync(new TerraScaleCreateTenantRequest(key, template, placementLock), cancellationToken);

    public Task<TerraScaleTenant> GetAsync(string tenantKey, CancellationToken cancellationToken = default)
    {
        ValidateKey(tenantKey, nameof(tenantKey));
        return Gap<TerraScaleTenant>("tenants.get", cancellationToken);
    }

    public Task<TerraScaleTenantPage> ListAsync(int limit = 100, string? cursor = null, CancellationToken cancellationToken = default)
    {
        TerraScalePagination.ValidateLimit(limit);
        return Gap<TerraScaleTenantPage>("tenants.list", cancellationToken);
    }

    public Task<TerraScaleDeletedTenant> DeleteAsync(string tenantKey, TerraScaleDeleteTenantOptions options, CancellationToken cancellationToken = default)
    {
        ValidateKey(tenantKey, nameof(tenantKey));
        ArgumentNullException.ThrowIfNull(options);
        if (!options.Confirm) throw new ArgumentException("Tenant deletion requires Confirm to be true.", nameof(options));
        ArgumentException.ThrowIfNullOrWhiteSpace(options.IdempotencyKey, nameof(options));
        if (Encoding.UTF8.GetByteCount(options.IdempotencyKey.Trim()) > 128)
            throw new ArgumentException("Idempotency keys must be at most 128 UTF-8 bytes.", nameof(options));
        return Gap<TerraScaleDeletedTenant>("tenants.delete", cancellationToken);
    }

    public Task<TerraScaleTenantApiKey> CreateKeyAsync(string tenantKey, CancellationToken cancellationToken = default)
    {
        ValidateKey(tenantKey, nameof(tenantKey));
        return Gap<TerraScaleTenantApiKey>("tenants.keys.create", cancellationToken);
    }

    public Task<TerraScaleTenantBranch> CreateBranchAsync(string tenantKey, CancellationToken cancellationToken = default)
    {
        ValidateKey(tenantKey, nameof(tenantKey));
        return Gap<TerraScaleTenantBranch>("tenants.branches.create", cancellationToken);
    }

    private static void ValidateCreate(TerraScaleCreateTenantRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        ValidateKey(request.Key, nameof(request));
        if (request.Template is not null && string.IsNullOrWhiteSpace(request.Template))
            throw new ArgumentException("Tenant template must not be blank when supplied.", nameof(request));
        ValidateLock(request.PlacementLock, nameof(request));
    }

    private static void ValidateLock(object? value, string parameterName)
    {
        if (value is null) return;
        if (value is string lockName)
        {
            if (lockName is not ("eu-only" or "br-only"))
                throw new ArgumentException("Placement lock must be eu-only, br-only, or a region list.", parameterName);
            return;
        }
        if (value is IEnumerable<string> regions)
        {
            var list = regions.ToArray();
            if (list.Length is < 1 or > MaximumPlacementRegions)
                throw new ArgumentException($"Placement lock must contain between 1 and {MaximumPlacementRegions} regions.", parameterName);
            var seen = new HashSet<string>(StringComparer.Ordinal);
            foreach (var region in list)
            {
                TerraScaleRegionCodes.Validate(region, parameterName);
                if (!seen.Add(region)) throw new ArgumentException($"Placement lock region '{region}' is duplicated.", parameterName);
            }
            return;
        }
        throw new ArgumentException("Placement lock must be eu-only, br-only, or a region list.", parameterName);
    }

    private static void ValidateKey(string value, string parameterName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(value, parameterName);
        var key = value.Trim();
        if (key.Any(char.IsControl) || Encoding.UTF8.GetByteCount(key) > MaximumTenantKeyBytes)
            throw new ArgumentException($"Tenant keys must be printable and at most {MaximumTenantKeyBytes} UTF-8 bytes.", parameterName);
    }

    private static Task<T> Gap<T>(string operation, CancellationToken cancellationToken) =>
        cancellationToken.IsCancellationRequested
            ? Task.FromCanceled<T>(cancellationToken)
            : Task.FromException<T>(new TerraScaleContractGapException(operation));
}

public sealed record TerraScaleCreateTenantRequest(
    string Key,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? Template = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] object? PlacementLock = null);

public sealed record TerraScaleDeleteTenantOptions(bool Confirm, string IdempotencyKey);

public sealed record TerraScaleTenant(
    [property: JsonRequired] string Key,
    [property: JsonRequired] string DatabaseId,
    string? Template = null,
    object? PlacementLock = null,
    IReadOnlyList<string>? Regions = null,
    DateTimeOffset? CreatedAt = null) : IJsonOnDeserialized
{
    void IJsonOnDeserialized.OnDeserialized()
    {
        if (string.IsNullOrWhiteSpace(Key) || string.IsNullOrWhiteSpace(DatabaseId))
            throw new JsonException("Tenant responses require non-blank key and databaseId fields.");
        if (PlacementLock is string lockName && lockName is not ("eu-only" or "br-only"))
            throw new JsonException("Tenant response placementLock names an unknown residency lock.");
        if (Regions is null) return;
        if (Regions.Count is < 1 or > 20)
            throw new JsonException("Tenant response regions must contain between 1 and 20 items.");
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var region in Regions)
        {
            TerraScaleRegionCodes.ValidateResponse(region);
            if (!seen.Add(region)) throw new JsonException("Tenant response regions must not contain duplicates.");
        }
    }
}

public sealed record TerraScaleTenantPage(
    [property: JsonRequired] IReadOnlyList<TerraScaleTenant> Tenants,
    string? NextCursor = null) : IJsonOnDeserialized
{
    void IJsonOnDeserialized.OnDeserialized()
    {
        if (Tenants is null) throw new JsonException("Tenant lists require a non-null tenants field.");
    }
}

public sealed record TerraScaleDeletedTenant(
    [property: JsonRequired] string Key,
    [property: JsonRequired] string DatabaseId,
    [property: JsonRequired] bool Deleted) : IJsonOnDeserialized
{
    void IJsonOnDeserialized.OnDeserialized()
    {
        if (string.IsNullOrWhiteSpace(Key) || string.IsNullOrWhiteSpace(DatabaseId))
            throw new JsonException("Delete responses require non-blank key and databaseId fields.");
    }
}

public sealed record TerraScaleTenantApiKey(
    [property: JsonRequired] string KeyId,
    string? Token,
    [property: JsonRequired] string TenantKey) : IJsonOnDeserialized
{
    void IJsonOnDeserialized.OnDeserialized()
    {
        if (string.IsNullOrWhiteSpace(KeyId) || string.IsNullOrWhiteSpace(TenantKey))
            throw new JsonException("Tenant key responses require non-blank keyId and tenantKey fields.");
    }
}

public sealed record TerraScaleTenantBranch(
    [property: JsonRequired] string BranchId,
    [property: JsonRequired] string TenantKey,
    string? Status = null) : IJsonOnDeserialized
{
    void IJsonOnDeserialized.OnDeserialized()
    {
        if (string.IsNullOrWhiteSpace(BranchId) || string.IsNullOrWhiteSpace(TenantKey))
            throw new JsonException("Tenant branch responses require non-blank branchId and tenantKey fields.");
    }
}
