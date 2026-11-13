using System.Diagnostics;
using Microsoft.Extensions.Caching.Memory;
using TerraBase.Engine;
using TerraBase.Errors;
using TerraBase.Features.FileStorage;
using TerraBase.Features.Management;
using TerraBase.Tenancy;

namespace TerraBase.Features.Qos;

internal sealed class StorageQuota : IStorageQuota, IDisposable
{
    internal static readonly Unavailable AccountingUnavailable = new("storage accounting is at capacity; retry later", "storage_accounting_busy");

    internal static Unavailable Failure(TenantPlanResult plan) => plan switch
    {
        PlanTier => throw new UnreachableException(),
        Unavailable error => error,
    };

    internal static Unavailable Failure(RegisteredDatabaseResult registered) => registered switch
    {
        bool => throw new UnreachableException(),
        Unavailable error => error,
    };

    private readonly SemaphoreSlim[] _gates = [.. Enumerable.Range(0, 256).Select(_ => new SemaphoreSlim(1, 1))];
    private readonly MemoryCache _usage = new(new MemoryCacheOptions { SizeLimit = 1024 });
    private readonly IStorageEngine _engine;
    private readonly ITenantDirectory _directory;
    private readonly SystemCatalog _catalog;
    private readonly Func<PlanTier, long> _limit;

    public StorageQuota(IStorageEngine engine, ITenantDirectory directory, SystemCatalog catalog) : this(engine, directory, catalog, plan => QosPolicy.For(plan).StorageBytes) { }
    private StorageQuota(IStorageEngine engine, ITenantDirectory directory, SystemCatalog catalog, Func<PlanTier, long> limit)
    {
        _engine = engine;
        _directory = directory;
        _catalog = catalog;
        _limit = limit;
    }

