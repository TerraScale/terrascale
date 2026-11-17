using System.Security.Cryptography;
using System.Text.Json;
using TerraBase.Engine;
using TerraBase.Errors;
using TerraBase.Features.Qos;
using TerraBase.Features.Shapes;
using TerraBase.Primitives;
using TerraBase.Tenancy;

namespace TerraBase.Features.Rules;

internal sealed class RulesService(IStorageEngine engine, IShapeStorage storage, ReplicaShapeTails replicas, TimeProvider time) : IRules
{
    private static readonly NotFound UnknownDatabase = new("the database does not exist", "unknown_database");
    private static readonly NotFound NoRules = new("the database has no rules", "no_rules");
    private static readonly NotFound UnknownVersion = new("the database has no such rules version", "unknown_version");
    private static readonly Unavailable StorageBusy = new("rules storage changed or is busy; retry later");
    private static readonly Unavailable CatchingUp = new("the region replica is catching up; retry later");

    public ValueTask<RulesCurrentResult> GetAsync(TenantContext database, CancellationToken cancellationToken) =>
        ReadAsync<RulesCurrentResult>(database, async rules => await rules.ReadRulesAsync(null, cancellationToken).ConfigureAwait(false) is { } current
            ? new CurrentRules(current, await rules.CheckRulesAsync(current.Rules, cancellationToken).ConfigureAwait(false))
            : NoRules, error => error, error => error, cancellationToken);

    public ValueTask<RulesVersionsResult> VersionsAsync(TenantContext database, string? after, int limit, CancellationToken cancellationToken) =>
        ReadAsync<RulesVersionsResult>(database, async rules => await rules.ListRulesVersionsAsync(after, limit, cancellationToken).ConfigureAwait(false) is { } page
            ? page
            : UnknownVersion, error => error, error => error, cancellationToken);

    public ValueTask<RulesVersionResult> VersionAsync(TenantContext database, string version, CancellationToken cancellationToken) =>
        ReadAsync<RulesVersionResult>(database, async rules => await rules.ReadRulesAsync(version, cancellationToken).ConfigureAwait(false) is { } stored
            ? stored
            : UnknownVersion, error => error, error => error, cancellationToken);

    public async ValueTask<RulesDeployResult> DeployAsync(TenantContext database, RulesChange change, RulesPrecondition precondition, string actor,
        CancellationToken cancellationToken)
    {
        try
        {
            if (await engine.OpenDatabaseAsync(database, cancellationToken).ConfigureAwait(false) is not { } store)
            {
                return UnknownDatabase;
            }

            await using var writer = await storage.BeginTransactionAsync(store, write: true, cancellationToken).ConfigureAwait(false);
            var rules = Rules(writer);
            var current = await rules.ReadRulesAsync(null, cancellationToken).ConfigureAwait(false);
            if (precondition switch
                {
                    RulesIfMatch match => current?.Version != match.Version,
                    RulesIfNone => current is not null,
                    _ => true,
                })
            {
                return new StaleRules(current?.Version);
            }

            RulesDocument document;
            switch (change)
            {
                case RulesDeploy deploy:
                    document = deploy.Rules;
                    break;
                case RulesRestore restore when await rules.ReadRulesAsync(restore.Version, cancellationToken).ConfigureAwait(false) is { } stored:
                    document = stored.Rules;
                    break;
                default:
                    return UnknownVersion;
            }

            document = new RulesDocument(new SortedDictionary<string, RulesTable>(document.Tables.ToDictionary(StringComparer.Ordinal), StringComparer.Ordinal));
            var problems = await rules.CheckRulesAsync(document, cancellationToken).ConfigureAwait(false);
            if (problems.Count > 0)
            {
                return new InvalidRules(problems);
            }

            var digest = Convert.ToHexStringLower(SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(document, RulesJsonContext.Default.RulesDocument)));
            var saved = new StoredRules(EntityIds.New().ToString(), document, time.GetUtcNow(), actor, digest);
            await rules.WriteRulesAsync(saved, cancellationToken).ConfigureAwait(false);
            _ = await writer.CommitAsync(cancellationToken).ConfigureAwait(false);
            return new DeployedRules(saved, RulesChecks.Warnings(document));
        }
        catch (Exception error) when (error is ObjectDisposedException or StorageBusyException or QosMemoryException)
        {
            return StorageBusy;
        }
    }

    private async ValueTask<T> ReadAsync<T>(TenantContext database, Func<IRulesStorage, ValueTask<T>> read, Func<NotFound, T> missing,
        Func<Unavailable, T> unavailable, CancellationToken cancellationToken)
    {
        try
        {
            if (replicas.Holds(database))
            {
                await using var replica = await replicas.BeginReadAsync(database, cancellationToken).ConfigureAwait(false);
                return replica is null ? unavailable(CatchingUp) : await read(replica).ConfigureAwait(false);
            }

            if (await engine.OpenDatabaseAsync(database, cancellationToken).ConfigureAwait(false) is not { } store)
            {
                return missing(UnknownDatabase);
            }

            await using var reader = await storage.BeginTransactionAsync(store, write: false, cancellationToken).ConfigureAwait(false);
            return await read(Rules(reader)).ConfigureAwait(false);
        }
        catch (Exception error) when (error is ObjectDisposedException or StorageBusyException or QosMemoryException)
        {
            return unavailable(StorageBusy);
        }
    }

    private static IRulesWriter Rules(IStorageTransaction transaction) =>
        transaction as IRulesWriter ?? throw new NotSupportedException("the storage transaction does not support rules");
}
