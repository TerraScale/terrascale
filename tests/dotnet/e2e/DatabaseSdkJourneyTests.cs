using System.Net;
using System.Net.Http.Json;
using System.Diagnostics;
using System.Text.Json;
using System.Text.RegularExpressions;
using TerraScale.DotNet;
using TUnit.Core;

namespace TerraScale.Dashboard.E2E;

[ClassDataSource<DashboardE2EFixture>(Shared = SharedType.PerTestSession)]
[NotInParallel("dashboard-demo")]
public sealed class DatabaseSdkJourneyTests(DashboardE2EFixture fixture)
{
    [Test]
    public async Task NativeSignupAndIssuedCustomerCredentialCompleteUnmodifiedPublishedTypeScriptSdkCrud()
    {
        await using var scenario = await DatabaseSdkScenario.StartAsync(fixture.Root);
        using var controls = scenario.CreateProviderClient();
        using var client = scenario.CreateClient();
        await NativeWorkspaceAsync(client);
        using var identityResponse = await client.GetAsync("/api/v1/auth/me");
        identityResponse.EnsureSuccessStatusCode();
        using var identity = JsonDocument.Parse(await identityResponse.Content.ReadAsStringAsync());
        var projectId = identity.RootElement.GetProperty("projectId").GetString()!;
        Check(TerraScaleIds.IsValid(projectId), "Account activation keeps the current producer's canonical ULID project identity");
        var csrf = await HttpJourney.CsrfAsync(client, "/databases");
        string databaseId;
        using (var database = await HttpJourney.PostFormAsync(client, "/api/v1/dashboard/databases", new()
        { ["_csrf"] = csrf, ["name"] = "customer-sdk", ["region"] = "GRU" }))
        {
            Check(database.StatusCode == HttpStatusCode.Created, "Native database creation reaches the real production adapter");
            using var json = JsonDocument.Parse(await database.Content.ReadAsStringAsync()); databaseId = json.RootElement.GetProperty("id").GetString()!;
        }
        using (var collection = await HttpJourney.PostFormAsync(client, "/api/v1/dashboard/data/collections", new()
        { ["_csrf"] = csrf, ["databaseId"] = databaseId, ["name"] = "users" }))
            Check(collection.StatusCode == HttpStatusCode.Created, "The native database has an SDK-backed collection");
        string keyId; string secret;
        using (var credential = await HttpJourney.PostFormAsync(client, "/api/v1/dashboard/credentials/key", new()
        { ["_csrf"] = csrf, ["name"] = "Customer SDK", ["databaseId"] = databaseId, ["scopes"] = "database:read,database:write" }))
        {
            Check(credential.StatusCode == HttpStatusCode.Created, "Native issuance returns an actual producer-scoped customer credential");
            using var json = JsonDocument.Parse(await credential.Content.ReadAsStringAsync());
            keyId = json.RootElement.GetProperty("apiKey").GetProperty("keyId").GetString()!;
            secret = json.RootElement.GetProperty("secret").GetString()!;
        }
        await RunCustomerSdkAsync(fixture.Root, scenario, controls, projectId, "customer-sdk", secret);
        Console.WriteLine("PROTOCOL E2E: native signup/database/collection/customer key and unchanged @terrascale/sdk0.0.3 CRUD succeeded against the test-only current producer compatibility contract; live project-JWT qualification remains outstanding.");
        using (var revoked = await HttpJourney.PostFormAsync(client, "/api/v1/dashboard/credentials/" + keyId + "/revoke", new() { ["_csrf"] = csrf }))
            Check(revoked.StatusCode == HttpStatusCode.NoContent, "Native cleanup revokes the issued customer key at its producer");
        using (var deleted = await HttpJourney.PostFormAsync(client, "/api/v1/dashboard/databases/delete", new()
        { ["_csrf"] = csrf, ["databaseId"] = databaseId, ["confirmName"] = "customer-sdk" }))
            Check(deleted.StatusCode == HttpStatusCode.NoContent, "Native cleanup deletes the canonical database");
        CheckProtocol(await ObservationsAsync(controls));

    }

