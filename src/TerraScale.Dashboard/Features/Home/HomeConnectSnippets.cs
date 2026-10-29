using TerraScale.Dashboard.Platform.Persistence;

namespace TerraScale.Dashboard.Features.Home;

// Copy-ready connect snippets for the Connect tab. Each SDK lists the first page of one collection with an access
// token exchanged from a dashboard API key (ts-docs docs/getting-started; sending the raw key as a bearer answers 401).
public static class HomeConnectSnippets
{
    public const string Collection = "posts";
    public static string ApiUrl => TerraBaseDiscovery.Origin.ToString().TrimEnd('/');

    public const string TokenExchange = """
        set +x
        set -o pipefail
        read -rs TERRASCALE_API_KEY &&  # paste the key, then press Enter
        TERRASCALE_ACCESS_TOKEN="$(
          printf 'user = "%s:%s"\n' "${TERRASCALE_API_KEY%%_*}" "$TERRASCALE_API_KEY" |
            curl -sS --fail-with-body --config - -d grant_type=client_credentials \
              https://auth.terrascale.tech/terrascale/oauth/token |
            jq -er '.access_token | select(type == "string" and length > 0)'
        )" &&
        export TERRASCALE_ACCESS_TOKEN
        """;

    // The Postgres wire protocol takes the dashboard API key as the password (ts-docs guides/postgres-clients).
    public static string Postgres(string database) => $"""
        read -rs PGPASSWORD && export PGPASSWORD  # paste the API key, then press Enter
        psql "postgresql://app@pg.terrascale.tech:5432/{Uri.EscapeDataString(database)}?sslmode=verify-full&sslrootcert=system"
        """;

    public static string CreateCollection(string projectId, string database) => $$"""
        curl -sS --fail-with-body --header @/dev/fd/3 \
          -H 'Content-Type: application/json' -d '{"name":"{{Collection}}"}' \
          "{{ApiUrl}}/v1/projects/{{projectId}}/databases/{{database}}/collections" \
          3< <(printf 'Authorization: Bearer %s\n' "$TERRASCALE_ACCESS_TOKEN")
        """;

    public static IReadOnlyList<(string Id, string Label, string Install, string Code)> For(string projectId, string database) =>
    [
        ("curl", "curl", "", $"""
            curl -sS --fail-with-body --header @/dev/fd/3 \
              "{ApiUrl}/v1/projects/{projectId}/databases/{database}/collections/{Collection}/documents?limit=10" \
              3< <(printf 'Authorization: Bearer %s\n' "$TERRASCALE_ACCESS_TOKEN")
            """),
        ("js", "JavaScript", $"npm install @terrascale/sdk@{SdkVersions.JavaScript}", $$"""
            import { createTerraBaseV1Client } from "@terrascale/sdk";

            const client = createTerraBaseV1Client({
              baseUrl: "{{ApiUrl}}",
              project: "{{projectId}}",
              token: process.env.TERRASCALE_ACCESS_TOKEN,
            });
            const page = await client.database("{{database}}").collection("{{Collection}}").list({ limit: 10 });
            console.log(page.items);
            """),
        ("go", "Go", $"go get terrascale.tech/sdk@{SdkVersions.Go}", $$"""
            package main

            import (
            	"context"
            	"fmt"
            	"os"

            	v1 "terrascale.tech/sdk/v1"
            )

            func main() {
            	c, err := v1.New(v1.Config{BaseURL: "{{ApiUrl}}", Token: os.Getenv("TERRASCALE_ACCESS_TOKEN")})
            	if err != nil {
            		panic(err)
            	}
            	page, err := c.Database("{{projectId}}", "{{database}}").Collection("{{Collection}}").List(context.Background(), v1.ListOptions{Limit: 10})
            	if err != nil {
            		panic(err)
            	}
            	fmt.Println(len(page.Items))
            }
            """),
        ("python", "Python", $"pip install \"terrascale @ git+https://github.com/TerraScale/terrascale@history/ts-python/{SdkVersions.Python}\"", $$"""
            import os

            from terrascale import TerraScale

            with TerraScale(project="{{projectId}}", token=os.environ["TERRASCALE_ACCESS_TOKEN"], base_url="{{ApiUrl}}") as ts:
                page = ts.database("{{database}}").collection("{{Collection}}").list(limit=10)
                print(len(page.items))
            """),
        ("rust", "Rust", $"cargo add terrascale --git https://github.com/TerraScale/terrascale --tag history/ts-rust/{SdkVersions.Rust}\ncargo add serde_json\ncargo add tokio --features full", $$"""
            use terrascale::v1::Client;

            #[tokio::main]
            async fn main() -> terrascale::v1::Result<()> {
                let token = std::env::var("TERRASCALE_ACCESS_TOKEN").expect("TERRASCALE_ACCESS_TOKEN");
                let client = Client::for_project("{{projectId}}", token).build()?;
                let page = client.database("{{database}}").collection("{{Collection}}").list::<serde_json::Value>(Some(10), None).await?;
                println!("{}", page.items.len());
                Ok(())
            }
            """),
    ];
}
