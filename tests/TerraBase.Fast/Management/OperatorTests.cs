using System.Collections.Concurrent;
using System.Net;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using TerraBase.Primitives;
using TerraBase.Testing;

namespace TerraBase.Fast.Management;

/// <summary>
/// The platform operator: the internal listener authenticates the peer credential and acts as the user it names.
/// It bootstraps the Management API before ts-auth exists.
/// </summary>
public sealed class OperatorTests
{
    private const string UserHeader = "X-TerraBase-User-Id";
    private const string Organizations = "/v1/management/organizations";

    [Test]
    public async Task The_operator_bootstraps_organizations_on_the_internal_listener_before_ts_auth_answers()
    {
        // TS-529: the node is configured for ts-auth, which does not run yet, so no user token can work.
        using var tls = new ClientTls();
        using var issuer = new TestIssuer { Available = false };
        await using var server = await TerraBaseTestServer.StartAsync(
            settings =>
            {
                tls.Apply(settings);
                TestIssuer.Configure(settings);
            },
            issuer.Route);
        var user = EntityIds.New().ToString();

        using var created = await SendAsync(server.Client, HttpMethod.Post, Organizations, null, user, new { name = "terrascale" });
        var organization = await created.Content.ReadFromJsonAsync<JsonElement>();
        var id = organization.GetProperty("id").GetString();
        using var project = await SendAsync(server.Client, HttpMethod.Post, $"{Organizations}/{id}/projects", null, user, new { name = "ts-auth" });
        using var ready = await server.Client.GetAsync(new Uri("/readyz", UriKind.Relative));

        await Assert.That(created.StatusCode).IsEqualTo(HttpStatusCode.Created);
        await Assert.That(organization.GetProperty("createdBy").GetString()).IsEqualTo(user);
        await Assert.That(project.StatusCode).IsEqualTo(HttpStatusCode.Created);
        // Ready without ts-auth's keys: ts-auth can only start once its project here serves.
        await Assert.That(ready.StatusCode).IsEqualTo(HttpStatusCode.OK);
    }

