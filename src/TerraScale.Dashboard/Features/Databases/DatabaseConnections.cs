using TerraScale.Dashboard.Platform.Persistence;

namespace TerraScale.Dashboard.Features.Databases
{
    public sealed record DatabaseConnection(string Id, string Label, string Text, string Guide);

    public sealed record DatabaseSdkSnippet(string Id, string Label, string Code);

    public static class DatabaseConnections
    {
        public static string ApiUrl => TerraBaseDiscovery.Origin.ToString().TrimEnd('/');

        // Public protocol contracts: docs.terrascale.tech/guides/{postgres-clients,sql-http}; the API key is the password.
        public static IReadOnlyList<DatabaseConnection> For(string projectId, string database)
        {
            var endpoint = $"{ApiUrl}/v1/projects/{Uri.EscapeDataString(projectId)}/databases/{Uri.EscapeDataString(database)}";
            return
            [
                new("psql", "psql", $"psql \"host=pg.terrascale.tech port=5432 dbname={projectId}/{database} user=any sslmode=verify-full sslrootcert=system\"", "guides/postgres-clients.html"),
                new("sql", "SQL over HTTP", "POST " + endpoint + "/sql/execute", "guides/sql-http.html")
            ];
        }

        // The shared SQL SDK shape (sdk/SQL-API.md) in each SDK's own syntax.
        public static IReadOnlyList<DatabaseSdkSnippet> Sdks(string projectId, string database)
        {
            return
            [
                new("python", "Python", $"""
                    from terrascale import TerraScale

                    db = TerraScale(project="{projectId}").database("{database}")
                    rows = db.query("SELECT 1")
                    """),
                new("js", "JavaScript", $$"""
                    import { createTerraBaseV1Client } from "@terrascale/sdk";

                    const db = createTerraBaseV1Client({ project: "{{projectId}}", token, baseUrl: "{{ApiUrl}}" }).database("{{database}}");
                    const rows = await db.query("SELECT 1");
                    """),
                new("go", "Go", $$"""
                    tokens, err := tsauth.NewAPIKeySource(tsauth.APIKeyConfig{
                        Key: os.Getenv("TERRASCALE_API_KEY"), Project: "{{projectId}}",
                    })
                    if err != nil {
                        panic(err)
                    }
                    c, err := v1.New(v1.Config{TokenSource: tokens})
                    if err != nil {
                        panic(err)
                    }
                    db := c.Database("{{projectId}}", "{{database}}")
                    rows, err := db.Query(ctx, "SELECT 1")
                    """),
                new("dotnet", ".NET", $$"""
                    var sql = new TerraBaseSqlClient(http, new TerraBaseSqlClientOptions { ApiKey = apiKey });
                    var db = sql.Database("{{projectId}}", "{{database}}");
                    var rows = await db.QueryAsync("SELECT 1", null, ct);
                    """),
                new("rust", "Rust", $"""
                    let client = terrascale::v1::Client::for_project_api_key("{projectId}", &api_key).build()?;
                    let db = client.database("{database}").sql();
                    let rows = db.query("SELECT 1", &[]).await?;
                    """)
            ];
        }
    }
}
