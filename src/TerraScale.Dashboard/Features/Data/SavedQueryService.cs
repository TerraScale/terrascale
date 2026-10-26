using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using TerraScale.Dashboard.Platform.Persistence;
using TerraScale.DotNet.TerraBase;

namespace TerraScale.Dashboard.Features.Data;

public sealed record SavedQuery(string Id, string Name, string Sql, string DatabaseId, DateTimeOffset CreatedAt);
public sealed record SavedQueryRecord(string OrganizationId, string ProjectId, IReadOnlyList<SavedQuery> Queries);

public sealed class SavedQueryService(IDashboardState state, TimeProvider time)
{
    private const string Collection = "dashboard_saved_queries";
    public async Task<IReadOnlyList<SavedQuery>> ListAsync(AccountContext context, string databaseId, CancellationToken cancellationToken)
    {
        if (databaseId.Length > 0) DatabaseService.Database(context, databaseId);
        var queries = await ListAsync(context, cancellationToken);
        return databaseId.Length == 0 ? queries : queries.Where(query => query.DatabaseId.Length == 0 || query.DatabaseId == databaseId).ToArray();
    }

    public async Task<IReadOnlyList<SavedQuery>> ListAsync(AccountContext context, CancellationToken cancellationToken)
    {
        var document = await state.GetAsync(Collection, Key(context), cancellationToken);
        if (document is null) return [];
        var record = Read(context, document);
        return record.Queries.OrderBy(query => query.Name, StringComparer.Ordinal).ToArray();
    }

    public async Task<SavedQuery> SaveAsync(AccountContext context, string databaseId, string name, string sql, CancellationToken cancellationToken)
    {
        if (databaseId.Length > 0) DatabaseService.Database(context, databaseId);
        if (!context.CanWrite) throw new AccountAccessException("Write access is required.");
        name = name.Trim();
        if (name.Length == 0 || Encoding.UTF8.GetByteCount(name) > 128) throw new AccountAccessException("Saved query names use 1–128 characters.", 422);
        sql = sql.Trim();
        if (SqlValidation.Diagnostics(sql).FirstOrDefault(item => item.Severity == "error") is { } problem) throw new AccountAccessException(problem.Message, 422);
        var query = new SavedQuery(AccountService.NewId(), name, sql, databaseId, time.GetUtcNow());
        await UpdateAsync(context, queries => queries.Add(query), cancellationToken);
        return query;
    }

    public async Task DeleteAsync(AccountContext context, string databaseId, string id, CancellationToken cancellationToken)
    {
        if (!context.CanWrite) throw new AccountAccessException("Write access is required.");
        await UpdateAsync(context, queries =>
        {
            var query = queries.FirstOrDefault(query => query.Id == id) ?? throw new AccountAccessException("Saved query is unavailable.", 404);
            queries.Remove(query);
        }, cancellationToken);
    }

    private async Task UpdateAsync(AccountContext context, Action<List<SavedQuery>> mutate, CancellationToken cancellationToken)
    {
        for (var attempt = 0; attempt < 4; attempt++)
        {
            var key = Key(context);
            var current = await state.GetAsync(Collection, key, cancellationToken);
            var queries = current is null ? [] : Read(context, current).Queries.ToList();
            mutate(queries);
            try
            {
                await state.PutAsync(Collection, key, JsonSerializer.SerializeToElement(new SavedQueryRecord(context.Organization!.Id, context.Project!.Id, queries)), current is null ? TerraBasePrecondition.Absent : TerraBasePrecondition.Matches(current.Version), cancellationToken);
                return;
            }
            catch (TerraBasePreconditionFailedException) when (attempt < 3) { }
        }
    }
    private static SavedQueryRecord Read(AccountContext context, TerraBaseDocument document)
    {
        var record = AccountService.Read<SavedQueryRecord>(document);
        if (record.OrganizationId != context.Organization!.Id || record.ProjectId != context.Project!.Id) throw new AccountAccessException("Saved query scope is invalid.");
        return record;
    }
    private static string Key(AccountContext context)
    {
        var scope = DatabaseService.Scope(context);
        return Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(scope.OrganizationId + "/" + scope.ProjectId)));
    }
}