    [Test]
    public async Task ProductionSdkEncryptsPrivateKeysAndRereadsEnrollmentBeforePublishingCredentialLists()
    {
        await using var scenario = await DatabaseSdkScenario.StartAsync(fixture.Root);
        using var controls = scenario.CreateProviderClient();
        using var client = scenario.CreateClient();
        using var replica = scenario.CreateClient(replica: true);
        var journey = await SignInAsync(client, replica);
        var database = await journey.CallAsync(client, "create_database", new() { ["name"] = "sdk-credentials", ["region"] = "GRU" });
        var id = database.GetProperty("id").GetString()!;
        await ControlAsync(controls, new { command = "pause-key-list" });
        var listing = journey.CallAsync(client, "list_api_keys");
        await WaitAsync(controls, "pausedLists", 1);
        using (var console = await replica.GetAsync("/databases/" + id + "/documents"))
            Check(console.StatusCode == HttpStatusCode.OK, "The real Documents SDK enrolls and opens the database while another replica lists keys");
        await ControlAsync(controls, new { command = "release-key-list" });
        Check((await listing).GetProperty("credentials").GetArrayLength() == 0,
            "A private credential enrolled after producer listing began is hidden by the post-list registry reread");
        var observed = await ObservationsAsync(controls);
        CheckProtocol(observed);
        var privateKey = observed.GetProperty("credentials").EnumerateArray().Single();
        var privateId = privateKey.GetProperty("id").GetString()!;
        var privateSecret = privateKey.GetProperty("secret").GetString()!;
        var encrypted = observed.GetProperty("encrypted").EnumerateArray().Single().GetString()!;
        Check(!encrypted.Contains(privateSecret, StringComparison.Ordinal) && !encrypted.Contains(privateId, StringComparison.Ordinal),
            "Persistent console credentials contain neither the plaintext secret nor its credential ID");
        using (var json = JsonDocument.Parse(encrypted))
            Check(json.RootElement.EnumerateObject().All(field => field.Value.ValueKind == JsonValueKind.String) && json.RootElement.EnumerateObject().Count() == 3,
                "Only the nonce, authenticated ciphertext and tag are persisted");
        var reads = observed.GetProperty("credentialReads").GetInt32();
        using (var console = await client.GetAsync("/databases/" + id + "/documents"))
            Check(console.StatusCode == HttpStatusCode.OK, "The other replica decrypts the existing credential");
        observed = await ObservationsAsync(controls);
        Check(observed.GetProperty("credentials").GetArrayLength() == 1 && observed.GetProperty("encrypted").GetArrayLength() == 1 &&
            observed.GetProperty("credentialReads").GetInt32() > reads, "Cross-replica reuse rereads encrypted state without issuing a second key");
        await journey.ErrorAsync(replica, "revoke_api_key", new() { ["keyId"] = privateId });
        Check((await ObservationsAsync(controls)).GetProperty("credentials").GetArrayLength() == 1,
            "A caller cannot revoke the console's private credential by guessing its ID");

        var issued = await journey.CallAsync(client, "create_api_key", new() { ["name"] = "Public SDK key", ["scopes"] = new[] { "database:read" } });
        var publicId = issued.GetProperty("apiKey").GetProperty("keyId").GetString()!;
        var keys = await journey.CallAsync(replica, "list_api_keys");
        Check(keys.GetProperty("credentials").GetArrayLength() == 1 && keys.GetProperty("credentials")[0].GetProperty("keyId").GetString() == publicId &&
            !keys.GetRawText().Contains(privateSecret, StringComparison.Ordinal), "Only the explicit public credential is exposed without either secret");
        await journey.CallAsync(replica, "revoke_api_key", new() { ["keyId"] = publicId });
        await ControlAsync(controls, new { command = "credential-conflict", value = true });
        var before = (await ObservationsAsync(controls)).GetProperty("documentsRequests").GetInt32();
        var deniedList = await journey.ErrorAsync(client, "list_api_keys");
        var deniedRead = await journey.ErrorAsync(replica, "list_collections", new() { ["databaseId"] = id });
        Check(!deniedList.GetRawText().Contains(privateSecret, StringComparison.Ordinal) && !deniedRead.GetRawText().Contains(privateSecret, StringComparison.Ordinal),
            "A causal credential conflict does not disclose encrypted or decrypted credential material");
        Check((await ObservationsAsync(controls)).GetProperty("documentsRequests").GetInt32() == before,
            "A causal credential conflict fails closed before any customer-data request");
        await ControlAsync(controls, new { command = "credential-conflict", value = false });
        await journey.CallAsync(client, "delete_database", new() { ["databaseId"] = id, ["confirmName"] = "sdk-credentials" });
        CheckProtocol(await ObservationsAsync(controls));
    }

