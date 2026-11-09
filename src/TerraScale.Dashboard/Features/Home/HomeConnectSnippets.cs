using TerraScale.Dashboard.Platform.Persistence;

namespace TerraScale.Dashboard.Features.Home
{
    // Copy-ready connect snippets for the Connect tab: an access token exchanged from a dashboard API key
    // (ts-docs docs/getting-started; sending the raw key as a bearer answers 401), one table, then SQL from each SDK.
    public static class HomeConnectSnippets
    {
        public const string Table = "posts";
        public static string ApiUrl => TerraBaseDiscovery.Origin.ToString().TrimEnd('/');

        public const string TokenExchange = """
        set +x
        set -o pipefail
        read -rs TERRASCALE_API_KEY && export TERRASCALE_API_KEY &&  # paste the key, then press Enter
        TERRASCALE_ACCESS_TOKEN="$(
          printf 'user = "%s:%s"\n' "${TERRASCALE_API_KEY%%_*}" "$TERRASCALE_API_KEY" |
            curl -sS --fail-with-body --config - -d grant_type=client_credentials \
              https://auth.terrascale.tech/terrascale/oauth/token |
            jq -er '.access_token | select(type == "string" and length > 0)'
        )" &&
        export TERRASCALE_ACCESS_TOKEN
        """;

        // The Postgres wire protocol takes the dashboard API key as the password (ts-docs guides/postgres-clients).
        public static string Postgres(string projectId, string database)
        {
            return $"""
        read -rs PGPASSWORD && export PGPASSWORD  # paste the API key, then press Enter
        psql "postgresql://app@pg.terrascale.tech:5432/{Uri.EscapeDataString(projectId)}/{Uri.EscapeDataString(database)}?sslmode=verify-full&sslrootcert=system"
        """;
        }

        public static string CreateTable(string projectId, string database)
        {
            return $$"""
        curl -sS --fail-with-body --header @/dev/fd/3 \
          -H 'Content-Type: application/json' -d '{"statements":[{"sql":"CREATE TABLE {{Table}} (id INTEGER PRIMARY KEY, title TEXT)"}]}' \
          "{{ApiUrl}}/v1/projects/{{projectId}}/databases/{{database}}/sql/execute" \
          3< <(printf 'Authorization: Bearer %s\n' "$TERRASCALE_ACCESS_TOKEN")
        """;
        }

        // Each SDK runs one query through the shared SQL shape (sdk/SQL-API.md).
        public static IReadOnlyList<(string Id, string Label, string Install, string Code)> For(string projectId, string database)
        {
            return [.. TerraScale.Dashboard.Features.Databases.DatabaseConnections.Sdks(projectId, database).Select(sdk => (sdk.Id, sdk.Label, Install(sdk.Id), sdk.Code))];
        }

        private static string Install(string sdk)
        {
            return sdk switch
            {
                "js" => $"npm install @terrascale/sdk@{SdkVersions.JavaScript}",
                "go" => $"go get terrascale.tech/sdk@{SdkVersions.Go}",
                "python" => $"pip install \"terrascale @ git+https://github.com/TerraScale/terrascale@{SdkVersions.PythonRef}#subdirectory=sdk/python\"",
                "rust" => "cargo add terrascale --git https://github.com/TerraScale/terrascale",
                "dotnet" => "git clone --depth 1 https://github.com/TerraScale/terrascale.git\ncd terrascale/sdk/dotnet\ndotnet pack TerraScale.DotNet.slnx -c Release -o ~/.nuget/packages-terrascale\ncd ../../..\ndotnet add package TerraScale.DotNet --source ~/.nuget/packages-terrascale",
                _ => ""
            };
        }
    }
}
