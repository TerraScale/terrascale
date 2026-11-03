using TerraScale.Dashboard.Platform.Persistence;

namespace TerraScale.Dashboard.Features.Databases
{
    public sealed record DatabaseConnection(string Id, string Label, string Url, string Guide);

    public static class DatabaseConnections
    {
        public static string ApiUrl => TerraBaseDiscovery.Origin.ToString().TrimEnd('/');

        // Public protocol contracts: docs.terrascale.tech/guides/{sql-http,postgres-clients}.
        public static IReadOnlyList<DatabaseConnection> For(string projectId, string database)
        {
            var project = Uri.EscapeDataString(projectId);
            var name = Uri.EscapeDataString(database);
            var endpoint = $"{ApiUrl}/v1/projects/{project}/databases/{name}";
            return
            [
                new("document", "Document HTTP", endpoint + "/collections", "getting-started/"),
                new("sql", "HTTP SQL", endpoint + "/sql/execute", "guides/sql-http.html"),
                new("postgres", "Postgres", $"postgresql://app@pg.terrascale.tech:5432/{name}?sslmode=verify-full&sslrootcert=system", "guides/postgres-clients.html")
            ];
        }
    }
}