    [Test]
    public async Task ActualSdkRequestsEncodeIdsAndEnforceCrossReplicaDocumentPreconditions()
    {
        await using var scenario = await DatabaseSdkScenario.StartAsync(fixture.Root);
        using var controls = scenario.CreateProviderClient();
        using var client = scenario.CreateClient();
        using var replica = scenario.CreateClient(replica: true);
        var journey = await SignInAsync(client, replica);
        var database = await journey.CallAsync(client, "create_database", new() { ["name"] = "sdk-documents", ["region"] = "GRU" });
        var id = database.GetProperty("id").GetString()!;
        var csrf = await HttpJourney.CsrfAsync(client, "/databases");
        using (var created = await HttpJourney.PostFormAsync(client, "/api/v1/dashboard/data/collections", new()
        { ["_csrf"] = csrf, ["databaseId"] = id, ["name"] = "users" }))
            Check(created.StatusCode == HttpStatusCode.Created, "Native collection creation passes through the official SDK");
        var reference = new Dictionary<string, object?> { ["databaseId"] = id, ["collection"] = "users", ["documentId"] = "customer/space %?#" };
        await journey.CallAsync(client, "create_document", new(reference) { ["document"] = new { score = 1, profile = new { city = "GRU", remove = true } } });
        var original = await journey.CallAsync(replica, "get_document", new(reference) { ["includeVersion"] = true });
        var version = original.GetProperty("version").GetString()!;
        var first = journey.RawAsync(client, "replace_document", new(reference) { ["version"] = version, ["document"] = new { score = 2, profile = new { city = "GRU", remove = true } } });
        var second = journey.RawAsync(replica, "replace_document", new(reference) { ["version"] = version, ["document"] = new { score = 3, profile = new { city = "GRU", remove = true } } });
        var outcomes = await Task.WhenAll(first, second);
        Check(outcomes.Count(IsError) == 1, "Exactly one real SDK conditional replacement wins across running app replicas");
        var current = await journey.CallAsync(replica, "get_document", new(reference) { ["includeVersion"] = true });
        var acceptedVersion = current.GetProperty("version").GetString()!;
        await journey.CallAsync(client, "patch_document", new(reference) { ["patch"] = new { profile = new { city = "SFO", remove = (object?)null } } });
        var patched = await journey.CallAsync(replica, "get_document", reference);
        Check(patched.GetProperty("value").GetProperty("profile").GetProperty("city").GetString() == "SFO" &&
            !patched.GetProperty("value").GetProperty("profile").TryGetProperty("remove", out _), "Legacy patch uses RFC7396 while preserving the current producer precondition");
        using (var native = await HttpJourney.PostFormAsync(client, "/api/v1/dashboard/data/documents", new()
        { ["_csrf"] = csrf, ["databaseId"] = id, ["collection"] = "users", ["operation"] = "replace", ["id"] = "customer/space %?#", ["document"] = "{\"score\":4}" }))
            Check(native.StatusCode == HttpStatusCode.OK, "A native legacy replacement reads and sends the current version");
        var observed = await ObservationsAsync(controls);
        CheckProtocol(observed);
        var requests = observed.GetProperty("requests").EnumerateArray().ToArray();
        var createdDocument = requests.Single(request => request.GetProperty("method").GetString() == "PUT" && request.GetProperty("ifNoneMatch").GetString() == "*");
        Check(createdDocument.GetProperty("path").GetString()!.EndsWith("/documents/by-key", StringComparison.Ordinal) &&
            createdDocument.GetProperty("query").GetString() == "?id=customer%2Fspace%20%25%3F%23",
            "The SDK query-encodes the raw document key exactly once without losing reserved characters");
        var patch = requests.Single(request => request.GetProperty("method").GetString() == "PATCH");
        Check(patch.GetProperty("ifMatch").GetString() == '"' + acceptedVersion + '"' && patch.GetProperty("contentType").GetString() == "application/merge-patch+json",
            "The wire request carries the latest opaque ETag and merge-patch content type");
        var listed = await journey.CallAsync(replica, "list_documents", new() { ["databaseId"] = id, ["collection"] = "users", ["limit"] = 3, ["after"] = "0" });
        Check(listed.GetProperty("documents").GetArrayLength() == 1, "The list uses the official document page response");
        observed = await ObservationsAsync(controls);
        Check(observed.GetProperty("requests").EnumerateArray().Any(request => request.GetProperty("query").GetString() == "?limit=3&cursor=0"),
            "The requested cursor and bounded limit reach the official SDK producer contract");
        await journey.CallAsync(client, "delete_document", reference);
        await journey.CallAsync(client, "delete_database", new() { ["databaseId"] = id, ["confirmName"] = "sdk-documents" });
        CheckProtocol(await ObservationsAsync(controls));
    }

