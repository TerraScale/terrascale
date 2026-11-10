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
            var path = $"/v1/projects/{Uri.EscapeDataString(projectId)}/databases/{Uri.EscapeDataString(database)}";
            var endpoint = ApiUrl + path;
            return
            [
                new("psql", "psql", $"psql \"host=pg.terrascale.tech port=5432 dbname={projectId}/{database} user=any sslmode=verify-full sslrootcert=system\"", "guides/postgres-clients.html"),
                new("sql", "SQL over HTTP", "POST " + endpoint + "/sql/execute", "guides/sql-http.html"),
                new("wss", "SQL over WebSocket", "GET wss://" + TerraBaseDiscovery.Origin.Host + path + "/sql/ws", "concepts/sql.html")
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
                    import { createTsAuthClient } from "@terrascale/sdk/ts-auth";

                    const apiKey = process.env.TERRASCALE_API_KEY;
                    const auth = createTsAuthClient({ apiBaseUrl: "https://auth.terrascale.tech", instanceSlug: "terrascale" });
                    const keyId = apiKey.slice(0, apiKey.indexOf("_"));
                    async function getAccessToken() {
                      const result = await auth.oidc.token(keyId, apiKey, { grant_type: "client_credentials", project: "{{projectId}}", scope: "sql:read sql:write" });
                      if (!result.ok) throw new Error("Token exchange failed");
                      return result.value.access_token;
                    }
                    const db = createTerraBaseV1Client({ baseUrl: "{{ApiUrl}}", project: "{{projectId}}", token: getAccessToken }).database("{{database}}");
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
                    using var http = new HttpClient(TerraBaseHttp.CreateHandler());
                    var sql = new TerraBaseSqlClient(http, new TerraBaseSqlClientOptions { ApiKey = Environment.GetEnvironmentVariable("TERRASCALE_API_KEY"), Region = Environment.GetEnvironmentVariable("TERRASCALE_REGION") });
                    var db = sql.Database("{{projectId}}", "{{database}}");
                    var rows = await db.QueryAsync("SELECT 1");
                    """),
                new("rust", "Rust", $$"""
                    let client = terrascale::v1::Client::for_project_api_key("{{projectId}}", &std::env::var("TERRASCALE_API_KEY").expect("TERRASCALE_API_KEY")).build()?;
                    let db = client.database("{{database}}").sql();
                    let rows = db.query("SELECT 1", &[]).await?;
                    """)
            ];
        }
    }
}
