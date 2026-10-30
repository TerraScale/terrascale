using TerraScale.TerraBase.Contracts;
using TerraScale.Dashboard.Platform.Observability;
using System.Text.Json;

namespace TerraScale.Dashboard.Features.Usage
{
    public sealed record UsageStorageFact(string ProjectId, string DatabaseId, long LogicalBytes, long Documents);
    public sealed record UsageProjectStorage(UsageStorageFact? Total, IReadOnlyDictionary<string, UsageStorageFact?> Databases);
    public sealed record UsageStorageSnapshot(string OrganizationId, IReadOnlyDictionary<string, UsageProjectStorage> Projects)
    {
        public long? OrganizationBytes
        {
            get
            {
                if (Projects.Values.Any(project => project.Total is null))
                {
                    return null;
                }

                return SumCounters(Projects.Values.Select(project => (long?)project.Total!.LogicalBytes));
            }
        }

        public UsageStorageFact? Scope(UsageSelection selection)
        {
            return selection.Context.Project is { } project && Projects.TryGetValue(project.Id, out var stored)
            ? selection.DatabaseId.Length == 0 ? stored.Total : stored.Databases.GetValueOrDefault(selection.DatabaseId) : null;
        }

        public void Validate(UsageSelection selection)
        {
            var organization = selection.Context.Organization ?? throw new AccountAccessException("Organization is unavailable.", 404);
            if (OrganizationId != organization.Id || !Projects.Keys.Order(StringComparer.Ordinal).SequenceEqual(organization.Projects.Keys.Order(StringComparer.Ordinal)))
            {
                throw new IOException("Stored usage belongs to a different organization.");
            }

            foreach (var (projectId, stored) in Projects)
            {
                var project = organization.Projects[projectId];
                if (project.OrganizationId != organization.Id || !stored.Databases.Keys.Order(StringComparer.Ordinal).SequenceEqual(project.Databases.Keys.Order(StringComparer.Ordinal)))
                {
                    throw new IOException("Stored usage belongs to a different project.");
                }

                foreach (var (databaseId, fact) in stored.Databases)
                {
                    if (project.Databases[databaseId].ProjectId != projectId)
                    {
                        throw new IOException("Stored database usage scope is invalid.");
                    }

                    if (fact is not null)
                    {
                        Check(fact, projectId, databaseId);
                    }
                }
                if (stored.Total is not null)
                {
                    Check(stored.Total, projectId, "");
                    if (stored.Databases.Values.Any(fact => fact is null) ||
                        stored.Total.LogicalBytes != SumCounters(stored.Databases.Values.Select(fact => fact?.LogicalBytes)) ||
                        stored.Total.Documents != SumCounters(stored.Databases.Values.Select(fact => fact?.Documents)))
                    {
                        throw new IOException("Stored project usage is incomplete.");
                    }
                }
            }
            _ = OrganizationBytes;
        }

        internal static long? SumCounters(IEnumerable<long?> counters)
        {
            long total = 0;
            try
            {
                foreach (var counter in counters)
                {
                    if (counter is null)
                    {
                        return null;
                    }

                    total = checked(total + counter.Value);
                }
                return total;
            }
            catch (OverflowException) { return null; }
        }

        private static void Check(UsageStorageFact fact, string projectId, string databaseId)
        {
            if (fact.ProjectId != projectId || fact.DatabaseId != databaseId || fact.LogicalBytes < 0 || fact.Documents < 0)
            {
                throw new IOException("Stored usage returned an invalid resource or counter.");
            }
        }
    }

    public interface IUsageStorage
    {
        Task<UsageStorageSnapshot> ReadAsync(UsageSelection selection, CancellationToken cancellationToken);
    }

    // Storage is read independently of operation meters. A canonical empty project
    // has zero stored bytes; a failed database read never becomes an observed zero.
    public sealed class UsageStorageBackend(AccountService accounts, IDatabaseBackend databases) : IUsageStorage
    {
        public async Task<UsageStorageSnapshot> ReadAsync(UsageSelection selection, CancellationToken cancellationToken)
        {
            var context = await AccountScope.RefreshAsync(accounts, selection.Context, cancellationToken);
            var organization = context.Organization!;
            using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            deadline.CancelAfter(TimeSpan.FromSeconds(5));
            IReadOnlyList<DatabaseRegion> regions = [];
            if (organization.Projects.Values.Any(project => project.Databases.Count > 0))
            {
                try { regions = await databases.RegionsAsync(deadline.Token); }
                catch (Exception error) when (Unavailable(error, cancellationToken)) { ServiceSloTelemetry.MarkFailure(error); }
            }

            using var concurrency = new SemaphoreSlim(4);
            var projects = await Task.WhenAll(organization.Projects.Values.Select(async project =>
            {
                if (project.OrganizationId != organization.Id)
                {
                    throw new IOException("Stored project scope is invalid.");
                }

                var rows = await Task.WhenAll(project.Databases.Values.Select(async database =>
                {
                    if (database.ProjectId != project.Id)
                    {
                        throw new IOException("Stored database scope is invalid.");
                    }

                    UsageStorageFact? fact = null;
                    var acquired = false;
                    try
                    {
                        await concurrency.WaitAsync(deadline.Token); acquired = true;
                        var code = database.Region.Length > 0 ? database.Region : project.Region;
                        var home = DatabaseRegion.Resolve(regions, code)
                            ?? throw new HttpRequestException("The database home region is unavailable.");
                        var scope = DatabaseService.Scope(context with { Project = project });
                        var contents = await databases.CollectionsAsync(scope, home, database.Id, deadline.Token);
                        DatabaseService.ValidateContents(contents);
                        fact = new(project.Id, database.Id, contents.Sum(collection => collection.Bytes), contents.Sum(collection => collection.Documents));
                    }
                    catch (Exception error) when (Unavailable(error, cancellationToken)) { ServiceSloTelemetry.MarkFailure(error); }
                    finally
                    {
                        if (acquired)
                        {
                            concurrency.Release();
                        }
                    }
                    return (database.Id, Fact: fact);
                }));
                var stored = rows.ToDictionary(row => row.Id, row => row.Fact, StringComparer.Ordinal);
                UsageStorageFact? total = null;
                if (stored.Values.All(fact => fact is not null))
                {
                    try { total = new(project.Id, "", stored.Values.Aggregate(0L, (sum, fact) => checked(sum + fact!.LogicalBytes)), stored.Values.Aggregate(0L, (sum, fact) => checked(sum + fact!.Documents))); }
                    catch (OverflowException) { }
                }

                return (project.Id, Stored: new UsageProjectStorage(total, stored));
            }));
            cancellationToken.ThrowIfCancellationRequested();
            var result = new UsageStorageSnapshot(organization.Id, projects.ToDictionary(project => project.Id, project => project.Stored, StringComparer.Ordinal));
            result.Validate(selection with { Context = context });
            return result;
        }

        // This filter only surrounds discovery/statistics IO after fresh scope
        // authorization. A producer rejection is unknown storage, not a zero or a
        // substitute authorization decision for the dashboard.
        private static bool Unavailable(Exception error, CancellationToken cancellationToken)
        {
            return error is TerraBaseException or HttpRequestException or IOException or JsonException or InvalidOperationException or OverflowException ||
            error is OperationCanceledException && !cancellationToken.IsCancellationRequested;
        }
    }
}