    [Test]
    public async Task QuotaCommitRacesAndMembershipRemovalAreCheckedAfterPatIssuance()
    {
        await using var scenario = await DatabaseSdkScenario.StartAsync(fixture.Root);
        using var controls = scenario.CreateProviderClient();
        using var client = scenario.CreateClient();
        using var replica = scenario.CreateClient(replica: true);
        var journey = await SignInAsync(client, replica);
        await ControlAsync(controls, new { command = "pause-database-creates" });
        await ControlAsync(controls, new { command = "pause-quota-commits" });
        var first = journey.RawAsync(client, "create_database", new() { ["name"] = "quota-first", ["region"] = "GRU" });
        var second = journey.RawAsync(replica, "create_database", new() { ["name"] = "quota-second", ["region"] = "GRU" });
        await WaitAsync(controls, "pausedCreates", 2);
        await ControlAsync(controls, new { command = "release-database-creates" });
        await WaitAsync(controls, "pausedQuotaCommits", 2);
        await ControlAsync(controls, new { command = "release-quota-commits" });
        var results = await Task.WhenAll(first, second);
        Check(results.Count(IsError) == 1, "The canonical free-plan quota admits only one of two concurrent producer creates");
        var listed = (await journey.CallAsync(replica, "list_databases")).GetProperty("databases");
        Check(listed.GetArrayLength() == 1, "Both replicas observe exactly one committed database after the CAS race");
        var committed = listed[0]; var id = committed.GetProperty("id").GetString()!; var name = committed.GetProperty("name").GetString()!;
        Check((await ObservationsAsync(controls)).GetProperty("creates").GetInt32() == 2,
            "The test exercises the post-producer quota commit check, including the known unclaimed producer resource gate");
        Check((await ObservationsAsync(controls)).GetProperty("quotaCasFailures").GetInt32() == 1,
            "Both running replicas commit against the same old version; the real application retries one datastore CAS failure and rereads its exhausted quota");
        var csrf = await HttpJourney.CsrfAsync(client, "/databases");
        using (var created = await HttpJourney.PostFormAsync(client, "/api/v1/dashboard/data/collections", new()
        { ["_csrf"] = csrf, ["databaseId"] = id, ["name"] = "users" })) Check(created.StatusCode == HttpStatusCode.Created, "Prepare a real SDK collection before authoritative membership removal");
        var before = (await ObservationsAsync(controls)).GetProperty("documentsRequests").GetInt32();
        await ControlAsync(controls, new { command = "role", value = "" });
        await journey.ErrorAsync(replica, "get_database", new() { ["databaseId"] = id });
        await journey.ErrorAsync(client, "create_database", new() { ["name"] = "after-revocation", ["region"] = "GRU" });
        await journey.ErrorAsync(replica, "create_document", new() { ["databaseId"] = id, ["collection"] = "users", ["documentId"] = "denied", ["document"] = new { denied = true } });
        await journey.ErrorAsync(client, "create_api_key", new() { ["name"] = "Denied", ["scopes"] = new[] { "database:read" } });
        Check((await ObservationsAsync(controls)).GetProperty("documentsRequests").GetInt32() == before,
            "Previously issued PATs cannot reach customer data after the production workspace SDK rereads removed membership");
        await ControlAsync(controls, new { command = "role", value = "owner" });
        await journey.CallAsync(client, "delete_database", new() { ["databaseId"] = id, ["confirmName"] = name });
        CheckProtocol(await ObservationsAsync(controls));
    }

