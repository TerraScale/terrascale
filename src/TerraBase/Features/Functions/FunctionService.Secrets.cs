using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using TerraBase.Engine;
using TerraBase.Errors;
using TerraBase.Features.Management;
using TerraBase.Tenancy;

namespace TerraBase.Features.Functions;

internal sealed partial class FunctionService
{
    private const int MaxSecrets = 50;
    private const int MaxSecretBytes = 4 * 1024;

    public async ValueTask<FunctionResult> ListSecretsAsync(TenantContext tenant, string name, CancellationToken cancellationToken) =>
        await catalog.ReadAsync<FunctionResult>(async transaction =>
            await GetAsync(transaction, tenant, name, cancellationToken).ConfigureAwait(false) is null
                ? new NotFound()
                : (await SystemCatalog.ListAllAsync(transaction, Secrets(tenant, name), FunctionsJsonContext.Default.FunctionSecretRecord, cancellationToken)
                    .ConfigureAwait(false)).Select(Body).OrderBy(secret => secret.Name, StringComparer.Ordinal).ToArray(),
            static error => error, cancellationToken).ConfigureAwait(false);

    public async ValueTask<FunctionResult> SetSecretAsync(TenantContext tenant, string name, string secret, SetFunctionSecretRequest request,
        CancellationToken cancellationToken)
    {
        if (SecretName(secret) is { } invalid)
        {
            return invalid;
        }

        if (request.Value is not { } value || Encoding.UTF8.GetByteCount(value) is 0 or > MaxSecretBytes)
        {
            return new Validation("value is required and at most 4 KiB");
        }

        if (!secrets.Available)
        {
            return new Unavailable("function secrets need the database key store");
        }

        var sealedValue = await secrets.SealAsync(tenant, name, secret, value, cancellationToken).ConfigureAwait(false);
        var record = new FunctionSecretRecord(1, secret, sealedValue, time.GetUtcNow());
        return await catalog.WriteDurableAsync<FunctionResult>(async transaction =>
        {
            if (await CatalogTables.DatabaseAsync(transaction, tenant.ProjectId!, tenant.DatabaseId!, cancellationToken).ConfigureAwait(false) is null
                || await GetAsync(transaction, tenant, name, cancellationToken).ConfigureAwait(false) is null)
            {
                return new NotFound();
            }

            var kind = Secrets(tenant, name);
            if (await transaction.GetAsync(kind, secret, cancellationToken).ConfigureAwait(false) is null
                && (await transaction.ListAsync(kind, null, MaxSecrets, 1024 * 1024, cancellationToken).ConfigureAwait(false)).Records.Count >= MaxSecrets)
            {
                return new Conflict($"at most {MaxSecrets} secrets may be set per function", "function_secret_limit");
            }

            await SystemCatalog.PutAsync(transaction, kind, secret, record, FunctionsJsonContext.Default.FunctionSecretRecord, cancellationToken)
                .ConfigureAwait(false);
            return Body(record);
        }, static error => error, cancellationToken).ConfigureAwait(false);
    }

    public async ValueTask<FunctionResult> DeleteSecretAsync(TenantContext tenant, string name, string secret, CancellationToken cancellationToken)
    {
        if (SecretName(secret) is { } invalid)
        {
            return invalid;
        }

        return await catalog.WriteDurableAsync<FunctionResult>(async transaction =>
            await GetAsync(transaction, tenant, name, cancellationToken).ConfigureAwait(false) is null
                ? new NotFound()
                : await transaction.DeleteAsync(Secrets(tenant, name), secret, cancellationToken).ConfigureAwait(false)
                    ? new Done()
                    : new NotFound("secret not found"),
            static error => error, cancellationToken).ConfigureAwait(false);
    }

    private async ValueTask<FunctionLoad> LoadAsync(TenantContext tenant, string name, CancellationToken cancellationToken) =>
        await catalog.ReadAsync<FunctionLoad>(async transaction =>
            await GetAsync(transaction, tenant, name, cancellationToken).ConfigureAwait(false) is { } record
                ? new FunctionCode(record.Source, await SystemCatalog.ListAllAsync(transaction, Secrets(tenant, name),
                    FunctionsJsonContext.Default.FunctionSecretRecord, cancellationToken).ConfigureAwait(false))
                : new NotFound(),
            static error => error, cancellationToken).ConfigureAwait(false);

    private async ValueTask<string> OpenSecretsAsync(TenantContext tenant, string name, IReadOnlyList<FunctionSecretRecord> stored,
        CancellationToken cancellationToken)
    {
        var values = new Dictionary<string, string>(stored.Count, StringComparer.Ordinal);
        foreach (var record in stored)
        {
            values[record.Name] = await secrets.OpenAsync(tenant, name, record.Name, record.Sealed, cancellationToken).ConfigureAwait(false);
        }

        return JsonSerializer.Serialize<IReadOnlyDictionary<string, string>>(values, FunctionsJsonContext.Default.IReadOnlyDictionaryStringString);
    }

    private static Validation? SecretName(string secret) =>
        SecretNamePattern().IsMatch(secret) ? null : new Validation("secret name must match ^[A-Z_][A-Z0-9_]{0,63}$");

    private static FunctionSecretBody Body(FunctionSecretRecord record) => new(record.Name, record.UpdatedAt);

    private static string Secrets(TenantContext tenant, string name) => SystemCatalog.DatabaseState(tenant.DatabaseId!) + "function_secrets/" + name;

    [GeneratedRegex(@"\A[A-Z_][A-Z0-9_]{0,63}\z")]
    private static partial Regex SecretNamePattern();
}
