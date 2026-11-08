// Public examples shared by the docs and landing code panels.
export const SDK_SAMPLES = {
  typescript: `import { createTerraBaseV1Client } from "@terrascale/sdk";

// Server-side: TERRASCALE_ACCESS_TOKEN is a 60-second token exchanged from
// your dashboard API key. Keep both out of browser bundles.
const client = createTerraBaseV1Client({
  baseUrl: "https://api.terrascale.tech",
  project: process.env.TERRASCALE_PROJECT_ID,
  token: process.env.TERRASCALE_ACCESS_TOKEN,
});
const db = client.database("app");

// One transaction: both statements commit or neither does.
await db.batch([
  "CREATE TABLE IF NOT EXISTS customers (id INTEGER PRIMARY KEY, email TEXT UNIQUE NOT NULL, seats INTEGER NOT NULL)",
  { sql: "INSERT INTO customers (email, seats) VALUES (?, ?)", args: ["team@example.com", 12] },
]);

const customers = await db.query(
  "SELECT email, seats FROM customers WHERE seats >= ? ORDER BY seats DESC LIMIT 25",
  [10],
);`,
  csharp: `using TerraScale.DotNet.TerraBase;

// Server-side: the client exchanges your dashboard API key for
// short-lived access tokens. Keep the key out of client apps.
using var http = new HttpClient(TerraBaseHttp.CreateHandler());
var sql = new TerraBaseSqlClient(http, new TerraBaseSqlClientOptions { ApiKey = apiKey });
TerraBaseSqlDatabase db = sql.Database(projectId, "app");

// One transaction: both statements commit or neither does.
await db.BatchAsync([
    new("CREATE TABLE IF NOT EXISTS customers (id INTEGER PRIMARY KEY, email TEXT UNIQUE NOT NULL, seats INTEGER NOT NULL)"),
    new("INSERT INTO customers (email, seats) VALUES (?, ?)", ["team@example.com", 12])
], cancellationToken);

var customers = await db.QueryAsync(
    "SELECT email, seats FROM customers WHERE seats >= ? ORDER BY seats DESC LIMIT 25",
    [10], cancellationToken);
foreach (var row in customers)
    Console.WriteLine($"{row.GetString("email")}: {row.GetInt64("seats")}");`,
  python: `from terrascale import TerraScale

# Server-side: reads TERRASCALE_API_KEY and TERRASCALE_PROJECT_ID.
with TerraScale() as client:
    db = client.database("app")
    customers = db.query(
        "SELECT email, seats FROM customers WHERE seats >= ? ORDER BY seats DESC LIMIT 25",
        [10],
    )
    for row in customers:
        print(row.get_string("email"), row.get_int64("seats"))`,
  go: `import (
    "context"
    "os"

    tb "terrascale.tech/sdk/v1"
    "terrascale.tech/sdk/tsauth"
)

// Server-side: the token source exchanges your API key.
project := os.Getenv("TERRASCALE_PROJECT_ID")
tokens, err := tsauth.NewAPIKeySource(tsauth.APIKeyConfig{
    Key: os.Getenv("TERRASCALE_API_KEY"), Project: project,
})
if err != nil { panic(err) }
client, err := tb.New(tb.Config{TokenSource: tokens})
if err != nil { panic(err) }
db := client.Database(project, "app")
customers, err := db.Query(context.Background(),
    "SELECT email, seats FROM customers WHERE seats >= ? ORDER BY seats DESC LIMIT 25", 10)
if err != nil { panic(err) }`,
  rust: `use terrascale::v1::Client;

// Server-side: the client exchanges and refreshes project tokens.
let project = std::env::var("TERRASCALE_PROJECT_ID")?;
let api_key = std::env::var("TERRASCALE_API_KEY")?;
let client = Client::for_project_api_key(&project, &api_key).build()?;
let db = client.database("app").sql();
let customers = db.query(
    "SELECT email, seats FROM customers WHERE seats >= ? ORDER BY seats DESC LIMIT 25",
    &[10.into()],
).await?;
for row in customers {
    println!("{}: {}", row.get_str("email")?, row.get_i64("seats")?);
}`,
};

export const SDK_LANGUAGES = [
  { id: 'python', label: 'Python', file: '~/app/queries.py', icon: '/language-icons/python.svg' },
  { id: 'typescript', label: 'JavaScript', file: '~/app/queries.js', icon: '/language-icons/javascript.svg' },
  { id: 'go', label: 'Go', file: '~/app/queries.go', icon: '/language-icons/go.svg' },
  { id: 'csharp', label: '.NET', file: '~/app/queries.cs', icon: '/language-icons/dotnet.svg' },
  { id: 'rust', label: 'Rust', file: '~/app/queries.rs', icon: '/language-icons/rust.svg' },
];