    private static async Task<Journey> SignInAsync(HttpClient client, HttpClient replica)
    {
        var account = await NativeWorkspaceAsync(client);
        using var other = await HttpJourney.LoginAsync(replica, account.Email, account.Password);
        Check(other.StatusCode == HttpStatusCode.SeeOther, "The other running app replica accepts the newly created account's native sign-in");
        var secret = await HttpJourney.CreatePatAsync(client, "SDK protocol E2E", account.Password);
        using var response = await client.GetAsync("/api/v1/auth/me");
        response.EnsureSuccessStatusCode();
        using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        return new(secret, body.RootElement.GetProperty("organizationId").GetString()!, body.RootElement.GetProperty("projectId").GetString()!);
    }

    internal static async Task<(string Email, string Password)> NativeWorkspaceAsync(HttpClient client)
    {
        var suffix = Guid.NewGuid().ToString("N")[..12];
        var email = "sdk-signup-" + suffix + "@example.test";
        var password = "SDK-signup-" + suffix;
        var csrf = await HttpJourney.CsrfAsync(client, "/onboarding");
        using (var registered = await HttpJourney.PostFormAsync(client, "/onboarding", new()
        {
            ["_csrf"] = csrf,
            ["displayName"] = "Customer SDK " + suffix,
            ["email"] = email,
            ["password"] = password,
            ["confirmPassword"] = password,
            ["terms"] = "true"
        }))
        {
            Check(registered.StatusCode == HttpStatusCode.OK, "Native signup creates a pending email verification");
            csrf = Token(await registered.Content.ReadAsStringAsync());
        }
        using (var verified = await HttpJourney.PostFormAsync(client, "/login/code/verify", new()
        { ["_csrf"] = csrf, ["email"] = email, ["code"] = "123456", ["signUp"] = "1", ["redirectTo"] = "/onboarding" }))
            Check(verified.StatusCode == HttpStatusCode.SeeOther, "Email verification bootstraps canonical producer organization/project IDs through the official management SDK");
        await StepAsync(new() { ["step"] = "account-information", ["displayName"] = "Customer SDK " + suffix });
        await StepAsync(new() { ["step"] = "payment-method", ["bypass"] = "1" });
        await StepAsync(new() { ["step"] = "project", ["name"] = "Customer SDK project", ["region"] = "GRU" });
        return (email, password);

        async Task StepAsync(Dictionary<string, string> form)
        {
            form["_csrf"] = await HttpJourney.CsrfAsync(client, "/onboarding");
            using var response = await HttpJourney.PostFormAsync(client, "/onboarding/step", form);
            Check(response.StatusCode == HttpStatusCode.SeeOther && response.Headers.Location?.OriginalString is "/onboarding" or "/",
                "Native onboarding advances the actual current account step: " + form["step"]);
        }
    }

