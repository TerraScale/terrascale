#:project ../src/TerraScale.DotNet/TerraScale.DotNet.csproj
#:property PublishAot=false

using System.Text.Json;
using System.Text.Json.Serialization;
using TerraScale.DotNet;
using TerraScale.DotNet.TerraBase;

if (args.Length != 5)
{
    Console.Error.WriteLine("usage: dotnet run sql-quickstart.cs -- <refresh-token-file> <organization> <project> <database> <region>");
    return 2;
}

const string CliClient = "00000000000000000000000007";
var (tokenFile, organization, project, database, region) = (args[0], args[1], args[2], args[3], args[4]);
using var http = new HttpClient(TerraBaseHttp.CreateHandler());
var auth = new TerraScaleAuthClient(http, new TerraScaleAuthClientOptions { BaseAddress = new Uri("https://auth.terrascale.tech"), InstanceSlug = "terrascale" });
var signIn = await auth.Oidc.RefreshTokenAsync(CliClient, File.ReadAllText(tokenFile).Trim());
File.WriteAllText(tokenFile, signIn.RefreshToken ?? throw new InvalidOperationException("ts-auth returned no refresh token."));
var projectToken = await auth.Oidc.ExchangePublicClientTokenAsync(CliClient, signIn.AccessToken, organization, project);

var sql = new TerraBaseSqlClient(http, new TerraBaseSqlClientOptions { Region = region, AccessTokenProvider = _ => ValueTask.FromResult(projectToken.AccessToken) });
var db = sql.Database(project, database);
var ids = new TerraScaleIdGenerator();
var (ada, grace) = (ids.NewId(), ids.NewId());

await db.ExecuteAsync("CREATE TABLE IF NOT EXISTS people (id TEXT PRIMARY KEY, name TEXT NOT NULL, avatar BLOB)");
await db.ExecuteAsync("INSERT INTO people (id, name, avatar) VALUES (?, ?, ?)", [ada, "Ada", TerraBaseSqlValue.FromBytes([1, 2, 3])]);
var batch = await db.BatchAsync([
    new("INSERT INTO people (id, name) VALUES (:id, :name)", Named: new Dictionary<string, TerraBaseSqlValue> { ["id"] = grace, ["name"] = "Grace" }),
    new("SELECT count(*) AS n FROM people WHERE id IN (?, ?)", [ada, grace]),
]);
var people = await db.QueryAsync("SELECT id, name, avatar FROM people WHERE id = ?", [ada], SampleJson.Default.Person);

TerraBaseException? refused = null;
try
{
    await db.ExecuteAsync("INSERT INTO people (id, name) VALUES (?, ?)", [ada, "Duplicate"]);
}
catch (TerraBaseException error)
{
    refused = error;
}

var passed = batch[0].RowsAffected == 1 && batch[1].Rows[0].GetInt64("n") == 2
    && people is [{ Name: "Ada", Avatar: [1, 2, 3] }] && refused is { StatusCode: System.Net.HttpStatusCode.BadRequest };
Console.WriteLine($"insert+batch rows={batch[1].Rows[0].GetInt64("n")} typed={people[0].Name}/{people[0].Avatar?.Length}B refused={refused?.ErrorCode} sqlstate={(refused as TerraBaseSqlException)?.Sqlstate ?? "-"} region={region}");
Console.WriteLine(passed ? "SQL quickstart passed" : "SQL quickstart FAILED");
return passed ? 0 : 1;

internal sealed record Person(string Id, string Name, byte[]? Avatar);

[JsonSourceGenerationOptions(JsonSerializerDefaults.Web)]
[JsonSerializable(typeof(Person))]
internal sealed partial class SampleJson : JsonSerializerContext;