    [Test]
    public async Task The_operator_reads_and_writes_a_projects_documents_on_the_internal_listener_with_the_peer_credential()
    {
        // ts-auth's own storage: it reaches TerraBase over NetBird with the peer credential.
        await using var api = await ManagementApi.StartAsync();
        var database = await api.CreateManagedDatabaseAsync("auth");
        using var storage = PeerTls.CreateClient(api.Server.BaseAddress);
        await ManagementApi.CreateCollectionAsync(storage, database, "users");
        var path = ManagementApi.Uri(ManagementApi.DocumentPath(database, "users", "ada"));

        using var written = await storage.PutAsJsonAsync(path, new { name = "Ada" });
        using var read = await storage.GetAsync(path);
        using var anonymous = api.WithToken(null);
        using var onPublic = await anonymous.GetAsync(path);
        using var unknownProject = await storage.GetAsync(ManagementApi.Uri($"/v1/projects/{EntityIds.New()}/databases/auth/collections/users/documents"));
        using var managementWithoutUser = await storage.GetAsync(ManagementApi.Uri(Organizations));

        await Assert.That(written.IsSuccessStatusCode).IsTrue();
        await Assert.That(read.StatusCode).IsEqualTo(HttpStatusCode.OK);
        await Assert.That((await read.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("document").GetProperty("name").GetString()).IsEqualTo("Ada");
        await Assert.That(onPublic.StatusCode).IsEqualTo(HttpStatusCode.Unauthorized);
        await Assert.That(unknownProject.StatusCode).IsEqualTo(HttpStatusCode.Unauthorized);
        await Assert.That(managementWithoutUser.StatusCode).IsEqualTo(HttpStatusCode.Unauthorized);
    }

    [Test]
    public async Task The_operator_is_refused_on_the_public_listener_and_tokens_on_the_internal_listener()
    {
        await using var api = await ManagementApi.StartAsync(settings => { });
        using var client = api.WithToken(null);
        var user = EntityIds.New().ToString();

        using var onAdmin = await SendAsync(api.Server.Client, HttpMethod.Get, Organizations, null, user);
        using var onClient = await SendAsync(client, HttpMethod.Get, Organizations, null, user);
        using var tokenOnAdmin = await SendAsync(api.Server.Client, HttpMethod.Get, Organizations, api.Issuer.Mint(user), user: null);
        using var tokenOnClient = await SendAsync(client, HttpMethod.Get, Organizations, api.Issuer.Mint(user), user: null);

        await Assert.That(onAdmin.StatusCode).IsEqualTo(HttpStatusCode.OK);
        await Assert.That(onClient.StatusCode).IsEqualTo(HttpStatusCode.Unauthorized);
        await Assert.That(tokenOnAdmin.StatusCode).IsEqualTo(HttpStatusCode.Unauthorized);
        await Assert.That(tokenOnClient.StatusCode).IsEqualTo(HttpStatusCode.OK);
    }

    [Test]
    public async Task The_operator_adds_a_member_who_then_takes_ownership()
    {
        // TS-535: the TS-529 bootstrap hands the organization it created to a person, before ts-auth exists.
        await using var server = await TerraBaseTestServer.StartAsync(settings => { });
        var bootstrap = EntityIds.New().ToString();
        var user = EntityIds.New().ToString();
        var email = $"{EntityIds.New()}@example.com";
        using var created = await SendAsync(server.Client, HttpMethod.Post, Organizations, null, bootstrap, new { name = "terrascale" });
        var organization = $"{Organizations}/{(await created.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetString()}";

        // Re-adding the sole owner must not demote it.
        using var owner = await SendAsync(server.Client, HttpMethod.Put, $"{organization}/members/{bootstrap}", null, bootstrap, new { email });
        using var added = await SendAsync(server.Client, HttpMethod.Put, $"{organization}/members/{user}", null, bootstrap, new { email });
        using var again = await SendAsync(server.Client, HttpMethod.Put, $"{organization}/members/{user}", null, bootstrap, new { email });
        using var transfer = await SendAsync(server.Client, HttpMethod.Post, $"{organization}/ownership-transfer", null, bootstrap, new { userId = user });
        using var confirmed = await SendAsync(server.Client, HttpMethod.Post, $"{organization}/ownership-transfer/confirm", null, user);
        // Confirming makes the previous owner a member; the new owner makes the bootstrap identity an admin.
        using var promoted = await SendAsync(server.Client, HttpMethod.Put, $"{organization}/members/{bootstrap}/role", null, user, new { role = "admin" });
        using var members = await SendAsync(server.Client, HttpMethod.Get, $"{organization}/members", null, user);
        using var audit = await SendAsync(server.Client, HttpMethod.Get, $"{organization}/audit-events", null, user);
        var member = await added.Content.ReadFromJsonAsync<JsonElement>();
        var roles = (await members.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("items").EnumerateArray()
            .ToDictionary(item => item.GetProperty("userId").GetString()!, item => item.GetProperty("role").GetString());
        var additions = (await audit.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("items").EnumerateArray()
            .Where(item => item.GetProperty("action").GetString() == "member.added")
            .Select(item => (Actor: item.GetProperty("actor").GetString()!, Target: item.GetProperty("target").GetString()!, Detail: item.GetProperty("detail").GetString()!))
            .ToList();

        await Assert.That(owner.StatusCode).IsEqualTo(HttpStatusCode.OK);
        await Assert.That((await owner.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("role").GetString()).IsEqualTo("owner");
        await Assert.That(added.StatusCode).IsEqualTo(HttpStatusCode.OK);
        await Assert.That(member.GetProperty("userId").GetString()).IsEqualTo(user);
        await Assert.That(member.GetProperty("role").GetString()).IsEqualTo("member");
        await Assert.That(again.StatusCode).IsEqualTo(HttpStatusCode.OK);
        await Assert.That(await again.Content.ReadAsStringAsync()).IsEqualTo(member.GetRawText());
        await Assert.That(transfer.StatusCode).IsEqualTo(HttpStatusCode.Created);
        await Assert.That(confirmed.StatusCode).IsEqualTo(HttpStatusCode.OK);
        await Assert.That(promoted.StatusCode).IsEqualTo(HttpStatusCode.OK);
        await Assert.That(roles).IsEquivalentTo(new Dictionary<string, string?> { [user] = "owner", [bootstrap] = "admin" });
        await Assert.That(additions).IsEquivalentTo([(bootstrap, user, email)]);
    }

    [Test]
    public async Task Adding_a_member_needs_an_admin_and_a_valid_email()
    {
        await using var server = await TerraBaseTestServer.StartAsync(settings => { });
        var owner = EntityIds.New().ToString();
        var member = EntityIds.New().ToString();
        var user = EntityIds.New().ToString();
        var email = $"{EntityIds.New()}@example.com";
        using var created = await SendAsync(server.Client, HttpMethod.Post, Organizations, null, owner, new { name = "terrascale" });
        var organization = $"{Organizations}/{(await created.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetString()}";
        using var added = await SendAsync(server.Client, HttpMethod.Put, $"{organization}/members/{member}", null, owner, new { email });

        using var byMember = await SendAsync(server.Client, HttpMethod.Put, $"{organization}/members/{user}", null, member, new { email });
        using var byStranger = await SendAsync(server.Client, HttpMethod.Put, $"{organization}/members/{user}", null, user, new { email });
        using var invalidEmail = await SendAsync(server.Client, HttpMethod.Put, $"{organization}/members/{user}", null, owner, new { email = "not an email" });
        using var invalidUser = await SendAsync(server.Client, HttpMethod.Put, $"{organization}/members/{new string('u', 257)}", null, owner, new { email });
        using var members = await SendAsync(server.Client, HttpMethod.Get, $"{organization}/members", null, owner);
        var users = (await members.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("items").EnumerateArray()
            .Select(item => item.GetProperty("userId").GetString()!).ToList();

        await Assert.That(added.StatusCode).IsEqualTo(HttpStatusCode.OK);
        await Assert.That(byMember.StatusCode).IsEqualTo(HttpStatusCode.Forbidden);
        await Assert.That(await ErrorAsync(byMember)).IsEqualTo(("forbidden_role", "your role in the organization does not allow this"));
        await Assert.That(byStranger.StatusCode).IsEqualTo(HttpStatusCode.NotFound);
        await Assert.That(await ErrorAsync(byStranger)).IsEqualTo(("not_found", null));
        await Assert.That(invalidEmail.StatusCode).IsEqualTo(HttpStatusCode.BadRequest);
        await Assert.That(await ErrorAsync(invalidEmail)).IsEqualTo(("invalid_request", "email must be one address of at most 254 characters"));
        await Assert.That(invalidUser.StatusCode).IsEqualTo(HttpStatusCode.BadRequest);
        await Assert.That(await ErrorAsync(invalidUser)).IsEqualTo(("invalid_request", "user must be a canonical uppercase ULID"));
        await Assert.That(users).IsEquivalentTo([owner, member]);
    }

    [Test]
    public async Task Adding_a_member_is_not_served_on_the_public_listener()
    {
        await using var api = await ManagementApi.StartAsync(settings => { });
        using var anonymous = api.WithToken(null);
        var organization = $"{Organizations}/{await api.CreateOrganizationAsync("Public")}";
        var path = $"{organization}/members/{EntityIds.New()}";
        var body = new { email = $"{EntityIds.New()}@example.com" };

        // The owner's own valid token, and the operator's user header, are both refused there.
        using var withToken = await api.Client.PutAsJsonAsync(ManagementApi.Uri(path), body);
        using var withOperator = await SendAsync(anonymous, HttpMethod.Put, path, null, api.User, body);
        using var members = await api.Client.GetAsync(ManagementApi.Uri($"{organization}/members"));
        var users = (await members.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("items").EnumerateArray()
            .Select(item => item.GetProperty("userId").GetString()!).ToList();

        await Assert.That(withToken.StatusCode).IsEqualTo(HttpStatusCode.NotFound);
        await Assert.That(withOperator.StatusCode).IsEqualTo(HttpStatusCode.NotFound);
        await Assert.That(users).IsEquivalentTo([api.User]);
    }

    [Test]
    public async Task The_operator_names_exactly_one_valid_user()
    {
        await using var server = await TerraBaseTestServer.StartAsync();
        string[][] refused =
        [
            [],
            [new string('u', 257)],
            ["bad\u0001user"],
            ["00112233-4455-6677-8899-aabbccddeeff"],
            ["01arz3ndektsv4rrffq69g5fav"],
            ["01ARZ3NDEKTSV4RRFFQ69G5FAI"],
            ["81ARZ3NDEKTSV4RRFFQ69G5FAV"],
            [EntityIds.New().ToString(), EntityIds.New().ToString()],
        ];

        var statuses = new List<HttpStatusCode>();
        foreach (var users in refused)
        {
            using var response = await SendAsync(server.Client, HttpMethod.Get, Organizations, null, users);
            statuses.Add(response.StatusCode);
        }

        await Assert.That(statuses).All().Satisfy(status => status.IsEqualTo(HttpStatusCode.Unauthorized));
    }

    [Test]
    public async Task Every_use_of_the_operator_is_logged()
    {
        var logs = new CapturedLogs();
        await using var server = await TerraBaseTestServer.StartAsync(
            settings => { },
            services => services.AddSingleton<ILoggerProvider>(logs));
        var user = EntityIds.New().ToString();

        using var accepted = await SendAsync(server.Client, HttpMethod.Post, Organizations, null, user, new { name = "Audited" });
        var audit = logs.Messages.Where(message => message.Contains("platform operator", StringComparison.Ordinal)).ToList();

        await Assert.That(accepted.StatusCode).IsEqualTo(HttpStatusCode.Created);
        await Assert.That(audit).Contains($"The platform operator acted as user {user}: POST {Organizations}");
    }

    // The code and message of an error body; the message is absent when it would say nothing.
    private static async Task<(string? Code, string? Message)> ErrorAsync(HttpResponseMessage response)
    {
        var error = (await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("error");
        return (error.GetProperty("code").GetString(), error.TryGetProperty("message", out var message) ? message.GetString() : null);
    }

    private static Task<HttpResponseMessage> SendAsync(
        HttpClient client, HttpMethod method, string path, string? bearer, string? user, object? body = null) =>
        SendAsync(client, method, path, bearer, user is null ? [] : [user], body);

    private static async Task<HttpResponseMessage> SendAsync(
        HttpClient client, HttpMethod method, string path, string? bearer, string[] users, object? body = null)
    {
        using var request = new HttpRequestMessage(method, new Uri(path, UriKind.Relative));
        if (bearer is not null)
        {
            request.Headers.Authorization = new("Bearer", bearer);
        }

        if (users.Length > 0)
        {
            // Sent verbatim, so an invalid value reaches the server's own check instead of the client's.
            if (!request.Headers.TryAddWithoutValidation(UserHeader, users))
            {
                throw new InvalidOperationException("the user header could not be added");
            }
        }

        if (body is not null)
        {
            request.Content = JsonContent.Create(body);
        }

        return await client.SendAsync(request);
    }

    /// <summary>Every log message of one server, formatted.</summary>
    private sealed class CapturedLogs : ILoggerProvider
    {
        private readonly ConcurrentQueue<string> _messages = new();

        public IEnumerable<string> Messages => _messages;

        public ILogger CreateLogger(string categoryName) => new Logger(_messages);

        public void Dispose()
        {
        }

        private sealed class Logger(ConcurrentQueue<string> messages) : ILogger
        {
            public IDisposable? BeginScope<TState>(TState state)
                where TState : notnull => null;

            public bool IsEnabled(LogLevel logLevel) => true;

            public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter) =>
                messages.Enqueue(formatter(state, exception));
        }
    }
}