    internal static async Task RunCustomerSdkAsync(string root, DatabaseSdkScenario scenario, HttpClient controls, string projectId, string databaseName, string secret)
    {
        var start = new ProcessStartInfo("node")
        { WorkingDirectory = root, RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true };
        start.ArgumentList.Add(Path.Combine("tests", "dotnet", "customer-sdk-flow.mjs"));
        using var process = Process.Start(start) ?? throw new IOException("Could not start the pinned public customer SDK journey");
        using var deadline = new CancellationTokenSource(TimeSpan.FromMinutes(5));
        var output = process.StandardOutput.ReadToEndAsync(deadline.Token); var errors = process.StandardError.ReadToEndAsync(deadline.Token);
        try
        {
            await process.StandardInput.WriteLineAsync(JsonSerializer.Serialize(new { projectId, databaseName, secret, providerOrigin = scenario.ProviderOrigin }).AsMemory(), deadline.Token);
            process.StandardInput.Close();
            await process.WaitForExitAsync(deadline.Token);
        }
        catch (OperationCanceledException) when (deadline.IsCancellationRequested)
        {
            throw new TimeoutException("The pinned public customer SDK journey exceeded its five-minute deadline.");
        }
        finally
        {
            if (!process.HasExited)
            {
                try { process.Kill(entireProcessTree: true); }
                catch (InvalidOperationException) when (process.HasExited) { }
            }
            using var cleanup = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            await process.WaitForExitAsync(cleanup.Token);
        }
        var result = await output; var errorText = await errors;
        var observed = await ObservationsAsync(controls);
        Check(process.ExitCode == 0, "Unmodified published customer SDK CRUD: " + errorText +
            " Producer contract failures: " + observed.GetProperty("failures").GetRawText());
        using (var json = JsonDocument.Parse(result))
            Check(json.RootElement.GetProperty("completed").GetBoolean() && json.RootElement.GetProperty("requests").GetInt32() == 8 &&
                json.RootElement.GetProperty("projectId").GetString() == projectId,
                "The actual public npm SDK performs customer CRUD, stale-write rejection and absent-read checks with the producer-owned project and native-issued customer key");
        var publicRequests = observed.GetProperty("requests").EnumerateArray().Where(request => request.GetProperty("authorization").GetString() == "Bearer " + secret).ToArray();
        Check(publicRequests.Length == 8 && publicRequests.All(request => request.GetProperty("path").GetString()!.EndsWith("/documents/by-key", StringComparison.Ordinal)),
            "Every customer SDK request reaches the current producer's point route using its actually issued customer credential");
    }

    private sealed class Journey(string secret, string organization, string project)
    {
        public async Task<JsonElement> RawAsync(HttpClient client, string name, Dictionary<string, object?>? fields = null)
        {
            fields ??= new(); fields.TryAdd("organizationId", organization); fields.TryAdd("projectId", project);
            return await HttpJourney.McpAsync(client, secret, "tools/call", new { name, arguments = fields });
        }
        public async Task<JsonElement> CallAsync(HttpClient client, string name, Dictionary<string, object?>? fields = null)
        {
            var rpc = await RawAsync(client, name, fields);
            Check(!IsError(rpc), "MCP tool must succeed: " + name + " " + rpc.GetRawText());
            return rpc.GetProperty("result").GetProperty("structuredContent");
        }
        public async Task<JsonElement> ErrorAsync(HttpClient client, string name, Dictionary<string, object?>? fields = null)
        { var rpc = await RawAsync(client, name, fields); Check(IsError(rpc), "MCP tool must fail closed: " + name); return rpc; }
    }

    private static bool IsError(JsonElement rpc) => rpc.TryGetProperty("error", out _) || rpc.GetProperty("result").TryGetProperty("isError", out var value) && value.GetBoolean();
    private static async Task ControlAsync(HttpClient client, object command)
    { using var response = await client.PostAsJsonAsync("/__test/control", command); response.EnsureSuccessStatusCode(); }
    private static async Task<JsonElement> ObservationsAsync(HttpClient client)
    { using var response = await client.GetAsync("/__test/observations"); response.EnsureSuccessStatusCode(); using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync()); return json.RootElement.Clone(); }
    private static async Task WaitAsync(HttpClient client, string counter, int expected)
    {
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        while ((await ObservationsAsync(client)).GetProperty(counter).GetInt32() < expected) await Task.Delay(25, deadline.Token);
    }
    private static void CheckProtocol(JsonElement observation) => Check(observation.GetProperty("failures").GetArrayLength() == 0,
        "The producer observed no contract violation: " + observation.GetProperty("failures").GetRawText());
    private static string Token(string html)
    {
        var token = Regex.Match(html, "name=\"_csrf\" value=\"([^\"]+)\"").Groups[1].Value;
        Check(token.Length > 0, "The native form publishes its CSRF token"); return WebUtility.HtmlDecode(token);
    }
    private static void Check(bool condition, string message) { if (!condition) throw new InvalidOperationException(message); }
}
