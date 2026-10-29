using System.Net;
using System.Net.Http.Json;
using System.Reflection;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using TerraScale.Dashboard.Features.Auth;
using TerraScale.DotNet;
using TerraScale.DotNet.TerraBase;
using TUnit.Core;

namespace TerraScale.Dashboard.E2E.Features.Databases;

[ClassDataSource<DashboardRootFixture>]
public sealed class DatabaseSdkJourneyTests(DashboardRootFixture fixture)
{
    [Test]
    public async Task NativeIssuedKeyStaysAtTsAuthAndCustomerSdkCrudUsesAnExchangedProjectToken()
    {
        TestAuth auth = null!;
        await using var scenario = await DatabaseSdkScenario.StartAsync(fixture.Root, CaptureAuth(value => auth = value));
        using var controls = scenario.CreateProviderClient();
        using var client = scenario.CreateClient();
        var account = await NativeWorkspaceAsync(client);
        using var identityResponse = await client.GetAsync("/api/v1/auth/me");
        identityResponse.EnsureSuccessStatusCode();
        using var identity = JsonDocument.Parse(await identityResponse.Content.ReadAsStringAsync());
        var organizationId = identity.RootElement.GetProperty("organizationId").GetString()!;
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
        Check((await ObservationsAsync(controls)).GetProperty("credentials").GetArrayLength() == 0,
            "Database and collection setup issues no hidden project key at ts-auth");
        using (var picker = await client.GetAsync("/api-keys?create=key"))
        {
            var html = await picker.Content.ReadAsStringAsync();
            Check(picker.StatusCode == HttpStatusCode.OK && html.Contains("value=\"documents:read\"", StringComparison.Ordinal) &&
                html.Contains("value=\"documents:read,documents:write\"", StringComparison.Ordinal) &&
                html.Contains("value=\"documents:write\"", StringComparison.Ordinal) &&
                html.Contains("value=\"documents:read,documents:write,databases:read,databases:write\"", StringComparison.Ordinal) &&
                !html.Contains("database:read", StringComparison.Ordinal), "Every picker option submits the canonical scopes");
        }
        foreach (var scope in new[] { "database:read", "database:write", "*", "documents:unknown" })
        {
            using var rejected = await HttpJourney.PostFormAsync(client, "/api/v1/dashboard/credentials/key", new()
            { ["_csrf"] = csrf, ["name"] = "Invalid scope", ["scopes"] = scope });
            Check(rejected.StatusCode == HttpStatusCode.UnprocessableEntity, "JSON issuance rejects the unsupported scope " + scope);
        }
        Check((await ObservationsAsync(controls)).GetProperty("credentials").GetArrayLength() == 0,
            "Rejected scopes issue no credential at ts-auth");
        string keyId; string secret;
        using (var credential = await HttpJourney.PostConfirmedFormAsync(client, "/api/v1/dashboard/credentials/key", new()
        { ["_csrf"] = csrf, ["name"] = "Customer SDK", ["databaseId"] = databaseId, ["scopes"] = "documents:read,documents:write" }))
        {
            Check(credential.StatusCode == HttpStatusCode.Created, "Native issuance returns a ts-auth project key once");
            using var json = JsonDocument.Parse(await credential.Content.ReadAsStringAsync());
            var key = json.RootElement.GetProperty("apiKey");
            keyId = key.GetProperty("keyId").GetString()!;
            secret = json.RootElement.GetProperty("secret").GetString()!;
            Check(TerraScaleIds.IsValid(keyId) && secret.StartsWith(keyId + "_", StringComparison.Ordinal) &&
                key.GetProperty("databaseName").GetString() == "customer-sdk" &&
                string.Join(',', key.GetProperty("scopes").EnumerateArray().Select(value => value.GetString()).Order(StringComparer.Ordinal)) == "documents:read,documents:write",
                "The revealed key keeps its ts-auth identity, database restriction and documents:* scopes");
        }
        var issued = (await ObservationsAsync(controls)).GetProperty("credentials").EnumerateArray().Single();
        Check(issued.GetProperty("id").GetString() == keyId && issued.GetProperty("database").GetString() == databaseId &&
            string.Join(',', issued.GetProperty("scopes").EnumerateArray().Select(value => value.GetString()).Order(StringComparer.Ordinal)) == "documents:read,documents:write",
            "ts-auth holds exactly the requested restrictions; the dashboard did not broaden them");
        await RunCustomerSdkAsync(fixture.Root, scenario, controls, auth, account, organizationId, projectId, "customer-sdk", secret);
        Console.WriteLine("PROTOCOL E2E: native signup/database/collection, a ts-auth issued key refused as a data bearer, and unchanged official TerraScale.DotNet SDK CRUD under the customer's exchanged documents:* project token, in-process against test-only producers; the key's client-credentials exchange and live qualification remain outstanding.");
        using (var rotated = await HttpJourney.PostConfirmedFormAsync(client, "/api/v1/dashboard/credentials/rotate", new() { ["_csrf"] = csrf, ["keyId"] = keyId, ["previousSecretTtl"] = "60" }))
        {
            Check(rotated.StatusCode == HttpStatusCode.OK, "Rotation returns the key's new secret once");
            using var json = JsonDocument.Parse(await rotated.Content.ReadAsStringAsync());
            var replacement = json.RootElement.GetProperty("secret").GetString()!;
            Check(json.RootElement.GetProperty("apiKey").GetProperty("keyId").GetString() == keyId && replacement.StartsWith(keyId + "_", StringComparison.Ordinal) && replacement != secret &&
                (await ObservationsAsync(controls)).GetProperty("credentials").EnumerateArray().Single().GetProperty("secret").GetString() == replacement,
                "Rotation keeps the key identity and replaces its secret at ts-auth");
        }
        using (var pending = await HttpJourney.PostConfirmedFormAsync(client, "/api/v1/dashboard/credentials/rotate", new() { ["_csrf"] = csrf, ["keyId"] = keyId }))
            Check(pending.StatusCode == HttpStatusCode.Conflict, "A second rotation during the old secret's grace period is refused");
        using (var revoked = await HttpJourney.PostFormAsync(client, "/api/v1/dashboard/credentials/" + keyId + "/revoke", new() { ["_csrf"] = csrf }))
            Check(revoked.StatusCode == HttpStatusCode.NoContent, "Native cleanup revokes the issued key at ts-auth");
        Check((await ObservationsAsync(controls)).GetProperty("credentials").GetArrayLength() == 0, "The revoked key is gone at its issuer");
        using (var full = await HttpJourney.PostConfirmedFormAsync(client, "/api/v1/dashboard/credentials/key", new()
        { ["_csrf"] = csrf, ["name"] = "Seed script", ["scopes"] = "documents:read,documents:write,databases:read,databases:write" }))
        {
            Check(full.StatusCode == HttpStatusCode.Created, "A Full access key is issued for seed and migration scripts");
            using var json = JsonDocument.Parse(await full.Content.ReadAsStringAsync());
            var key = json.RootElement.GetProperty("apiKey");
            Check(string.Join(',', key.GetProperty("scopes").EnumerateArray().Select(value => value.GetString()).Order(StringComparer.Ordinal)) == "databases:read,databases:write,documents:read,documents:write",
                "Full access adds database management to documents:*");
            using var revoked = await HttpJourney.PostFormAsync(client, "/api/v1/dashboard/credentials/" + key.GetProperty("keyId").GetString() + "/revoke", new() { ["_csrf"] = csrf });
            Check(revoked.StatusCode == HttpStatusCode.NoContent, "The Full access key is revoked");
        }
        using (var deleted = await HttpJourney.PostFormAsync(client, "/api/v1/dashboard/databases/delete", new()
        { ["_csrf"] = csrf, ["databaseId"] = databaseId, ["confirmName"] = "customer-sdk" }))
            Check(deleted.StatusCode == HttpStatusCode.NoContent, "Native cleanup deletes the canonical database");
        var observed = await ObservationsAsync(controls);
        CheckNoStoredDataKey(observed);
        CheckProtocol(observed);
    }

