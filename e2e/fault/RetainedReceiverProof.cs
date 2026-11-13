using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using TerraBase.Engine;
using TerraBase.Engine.Revisions;
using TerraBase.Features.Management;
using TerraBase.Features.Replicas;
using TerraBase.Tenancy;

internal static class RetainedReceiverProof
{
    private static readonly Dictionary<string, object?> NoNamed = [];

    public static async Task RunAsync(WebApplication app, CancellationToken token)
    {
        await app.StartAsync(token);
        var engine = app.Services.GetRequiredService<IStorageEngine>();
        var replication = app.Services.GetRequiredService<RegionReplication>();
        try
        {
            Console.WriteLine("RECEIPT_NODE started");
            while (await Console.In.ReadLineAsync(token) is { } command && command != "stop")
            {
                var parts = command.Split(' ');
                if (parts[0] != "ping")
                {
                    var database = new TenantContext(parts[1], PlanTier.Hobby, parts[2], parts[3]);
                    switch (parts[0])
                    {
                        case "write":
                            var store = await engine.OpenDatabaseAsync(database, token) ?? throw new InvalidOperationException("source file missing");
                            await using (var transaction = await store.BeginDurableTransactionAsync(token))
                            {
                                _ = await transaction.ExecuteStatementAsync(Sql("CREATE TABLE IF NOT EXISTS retained_receiver (value INTEGER)"), token);
                                _ = await transaction.ExecuteStatementAsync(Sql("INSERT INTO retained_receiver VALUES (?)", long.Parse(parts[4], CultureInfo.InvariantCulture)), token);
                                await transaction.CommitAsync(token);
                            }
                            _ = await ((IDatabaseRevisions)engine).DrainWritersAsync(database, token);
                            break;
                        case "retain":
                            var revision = await ((IDatabaseRevisions)engine).CurrentRevisionAsync(database, token)
                                ?? throw new InvalidOperationException("source revision missing");
                            await using (var transaction = await (await engine.OpenSystemDatabaseAsync(token)).BeginDurableTransactionAsync(token))
                            {
                                var record = await CatalogTables.DatabaseAsync(transaction, database.DatabaseId!, token)
                                    ?? throw new InvalidOperationException("source registration missing");
                                var now = app.Services.GetRequiredService<TimeProvider>().GetUtcNow();
                                var json = new JsonObject
                                {
                                    ["storage"] = JsonSerializer.SerializeToNode(database, ManagementJsonContext.Default.TenantContext),
                                    ["database"] = JsonSerializer.SerializeToNode(record, ManagementJsonContext.Default.ManagedDatabase),
                                    ["deletedAt"] = JsonValue.Create(now),
                                    ["purgeAt"] = JsonValue.Create(now.AddDays(7)),
                                    ["revision"] = revision.ToString(),
                                };
                                var retained = json.Deserialize(ManagementJsonContext.Default.DeletedDatabaseRecord)
                                    ?? throw new InvalidOperationException("retained fixture missing");
                                await SystemCatalog.PutAsync(transaction, DeletedDatabases.Kind, record.Id, retained,
                                    ManagementJsonContext.Default.DeletedDatabaseRecord, token);
                                _ = await CatalogTables.DeleteDatabaseAsync(transaction, record.Id, token, retireIdentity: false);
                                await transaction.CommitAsync(token);
                            }
                            break;
                        case "restore":
                            await using (var transaction = await (await engine.OpenSystemDatabaseAsync(token)).BeginDurableTransactionAsync(token))
                            {
                                var retained = await SystemCatalog.GetAsync(transaction, DeletedDatabases.Kind, database.DatabaseId!,
                                    ManagementJsonContext.Default.DeletedDatabaseRecord, token)
                                    ?? throw new InvalidOperationException("retained fixture missing");
                                await CatalogTables.PutDatabaseAsync(transaction, retained.Database, token);
                                await DeletedDatabases.RemoveAsync(transaction, retained, token);
                                await transaction.CommitAsync(token);
                            }
                            break;
                        case "check":
                            await replication.SyncCatalogsAsync(token);
                            _ = await replication.CatchUpAsync(database, token);
                            await using (var reader = await app.Services.GetRequiredService<IDatabaseReplicas>().BeginReadAsync(database, token)
                                ?? throw new InvalidOperationException("retained receiver copy was deleted"))
                            {
                                var rows = await reader.ExecuteAsync(Sql("SELECT sum(value) FROM retained_receiver"), token);
                                if (rows.Rows.Count != 1 || rows.Rows[0][0] is not long sum || sum != long.Parse(parts[4], CultureInfo.InvariantCulture))
                                {
                                    throw new InvalidOperationException("retained receiver did not resume replication with the restored rows");
                                }
                            }
                            break;
                        default:
                            throw new InvalidOperationException("unknown retained receiver journey command");
                    }
                }
                Console.WriteLine("RECEIPT_NODE " + command);
            }
        }
        finally
        {
            await app.StopAsync(CancellationToken.None);
        }
    }

    private static SqlStatement Sql(string text, params object?[] args) => new(text, args, NoNamed, 10, 1024, TimeSpan.FromSeconds(10));
}
