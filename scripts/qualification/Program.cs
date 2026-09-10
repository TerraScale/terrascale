using System.Net;
using System.Text.Json;
using TerraScale.DotNet;

static string Required(string name)
{
    var value = Environment.GetEnvironmentVariable(name);
    if (string.IsNullOrWhiteSpace(value)) throw new InvalidOperationException($"missing required input: {name}");
    return value.Trim();
}
try
{
var phase = args.SkipWhile(static x => x != "--phase").Skip(1).FirstOrDefault()
    ?? throw new InvalidOperationException("missing required --phase");
if (phase is not ("seed" or "verify" or "exercise" or "cleanup")) throw new InvalidOperationException("phase must be seed, verify, exercise, or cleanup");
var api = new Uri(Required("TERRASCALE_API_URL"));
_ = new Uri(Required("TERRASCALE_AUTH_URL"));
_ = Required("TERRASCALE_PROJECT_ID");
var database = Required("TERRASCALE_DATABASE_ID");
var key = Required("TERRASCALE_API_KEY");
var runId = Required("TERRASCALE_JOURNEY_RUN_ID");
if (!System.Text.RegularExpressions.Regex.IsMatch(runId, "^[A-Za-z0-9][A-Za-z0-9_-]{0,63}$")) throw new InvalidOperationException("invalid journey run ID");
using var http = new HttpClient(new SocketsHttpHandler { AllowAutoRedirect = false });
// API keys are project-scoped credentials; the project identifier is required
// as an input for the journey namespace but is not an authorization header.
var client = new TerraScaleClient(http, new TerraScaleClientOptions { BaseAddress = api, ApiKey = key });
const string collection = "sdk_journey";
var own = $"{runId}-dotnet";
var peers = new[] { "typescript", "go", "rust", "dotnet" }.Select(x => $"{runId}-{x}").ToArray();

async Task Observe(string operation, Func<Task> action)
{
    try { await action(); Console.WriteLine(JsonSerializer.Serialize(new { operation, status = "ok" })); }
    catch (TerraScaleApiException ex) { Console.WriteLine(JsonSerializer.Serialize(new { operation, status = "error", http = (int?)ex.StatusCode })); throw; }
}
if (phase == "seed")
{
    await Observe("seed", async () => await client.Documents.UpsertAsync(database, collection, own, new { sdk = "dotnet", runId }));
}
else if (phase == "verify")
{
    foreach (var id in peers)
    {
        var doc = await client.Documents.GetAsync(database, collection, id);
        var body = doc.Value;
        if (body.GetProperty("runId").GetString() != runId || body.GetProperty("sdk").GetString() != id[(runId.Length + 1)..]) throw new InvalidOperationException($"unexpected body in {id}");
        Console.WriteLine(JsonSerializer.Serialize(new { operation = "verify", id, status = "ok" }));
    }
}
else if (phase == "exercise")
{
    var id = $"{runId}-dotnet-exercise";
    var created = await client.Documents.CreateAsync(database, collection, id, new { sdk = "dotnet", runId, value = 1 });
    await Observe("missing_document", async () =>
    {
        try { await client.Documents.GetAsync(database, collection, $"{runId}-dotnet-missing"); throw new InvalidOperationException("missing document was found"); }
        catch (TerraScaleApiException ex) when (ex.StatusCode == HttpStatusCode.NotFound) { }
    });
    await Observe("stale_etag_denial", async () =>
    {
        try { await client.Documents.PutAsync(database, collection, id, new { sdk = "dotnet", runId, value = 2 }, new TerraScaleDocumentMutationOptions(ExpectedChangeToken: "stale")); throw new InvalidOperationException("stale ETag was accepted"); }
        catch (TerraScaleApiException ex) when (JourneyChecks.IsStaleDenial(ex.StatusCode, ex.ErrorCode)) { }
    });
    var afterStale = await client.Documents.GetAsync(database, collection, id);
    JourneyChecks.SameReceipt(created, afterStale);
    JourneyChecks.Body(afterStale, runId, 1);
    var idem = $"{runId}-dotnet-idempotency";
    var first = await client.Documents.PutAsync(database, collection, id, new { sdk = "dotnet", runId, value = 3 }, new TerraScaleDocumentMutationOptions(IdempotencyKey: idem));
    var duplicate = await client.Documents.PutAsync(database, collection, id, new { sdk = "dotnet", runId, value = 3 }, new TerraScaleDocumentMutationOptions(IdempotencyKey: idem));
    JourneyChecks.SameReceipt(first, duplicate);
    var replayed = await client.Documents.GetAsync(database, collection, id);
    JourneyChecks.SameReceipt(first, replayed);
    JourneyChecks.Body(replayed, runId, 3);
    var sql = await client.Sql.ExecuteAsync(database, "SELECT * FROM sdk_journey WHERE id = ?", new object?[] { own });
    if (sql.Matched != 1 || sql.Rows.Count != 1 || sql.Rows[0].Id != own || sql.Rows[0].Value.GetProperty("runId").GetString() != runId) throw new InvalidOperationException("parameterized SQL did not return the seeded own row");
    var reactive = new TerraScaleReactiveHttpClient(http, new TerraScaleReactiveHttpClientOptions { BaseAddress = api, ApiBaseAddress = api, ApiKey = key });
    using var attachDeadline = new CancellationTokenSource(TimeSpan.FromSeconds(20));
    var subscription = await reactive.AttachAsync(database, $"{runId}-dotnet-view", "SELECT * FROM sdk_journey", cancellationToken: attachDeadline.Token).WaitAsync(TimeSpan.FromSeconds(22));
    try
    {
    var initialRevision = subscription.CurrentSnapshot.Revision;
    var reactiveId = $"{runId}-dotnet-reactive";
    await client.Documents.UpsertAsync(database, collection, reactiveId, new { sdk = "dotnet", runId, value = 4 });
    await client.Documents.PutAsync(database, collection, reactiveId, new { sdk = "dotnet", runId, value = 5 });
    await JourneyChecks.ObserveAsync(subscription.ReadNextAsync,
        () => JourneyChecks.ReactiveValue(subscription.CurrentSnapshot, initialRevision, reactiveId, runId), TimeSpan.FromSeconds(20));
    await JourneyChecks.CancellationAsync(subscription.ReadNextAsync, TimeSpan.FromSeconds(2));
    }
    finally { await subscription.DisposeAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(2)); }
    await client.Documents.DeleteAsync(database, collection, id, new TerraScaleDocumentMutationOptions(IdempotencyKey: $"{runId}-dotnet-delete"));
    Console.WriteLine(JsonSerializer.Serialize(new { operation = "exercise", status = "ok", created = created.Id }));
}
else
{
    await JourneyChecks.CleanupAsync(new[] { own, $"{runId}-dotnet-exercise", $"{runId}-dotnet-reactive" }, async id =>
    {
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        try { await client.Documents.DeleteAsync(database, collection, id, deadline.Token).WaitAsync(TimeSpan.FromSeconds(12)); Console.WriteLine(JsonSerializer.Serialize(new { operation = "cleanup", id, status = "ok" })); }
        catch (TerraScaleApiException ex) when (ex.StatusCode == HttpStatusCode.NotFound) { Console.WriteLine(JsonSerializer.Serialize(new { operation = "cleanup", id, status = "absent" })); }
    });
}
}
catch (Exception ex)
{
    var message = ex.Message;
    foreach (var name in new[] { "TERRASCALE_API_KEY", "TERRASCALE_DENY_API_KEY" })
        if (Environment.GetEnvironmentVariable(name) is { Length: > 0 } secret) message = message.Replace(secret, "[REDACTED]", StringComparison.Ordinal);
    Console.WriteLine(JsonSerializer.Serialize(new { status = "error", error = message.Length > 256 ? message[..256] : message }));
    Environment.ExitCode = 1;
}