    [Test]
    public async Task DataCallsExchangeActionScopedSessionTokensAndNoHiddenKeyIsIssuedOrStored()
    {
        TestAuth auth = null!;
        await using var scenario = await DatabaseSdkScenario.StartAsync(fixture.Root, CaptureAuth(value => auth = value));
        using var controls = scenario.CreateProviderClient();
        using var client = scenario.CreateClient();
        using var replica = scenario.CreateClient(replica: true);
        var journey = await SignInAsync(client, replica);
        var database = await journey.CallAsync(client, "create_database", new() { ["name"] = "sdk-credentials", ["region"] = "GRU" });
        var id = database.GetProperty("id").GetString()!;
        var csrf = await HttpJourney.CsrfAsync(client, "/databases");
        using (var created = await HttpJourney.PostFormAsync(client, "/api/v1/dashboard/data/collections", new()
        { ["_csrf"] = csrf, ["databaseId"] = id, ["name"] = "users" }))
            Check(created.StatusCode == HttpStatusCode.Created, "Native collection creation passes through the official SDK");
        await ControlAsync(controls, new { command = "pause-key-list" });
        var listing = journey.CallAsync(client, "list_api_keys");
        await WaitAsync(controls, "pausedLists", 1);
        using (var console = await replica.GetAsync("/databases/" + id + "/documents"))
            Check(console.StatusCode == HttpStatusCode.OK, "The other replica opens the documents console while ts-auth key listing is in flight");
        await ControlAsync(controls, new { command = "release-key-list" });
        Check((await listing).GetProperty("credentials").GetArrayLength() == 0,
            "Opening the console enrolls no private key: the ts-auth listing stays empty");

        var reference = new Dictionary<string, object?> { ["databaseId"] = id, ["collection"] = "users", ["documentId"] = "01K78D0C000000000000000016" };
        await journey.CallAsync(client, "create_document", new(reference) { ["document"] = new { scoped = true } });
        await journey.CallAsync(replica, "get_document", reference);
        var observed = await ObservationsAsync(controls);
        CheckProtocol(observed);
        CheckNoStoredDataKey(observed);
        Check(observed.GetProperty("credentials").GetArrayLength() == 0, "Document reads and writes issue no ts-auth key");
        var bearers = observed.GetProperty("requests").EnumerateArray().Select(request =>
        {
            var authorization = request.GetProperty("authorization").GetString()!;
            var grant = authorization.StartsWith("Bearer ", StringComparison.Ordinal) ? auth.ResolveProjectToken(authorization[7..]) : null;
            var scope = request.GetProperty("method").GetString() == "GET" || request.GetProperty("path").GetString()!.EndsWith("/query", StringComparison.Ordinal) ? "documents:read" : "documents:write";
            Check(grant is { } admitted && admitted.UserId == journey.User && admitted.OrganizationId == journey.Organization &&
                admitted.ProjectId == journey.Project && admitted.Scopes.SequenceEqual([scope]),
                "Each data request carries a current-session project token for exactly its tenant, project and " + scope);
            return authorization;
        }).ToArray();
        Check(bearers.Length >= 2 && bearers.Distinct(StringComparer.Ordinal).Count() == bearers.Length,
            "Every action exchanges its own token; no project-wide data credential is cached and reused");

        await journey.ErrorAsync(replica, "revoke_api_key", new() { ["keyId"] = new TerraScaleIdGenerator().NewId() });
        Check((await ObservationsAsync(controls)).GetProperty("credentials").GetArrayLength() == 0,
            "Revoking a guessed key ID is refused by ts-auth and changes nothing");
        await journey.ErrorAsync(client, "create_api_key", new() { ["name"] = "Foreign", ["scopes"] = new[] { "documents:read" }, ["projectId"] = new TerraScaleIdGenerator().NewId() });
        Check((await ObservationsAsync(controls)).GetProperty("credentials").GetArrayLength() == 0,
            "A key is never issued outside the signed-in tenant's selected project");
        foreach (var scope in new[] { "database:read", "database:write", "*", "documents:unknown" })
            await journey.ErrorAsync(client, "create_api_key", new() { ["name"] = "Invalid scope", ["scopes"] = new[] { scope } });
        Check((await ObservationsAsync(controls)).GetProperty("credentials").GetArrayLength() == 0,
            "MCP refuses unsupported scopes before issuing any key");
        await journey.ErrorAsync(client, "create_api_key", new() { ["name"] = "Public SDK key", ["scopes"] = new[] { "documents:read" }, ["databaseId"] = id });
        using var issuedResponse = await HttpJourney.PostConfirmedFormAsync(client, "/api/v1/dashboard/credentials/key", new()
        { ["_csrf"] = csrf, ["name"] = "Public SDK key", ["scopes"] = "documents:read", ["databaseId"] = id });
        var issued = await issuedResponse.Content.ReadFromJsonAsync<JsonElement>();
        var publicId = issued.GetProperty("apiKey").GetProperty("keyId").GetString()!;
        var publicSecret = issued.GetProperty("secret").GetString()!;
        var held = (await ObservationsAsync(controls)).GetProperty("credentials").EnumerateArray().Single();
        Check(held.GetProperty("id").GetString() == publicId && held.GetProperty("database").GetString() == id &&
            held.GetProperty("scopes").EnumerateArray().Select(value => value.GetString()).SequenceEqual(["documents:read"]),
            "ts-auth issues the explicit key with exactly its read scope and database restriction");
        var keys = await journey.CallAsync(replica, "list_api_keys");
        Check(keys.GetProperty("credentials").GetArrayLength() == 1 && keys.GetProperty("credentials")[0].GetProperty("keyId").GetString() == publicId &&
            !keys.GetRawText().Contains(publicSecret, StringComparison.Ordinal), "The other replica lists the explicit key from ts-auth without its secret");
        await journey.CallAsync(replica, "revoke_api_key", new() { ["keyId"] = publicId });
        Check((await ObservationsAsync(controls)).GetProperty("credentials").GetArrayLength() == 0, "Explicit revocation reaches ts-auth");
        CheckNoStoredDataKey(await ObservationsAsync(controls));

        var keyRequests = (await ObservationsAsync(controls)).GetProperty("authKeys").GetProperty("requests").EnumerateArray().ToArray();
        Check(keyRequests.Length > 0 && keyRequests.All(request =>
        {
            var authorization = request.GetProperty("authorization").GetString()!;
            return request.GetProperty("bearerKind").GetString() == "original-session" &&
                authorization.StartsWith("Bearer ", StringComparison.Ordinal) && auth.ResolveAccessToken(authorization[7..])?.UserId == journey.User &&
                auth.ResolveProjectToken(authorization[7..]) is null;
        }), "Every project-key call uses the original admitted user session, never an exchanged data bearer");

        var beforeFailure = (await ObservationsAsync(controls)).GetProperty("documentsRequests").GetInt32();
        auth.ExchangeError = new HttpRequestException("Synthetic token exchange unavailable", null, HttpStatusCode.ServiceUnavailable);
        try
        {
            await journey.ErrorAsync(replica, "get_document", reference);
            Check((await ObservationsAsync(controls)).GetProperty("documentsRequests").GetInt32() == beforeFailure,
                "A token exchange failure keeps the sign-in alive and sends no document request or stored-key fallback");
        }
        finally { auth.ExchangeError = null; }
        await journey.CallAsync(replica, "get_document", reference);

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
        var reference = new Dictionary<string, object?> { ["databaseId"] = id, ["collection"] = "users", ["documentId"] = "01K78D0C000000000000000017" };
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
        { ["_csrf"] = csrf, ["databaseId"] = id, ["collection"] = "users", ["operation"] = "replace", ["id"] = "01K78D0C000000000000000017", ["document"] = "{\"score\":4}" }))
            Check(native.StatusCode == HttpStatusCode.OK, "A native legacy replacement reads and sends the current version");
        var observed = await ObservationsAsync(controls);
        CheckProtocol(observed);
        var requests = observed.GetProperty("requests").EnumerateArray().ToArray();
        var createdDocument = requests.Single(request => request.GetProperty("method").GetString() == "PUT" && request.GetProperty("ifNoneMatch").GetString() == "*");
        Check(createdDocument.GetProperty("path").GetString()!.EndsWith("/documents/by-key", StringComparison.Ordinal) &&
            createdDocument.GetProperty("query").GetString() == "?id=01K78D0C000000000000000017",
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
        var name = results.Single(result => !IsError(result)).GetProperty("result").GetProperty("structuredContent").GetProperty("name").GetString()!;
        var listed = (await journey.CallAsync(replica, "list_databases")).GetProperty("databases");
        // TerraBase owns databases: the one it made before the quota commit refused it is listed (and counted) as well.
        Check(listed.GetArrayLength() == 2, "Both replicas list every database TerraBase holds after the CAS race");
        var id = listed.EnumerateArray().Single(database => database.GetProperty("name").GetString() == name).GetProperty("id").GetString()!;
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
        await journey.ErrorAsync(replica, "create_document", new() { ["databaseId"] = id, ["collection"] = "users", ["documentId"] = "01K78D0C000000000000000018", ["document"] = new { denied = true } });
        await journey.ErrorAsync(client, "create_api_key", new() { ["name"] = "Denied", ["scopes"] = new[] { "documents:read" } });
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
        return new(secret, body.RootElement.GetProperty("userId").GetString()!, body.RootElement.GetProperty("organizationId").GetString()!,
            body.RootElement.GetProperty("projectId").GetString()!);
    }

    internal static async Task<(string Email, string Password)> NativeWorkspaceAsync(HttpClient client)
    {
        var suffix = Guid.NewGuid().ToString("N")[..12];
        var email = "sdk-signup-" + suffix + "@example.test";
        var password = "SDK-signup-" + suffix;
        // Accounts are created on ts-auth's hosted sign-up; the callback starts the dashboard session.
        using (var verified = await HttpJourney.LoginAsync(client, email, password, "/onboarding", signUp: true))
            Check(verified.StatusCode == HttpStatusCode.SeeOther, "Hosted sign-up bootstraps canonical producer organization/project IDs through the official management SDK");
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

    // A ts-auth issued key is not a TerraBase bearer. Until the client-credentials exchange fake exists, the customer's
    // own sign-in is exchanged at the same ts-auth boundary for exactly documents:read/write on this project.
    internal static async Task RunCustomerSdkAsync(string root, DatabaseSdkScenario scenario, HttpClient controls, TestAuth auth,
        (string Email, string Password) account, string organizationId, string projectId, string databaseName, string issuedSecret)
    {
        using (var provenance = JsonDocument.Parse(await File.ReadAllTextAsync(Path.Combine(root, "vendor", "dotnet", "provenance.json"))))
        {
            var pinned = provenance.RootElement.GetProperty("version").GetString()!;
            var loaded = typeof(TerraBaseDocumentsClient).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()!.InformationalVersion;
            Check(loaded == pinned || loaded.StartsWith(pinned + "+", StringComparison.Ordinal), "Use the exact official .NET SDK pinned by the checkout: " + loaded);
            Check(provenance.RootElement.GetProperty("assemblySha256ByFramework").TryGetProperty("net" + Environment.Version.ToString(2), out _),
                "Run customer SDK qualification on the pinned SDK's supported .NET runtime");
        }
        Check(TerraScaleIds.IsValid(projectId), "Use the canonical ULID project returned by the current producer, without translating it");
        using var deadline = new CancellationTokenSource(TimeSpan.FromMinutes(5));
        var rawTransport = new CustomerTransport(scenario.ProviderOrigin);
        using (var rawHttp = new HttpClient(rawTransport))
        {
            try
            {
                await Customer(rawHttp, issuedSecret).Database(projectId, databaseName).GetDocumentAsync("users", "01K78D0C000000000000000019", deadline.Token);
                throw new InvalidOperationException("A raw issued key must not be accepted as a data-plane bearer");
            }
            catch (TerraBaseAuthenticationException error)
            {
                Check(error.StatusCode == HttpStatusCode.Unauthorized && rawTransport.LastStatus == HttpStatusCode.Unauthorized &&
                    JsonDocument.Parse(rawTransport.LastBody).RootElement.GetProperty("error").GetProperty("code").GetString() == "unauthorized",
                    "The canonical data API answers 401 unauthorized to the raw ts-auth key");
            }
        }
        var session = await auth.ExchangeAsync(auth.Authorize(account.Email, account.Password, false)["code=".Length..], null!, deadline.Token);
        var token = await auth.GetAsync(session, organizationId, projectId, ["documents:read", "documents:write"], deadline.Token);
        var readOnly = await auth.GetAsync(session, organizationId, projectId, ["documents:read"], deadline.Token);
        Check(auth.ResolveProjectToken(token) is { } grant && grant.OrganizationId == organizationId && grant.ProjectId == projectId &&
            grant.Scopes.SequenceEqual(["documents:read", "documents:write"]), "The exchanged customer token holds exactly this project and documents:* scopes");
        var transport = new CustomerTransport(scenario.ProviderOrigin);
        using var http = new HttpClient(transport);
        var database = Customer(http, token).Database(projectId, databaseName);
        var id = new TerraScaleIdGenerator().NewId();
        try
        {
            var created = await database.PutDocumentAsync("users", id, JsonSerializer.SerializeToElement(new { title = "Customer SDK flow", done = false }), TerraBasePrecondition.Absent, deadline.Token);
            Check(created.Id == id && created.Created && created.Offset >= 0, "A create-only write returns the new key, a create and a canonical offset");
            var document = await database.GetDocumentAsync("users", id, deadline.Token) ?? throw new InvalidOperationException("The created document reads back");
            Check(document.Version == created.Version, "The read returns the created version");
            Check(JsonNode.DeepEquals(JsonNode.Parse(document.Document.GetRawText()), JsonNode.Parse("""{"title":"Customer SDK flow","done":false}""")), "The read returns the created document");
            var patched = await database.PatchDocumentAsync("users", id, JsonSerializer.SerializeToElement(new { done = true }), TerraBasePrecondition.Matches(document.Version), deadline.Token);
            var updated = await database.GetDocumentAsync("users", id, deadline.Token) ?? throw new InvalidOperationException("The patched document reads back");
            Check(updated.Version == patched.Version && updated.Document.GetProperty("done").GetBoolean() &&
                updated.Document.GetProperty("title").GetString() == "Customer SDK flow", "The merge patch sets done and keeps the title");
            try
            {
                await database.PutDocumentAsync("users", id, JsonSerializer.SerializeToElement(new { title = "stale" }), TerraBasePrecondition.Matches(document.Version), deadline.Token);
                throw new InvalidOperationException("A stale write must be rejected");
            }
            catch (TerraBasePreconditionFailedException error)
            {
                Check(error.StatusCode == HttpStatusCode.PreconditionFailed && error.ErrorCode == "precondition_failed", "A stale write fails with 412 precondition_failed");
            }
            var replaced = await database.PutDocumentAsync("users", id, JsonSerializer.SerializeToElement(new { title = "Customer SDK replaced", done = true }), TerraBasePrecondition.Matches(updated.Version), deadline.Token);
            Check(!replaced.Created, "Replacing an existing document does not report a create");
            var deleted = await database.DeleteDocumentAsync("users", id, TerraBasePrecondition.Matches(replaced.Version), deadline.Token);
            Check(deleted.Id == id, "The delete returns the requested document key");
            // The .NET SDK maps an absent read to null, so the 404 not_found wire response is checked at the transport.
            Check(await database.GetDocumentAsync("users", id, deadline.Token) is null && transport.LastStatus == HttpStatusCode.NotFound &&
                JsonDocument.Parse(transport.LastBody).RootElement.GetProperty("error").GetProperty("code").GetString() == "not_found",
                "An absent read fails with 404 not_found");
        }
        catch (OperationCanceledException) when (deadline.IsCancellationRequested)
        {
            throw new TimeoutException("The pinned official customer SDK journey exceeded its five-minute deadline.");
        }
        catch (Exception error)
        {
            throw new InvalidOperationException("Unmodified official customer SDK CRUD: " + error.Message +
                " Producer contract failures: " + (await ObservationsAsync(controls)).GetProperty("failures").GetRawText(), error);
        }
        Check(transport.Requests == 8,
            "The official .NET SDK performs customer CRUD, stale-write rejection and absent-read checks with the exchanged project token");
        var readTransport = new CustomerTransport(scenario.ProviderOrigin);
        using (var readHttp = new HttpClient(readTransport))
        {
            try
            {
                await Customer(readHttp, readOnly).Database(projectId, databaseName).PutDocumentAsync("users", id,
                    JsonSerializer.SerializeToElement(new { title = "denied" }), TerraBasePrecondition.Absent, deadline.Token);
                throw new InvalidOperationException("A documents:read token must not write");
            }
            catch (TerraBaseForbiddenException error)
            {
                Check(error.StatusCode == HttpStatusCode.Forbidden && readTransport.LastStatus == HttpStatusCode.Forbidden,
                    "The canonical data API answers 403 to a write under a read-only exchanged token");
            }
        }
        var observed = await ObservationsAsync(controls);
        var requests = observed.GetProperty("requests").EnumerateArray().ToArray();
        var customerRequests = requests.Where(request => request.GetProperty("authorization").GetString() == "Bearer " + token).ToArray();
        Check(customerRequests.Length == 8 && customerRequests.All(request => request.GetProperty("path").GetString()!.EndsWith("/documents/by-key", StringComparison.Ordinal)),
            "Every customer SDK request reaches the current producer's point route with the exchanged project token");
        Check(!requests.Any(request => request.GetProperty("authorization").GetString()!.Contains(issuedSecret, StringComparison.Ordinal)),
            "The producer never admitted a request carrying the raw issued key");
    }

    private static TerraBaseDocumentsClient Customer(HttpClient http, string bearer) => new(http, new TerraBaseDocumentsClientOptions
    { BaseAddress = new Uri("https://gru.api.terrascale.tech"), AccessTokenProvider = _ => ValueTask.FromResult(bearer) });

    // Each scenario owns one TestAuth shared by its host pair; tests read its exchange grants to check bearers exactly.
    internal static Action<WebApplicationBuilder> CaptureAuth(Action<TestAuth> captured) => application =>
        captured((TestAuth)application.Services.Last(item => item.ServiceType == typeof(IAuthGateway)).ImplementationInstance!);

    // A producer fault stays on the pinned home and a current-session bearer exchanged for exactly this tenant, project and scope.
    internal static void DemandProjectToken(TestAuth auth, HttpContext http, string organizationId, string projectId, string scope)
    {
        var text = http.Request.Headers.Authorization.ToString();
        var grant = text.StartsWith("Bearer ", StringComparison.Ordinal) ? auth.ResolveProjectToken(text[7..]) : null;
        if (http.Request.Host.Host != "gru.api.terrascale.tech" || grant is not { } admitted || admitted.OrganizationId != organizationId ||
            admitted.ProjectId != projectId || !admitted.Scopes.SequenceEqual([scope]))
            throw new InvalidOperationException("A producer fault must stay on the home region with an exchanged " + scope + " bearer for this project.");
    }

    // Lets a fault observe or rewrite the real producer's answer: the request is replayed once to the producer, marked so
    // the extension passes it through, and the answer keeps the producer's region headers.
    internal const string Forwarded = "X-Test-Forwarded";
    internal static async Task<(int Status, JsonNode? Body)> ForwardAsync(HttpContext http, Uri producer)
    {
        http.Request.EnableBuffering();
        using var body = new MemoryStream();
        await http.Request.Body.CopyToAsync(body, http.RequestAborted);
        using var request = new HttpRequestMessage(new HttpMethod(http.Request.Method), new Uri(producer, http.Request.Path + http.Request.QueryString));
        if (body.Length > 0) request.Content = new ByteArrayContent(body.ToArray());
        foreach (var header in http.Request.Headers)
            if (!header.Key.StartsWith("Content-", StringComparison.OrdinalIgnoreCase) && header.Key is not ("Host" or "Transfer-Encoding" or "Connection"))
                request.Headers.TryAddWithoutValidation(header.Key, header.Value.ToArray());
        if (request.Content is not null && http.Request.ContentType is { } type) request.Content.Headers.TryAddWithoutValidation("Content-Type", type);
        request.Headers.Host = http.Request.Host.Value;
        request.Headers.Add(Forwarded, "1");
        using var client = new HttpClient(new HttpClientHandler { ServerCertificateCustomValidationCallback = TestHttps.Validate, AllowAutoRedirect = false });
        using var response = await client.SendAsync(request, http.RequestAborted);
        foreach (var header in response.Headers.Where(header => header.Key.StartsWith("X-TerraBase-", StringComparison.OrdinalIgnoreCase)))
            http.Response.Headers[header.Key] = header.Value.ToArray();
        var text = await response.Content.ReadAsStringAsync(http.RequestAborted);
        return ((int)response.StatusCode, text.Length == 0 ? null : JsonNode.Parse(text));
    }

    // The removed data-key registry collection stays untouched: no hidden durable key is written or read.
    private static void CheckNoStoredDataKey(JsonElement observation) =>
        Check(observation.GetProperty("encrypted").GetArrayLength() == 0 && observation.GetProperty("credentialReads").GetInt32() == 0,
            "The dashboard neither writes nor reads a stored data credential");

    // Only the physical destination changes for the local HTTP producer; project/document IDs, routes, headers and bodies remain SDK-owned.
    private sealed class CustomerTransport(Uri origin) : DelegatingHandler(new HttpClientHandler { ServerCertificateCustomValidationCallback = TestHttps.Validate, AllowAutoRedirect = false })
    {
        public int Requests;
        public HttpStatusCode LastStatus;
        public string LastBody = "";
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var original = request.RequestUri!;
            Check(original.GetLeftPart(UriPartial.Authority) == "https://gru.api.terrascale.tech", "The SDK addresses the public GRU producer origin");
            Requests++;
            request.Headers.Host = original.Authority;
            request.RequestUri = new(origin, original.PathAndQuery);
            var response = await base.SendAsync(request, cancellationToken);
            LastStatus = response.StatusCode;
            LastBody = await response.Content.ReadAsStringAsync(cancellationToken);
            return response;
        }
    }

    private sealed class Journey(string secret, string user, string organization, string project)
    {
        public string User => user;
        public string Organization => organization;
        public string Project => project;
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
