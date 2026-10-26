using TerraScale.Dashboard.Platform.Persistence;

namespace TerraScale.Dashboard.Features.Databases;

public sealed record DatabaseConnection(string Id, string Label, string Url, string Hint, string Guide);

public static class DatabaseConnections
{
    public static string ApiUrl => TerraBaseDiscovery.Origin.ToString().TrimEnd('/');

    // Public protocol contracts: docs.terrascale.tech/guides/{sql-http,libsql-clients,postgres-clients}.
    public static IReadOnlyList<DatabaseConnection> For(string projectId, string database)
    {
        var project = Uri.EscapeDataString(projectId);
        var name = Uri.EscapeDataString(database);
        var endpoint = $"{ApiUrl}/v1/projects/{project}/databases/{name}";
        return
        [
            new("document", "Document HTTP", endpoint + "/collections",
                "GET lists collections. Append /{collection}/documents to read or write documents.", "getting-started/"),
            new("sql", "HTTP SQL", endpoint + "/sql/execute",
                "POST JSON, for example: {\"statements\":[{\"sql\":\"SELECT 1\"}]}. Set Content-Type: application/json.", "guides/sql-http.html"),
            new("libsql", "libSQL / Hrana", endpoint + "/libsql/",
                "Use this HTTPS URL, including the trailing slash, and the access token as authToken. Refresh the token and recreate the client before it expires.", "guides/libsql-clients.html"),
            new("postgres", "Postgres", $"postgresql://app@pg.terrascale.tech:5432/{project}%2F{name}?sslmode=require",
                "Use the full API key as the password (PGPASSWORD for psql); no token exchange. TLS is required. With libpq 16+, sslmode=verify-full&sslrootcert=system also verifies the certificate.", "guides/postgres-clients.html")
        ];
    }
}