    public async ValueTask<StorageQuotaEntry> EnterAsync(TenantContext database, IDatabaseStore store, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(database);
        ArgumentNullException.ThrowIfNull(store);
        var currentPlan = await _directory.PlanAsync(database.OrganizationId, cancellationToken).ConfigureAwait(false);
        if (currentPlan is not PlanTier plan)
        {
            return Failure(currentPlan);
        }

        if (_limit(plan) == long.MaxValue)
        {
            return new StorageQuotaLease(null, long.MaxValue, _ => ValueTask.FromResult<StorageQuotaBytesResult>(0L), _ => { });
        }

        var gate = _gates[Stripe(database.OrganizationId)];
        await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var checkedPlan = await _directory.PlanAsync(database.OrganizationId, cancellationToken).ConfigureAwait(false);
            if (checkedPlan is not PlanTier livePlan)
            {
                gate.Release();
                return Failure(checkedPlan);
            }

            var limit = _limit(livePlan);
            if (limit == long.MaxValue)
            {
                var unrestricted = new StorageQuotaLease(null, long.MaxValue, _ => ValueTask.FromResult<StorageQuotaBytesResult>(0L), _ => { });
                gate.Release();
                return unrestricted;
            }

            _usage.TryGetValue(store.Incarnation, out Usage? current);
            if (current?.Sequence != store.CommitSequence)
            {
                current = null;
            }

            return new StorageQuotaLease(gate, limit, async token =>
            {
                current = await UsageAsync(store, token).ConfigureAwait(false);
                var used = checked(current.Bytes + await FileBytesAsync(database.OrganizationId, token).ConfigureAwait(false));
                return await SumRegisteredBytesAsync(database.OrganizationId, used, database, token).ConfigureAwait(false);
            }, delta =>
            {
                var committedSequence = store.CommitSequence;
                if (current is not null && committedSequence == current.Sequence + 1)
                {
                    Remember(store, new Usage(committedSequence, checked(current.Bytes + delta)));
                }
            });
        }
        catch
        {
            gate.Release();
            throw;
        }
    }

    public async ValueTask<StorageQuotaGroupEntry> EnterOrganizationsAsync(IReadOnlyList<string> organizationIds, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(organizationIds);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(organizationIds.Count, 16);
        SemaphoreSlim[] gates = [.. organizationIds.Select(Stripe).Distinct().Order().Select(index => _gates[index])];
        var acquired = 0;
        try
        {
            foreach (var gate in gates)
            {
                await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
                acquired++;
            }

            var limits = new Dictionary<string, long>(StringComparer.Ordinal);
            foreach (var organization in organizationIds.Distinct(StringComparer.Ordinal))
            {
                var checkedPlan = await _directory.PlanAsync(organization, cancellationToken).ConfigureAwait(false);
                if (checkedPlan is not PlanTier plan)
                {
                    for (var i = acquired - 1; i >= 0; i--)
                    {
                        gates[i].Release();
                    }

                    return Failure(checkedPlan);
                }

                limits.Add(organization, _limit(plan));
            }

            return new StorageQuotaGroupLease(gates, limits, OrganizationUsageAsync);
        }
        catch
        {
            for (var i = acquired - 1; i >= 0; i--)
            {
                gates[i].Release();
            }

            throw;
        }
    }

    public async ValueTask<long> ReadBytesAsync(TenantContext database, CancellationToken cancellationToken)
    {
        var store = await _engine.OpenDatabaseAsync(database, cancellationToken).ConfigureAwait(false)
            ?? throw new InvalidOperationException("the staged database no longer exists");
        return (await UsageAsync(store, cancellationToken).ConfigureAwait(false)).Bytes;
    }

    public async ValueTask<OrganizationBytesResult> ReadOrganizationsAsync(CancellationToken cancellationToken)
    {
        var bytes = new Dictionary<string, long>(StringComparer.Ordinal);
        await foreach (var candidate in _engine.ListDatabasesAsync(cancellationToken).ConfigureAwait(false))
        {
            var walked = await RegisteredBytesAsync(candidate, cancellationToken).ConfigureAwait(false);
            if (walked is Unavailable error)
            {
                return error;
            }

            if (walked is long used)
            {
                bytes[candidate.OrganizationId] = checked(bytes.GetValueOrDefault(candidate.OrganizationId) + used);
            }
        }

        return bytes;
    }

    private int Stripe(string organizationId) => (int)((uint)StringComparer.Ordinal.GetHashCode(organizationId) % (uint)_gates.Length);

    private ValueTask<long> FileBytesAsync(string organizationId, CancellationToken cancellationToken) =>
        new FileStorageUsage(_engine).BytesAsync(organizationId, null, cancellationToken);

    private async ValueTask<StorageQuotaBytesResult> OrganizationUsageAsync(string organizationId, CancellationToken cancellationToken) =>
        await SumRegisteredBytesAsync(organizationId, await FileBytesAsync(organizationId, cancellationToken).ConfigureAwait(false), null, cancellationToken).ConfigureAwait(false);

    private async ValueTask<StorageQuotaBytesResult> SumRegisteredBytesAsync(string organizationId, long used, TenantContext? exclude, CancellationToken cancellationToken)
    {
        await foreach (var candidate in _engine.ListDatabasesAsync(cancellationToken).ConfigureAwait(false))
        {
            if (candidate.OrganizationId != organizationId
                || exclude is not null && candidate.ProjectId == exclude.ProjectId && candidate.DatabaseId == exclude.DatabaseId)
            {
                continue;
            }

            var walked = await RegisteredBytesAsync(candidate, cancellationToken).ConfigureAwait(false);
            if (walked is Unavailable error)
            {
                return error;
            }

            if (walked is long bytes)
            {
                used = checked(used + bytes);
            }
        }

        return used;
    }

    private async ValueTask<StorageQuotaBytesResult?> RegisteredBytesAsync(TenantContext candidate, CancellationToken cancellationToken)
    {
        var registered = await _directory.RegisteredAsync(candidate, cancellationToken).ConfigureAwait(false);
        if (registered is not bool known)
        {
            return Failure(registered);
        }

        if (!known)
        {
            var retained = await _catalog.ReadAsync<RegisteredDatabaseResult>(async transaction =>
                await SystemCatalog.GetAsync(transaction, DeletedDatabases.Kind, candidate.DatabaseId!, ManagementJsonContext.Default.DeletedDatabaseRecord,
                    cancellationToken).ConfigureAwait(false) is { } record && DatabasePurge.SameDatabase(record.Storage, candidate),
                static error => error, cancellationToken).ConfigureAwait(false);
            if (retained is Unavailable error)
            {
                return error;
            }
            if (retained is false)
            {
                return null;
            }
        }

        return await _engine.OpenDatabaseAsync(candidate, cancellationToken).ConfigureAwait(false) is { } store
            ? (await UsageAsync(store, cancellationToken).ConfigureAwait(false)).Bytes
            : null;
    }

    private async ValueTask<Usage> UsageAsync(IDatabaseStore store, CancellationToken cancellationToken)
    {
        var sequence = store.CommitSequence;
        if (_usage.TryGetValue(store.Incarnation, out Usage? cached) && cached!.Sequence == sequence)
        {
            return cached;
        }
        var transaction = await store.BeginTransactionAsync(write: false, cancellationToken).ConfigureAwait(false);
        await using (transaction.ConfigureAwait(false))
        {
            var bytes = await transaction.GetStoredBytesAsync(cancellationToken).ConfigureAwait(false);
            var usage = new Usage(sequence, bytes);
            if (store.CommitSequence == sequence)
            {
                Remember(store, usage);
            }

            return usage;
        }
    }

    private void Remember(IDatabaseStore store, Usage usage) =>
        _usage.Set(store.Incarnation, usage, new MemoryCacheEntryOptions { Size = 1, SlidingExpiration = TimeSpan.FromMinutes(5) });

    public void Dispose()
    {
        _usage.Dispose();
        foreach (var gate in _gates)
        {
            gate.Dispose();
        }
    }

    private sealed record Usage(long Sequence, long Bytes);
}
