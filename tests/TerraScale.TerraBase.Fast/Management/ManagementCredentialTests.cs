using System.Collections.Concurrent;
using System.Net;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using TerraBase.Primitives;
using TerraBase.Testing;

namespace TerraBase.Fast.Management;

/// <summary>
/// The management service credential (TS-519, COS decision 2026-10-05): bootstrap access to the Management API before
/// ts-auth exists, on the loopback administrative listener only.
/// </summary>
public sealed class ManagementCredentialTests
{
    private const string Credential = "test-management-credential-0123456789abcdef";
    private const string UserHeader = "X-TerraBase-User-Id";
    private const string Organizations = "/v1/management/organizations";


    [Test]
    public async Task The_credential_bootstraps_organizations_on_the_admin_listener_before_ts_auth_answers()
    {
        // TS-529: the node is configured for ts-auth, which does not run yet, so no user token can work.
        using var credential = new CredentialFile(Credential);
        using var tls = new ClientTls();
        using var issuer = new TestIssuer { Available = false };
        await using var server = await TerraBaseTestServer.StartAsync(
            settings =>
            {
                tls.Apply(settings);
                TestIssuer.Configure(settings);
                settings["management-credential-file"] = credential.Path;
            },
            issuer.Route);
        var user = EntityIds.New().ToString();

        using var created = await SendAsync(server.Client, HttpMethod.Post, Organizations, Credential, user, new { name = "terrascale" });
        var organization = await created.Content.ReadFromJsonAsync<JsonElement>();
        var id = organization.GetProperty("id").GetString();
        using var project = await SendAsync(server.Client, HttpMethod.Post, $"{Organizations}/{id}/projects", Credential, user, new { name = "ts-auth" });
        using var ready = await server.Client.GetAsync(new Uri("/readyz", UriKind.Relative));

        await Assert.That(created.StatusCode).IsEqualTo(HttpStatusCode.Created);
        await Assert.That(organization.GetProperty("createdBy").GetString()).IsEqualTo(user);
        await Assert.That(project.StatusCode).IsEqualTo(HttpStatusCode.Created);
        await Assert.That(ready.StatusCode).IsEqualTo(HttpStatusCode.ServiceUnavailable);
    }

    [Test]
    public async Task The_credential_is_refused_on_the_public_listener_and_tokens_on_the_admin_listener()
    {
        using var credential = new CredentialFile(Credential);
        await using var api = await ManagementApi.StartAsync(settings => settings["management-credential-file"] = credential.Path);
        using var client = api.WithToken(null);
        var user = EntityIds.New().ToString();

        using var onAdmin = await SendAsync(api.Server.Client, HttpMethod.Get, Organizations, Credential, user);
        using var onClient = await SendAsync(client, HttpMethod.Get, Organizations, Credential, user);
        using var tokenOnAdmin = await SendAsync(api.Server.Client, HttpMethod.Get, Organizations, api.Issuer.Mint(user), user: null);
        using var tokenOnClient = await SendAsync(client, HttpMethod.Get, Organizations, api.Issuer.Mint(user), user: null);

        await Assert.That(onAdmin.StatusCode).IsEqualTo(HttpStatusCode.OK);
        await Assert.That(onClient.StatusCode).IsEqualTo(HttpStatusCode.Unauthorized);
        await Assert.That(tokenOnAdmin.StatusCode).IsEqualTo(HttpStatusCode.Unauthorized);
        await Assert.That(tokenOnClient.StatusCode).IsEqualTo(HttpStatusCode.OK);
    }

    [Test]
    public async Task The_credential_adds_a_member_who_then_takes_ownership()
    {
        // TS-535: the TS-529 bootstrap hands the organization it created to a person, before ts-auth exists.
        using var credential = new CredentialFile(Credential);
        await using var server = await TerraBaseTestServer.StartAsync(settings => settings["management-credential-file"] = credential.Path);
        var bootstrap = EntityIds.New().ToString();
        var user = EntityIds.New().ToString();
        var email = $"{EntityIds.New()}@example.com";
        using var created = await SendAsync(server.Client, HttpMethod.Post, Organizations, Credential, bootstrap, new { name = "terrascale" });
        var organization = $"{Organizations}/{(await created.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetString()}";

        // Re-adding the sole owner must not demote it.
        using var owner = await SendAsync(server.Client, HttpMethod.Put, $"{organization}/members/{bootstrap}", Credential, bootstrap, new { email });
        using var added = await SendAsync(server.Client, HttpMethod.Put, $"{organization}/members/{user}", Credential, bootstrap, new { email });
        using var again = await SendAsync(server.Client, HttpMethod.Put, $"{organization}/members/{user}", Credential, bootstrap, new { email });
        using var transfer = await SendAsync(server.Client, HttpMethod.Post, $"{organization}/ownership-transfer", Credential, bootstrap, new { userId = user });
        using var confirmed = await SendAsync(server.Client, HttpMethod.Post, $"{organization}/ownership-transfer/confirm", Credential, user);
        // Confirming makes the previous owner a member; the new owner makes the bootstrap identity an admin.
        using var promoted = await SendAsync(server.Client, HttpMethod.Put, $"{organization}/members/{bootstrap}/role", Credential, user, new { role = "admin" });
        using var members = await SendAsync(server.Client, HttpMethod.Get, $"{organization}/members", Credential, user);
        using var audit = await SendAsync(server.Client, HttpMethod.Get, $"{organization}/audit-events", Credential, user);
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
        using var credential = new CredentialFile(Credential);
        await using var server = await TerraBaseTestServer.StartAsync(settings => settings["management-credential-file"] = credential.Path);
        var owner = EntityIds.New().ToString();
        var member = EntityIds.New().ToString();
        var user = EntityIds.New().ToString();
        var email = $"{EntityIds.New()}@example.com";
        using var created = await SendAsync(server.Client, HttpMethod.Post, Organizations, Credential, owner, new { name = "terrascale" });
        var organization = $"{Organizations}/{(await created.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetString()}";
        using var added = await SendAsync(server.Client, HttpMethod.Put, $"{organization}/members/{member}", Credential, owner, new { email });

        using var byMember = await SendAsync(server.Client, HttpMethod.Put, $"{organization}/members/{user}", Credential, member, new { email });
        using var byStranger = await SendAsync(server.Client, HttpMethod.Put, $"{organization}/members/{user}", Credential, user, new { email });
        using var invalidEmail = await SendAsync(server.Client, HttpMethod.Put, $"{organization}/members/{user}", Credential, owner, new { email = "not an email" });
        using var invalidUser = await SendAsync(server.Client, HttpMethod.Put, $"{organization}/members/{new string('u', 257)}", Credential, owner, new { email });
        using var members = await SendAsync(server.Client, HttpMethod.Get, $"{organization}/members", Credential, owner);
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
        using var credential = new CredentialFile(Credential);
        await using var api = await ManagementApi.StartAsync(settings => settings["management-credential-file"] = credential.Path);
        using var anonymous = api.WithToken(null);
        var organization = $"{Organizations}/{await api.CreateOrganizationAsync("Public")}";
        var path = $"{organization}/members/{EntityIds.New()}";
        var body = new { email = $"{EntityIds.New()}@example.com" };

        // The owner's own valid token, and the credential, are both refused there.
        using var withToken = await api.Client.PutAsJsonAsync(ManagementApi.Uri(path), body);
        using var withCredential = await SendAsync(anonymous, HttpMethod.Put, path, Credential, api.User, body);
        using var members = await api.Client.GetAsync(ManagementApi.Uri($"{organization}/members"));
        var users = (await members.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("items").EnumerateArray()
            .Select(item => item.GetProperty("userId").GetString()!).ToList();

        await Assert.That(withToken.StatusCode).IsEqualTo(HttpStatusCode.NotFound);
        await Assert.That(withCredential.StatusCode).IsEqualTo(HttpStatusCode.NotFound);
        await Assert.That(users).IsEquivalentTo([api.User]);
    }

    [Test]
    public async Task The_credential_needs_the_exact_secret_and_one_valid_user()
    {
        using var credential = new CredentialFile(Credential);
        await using var server = await TerraBaseTestServer.StartAsync(settings => settings["management-credential-file"] = credential.Path);
        var user = EntityIds.New().ToString();
        (string? Bearer, string[] Users)[] refused =
        [
            (null, [user]),
            (Credential + "x", [user]),
            (Credential[..^1], [user]),
            (Credential, []),
            (Credential, [new string('u', 257)]),
            (Credential, ["bad\u0001user"]),
            (Credential, ["00112233-4455-6677-8899-aabbccddeeff"]),
            (Credential, ["01arz3ndektsv4rrffq69g5fav"]),
            (Credential, ["01ARZ3NDEKTSV4RRFFQ69G5FAI"]),
            (Credential, ["81ARZ3NDEKTSV4RRFFQ69G5FAV"]),
        ];

        var statuses = new List<HttpStatusCode>();
        foreach (var (bearer, users) in refused)
        {
            using var response = await SendAsync(server.Client, HttpMethod.Get, Organizations, bearer, users);
            statuses.Add(response.StatusCode);
        }

        await Assert.That(statuses).All().Satisfy(status => status.IsEqualTo(HttpStatusCode.Unauthorized));
    }

    [Test]
    public async Task The_credential_is_off_unless_configured()
    {
        await using var server = await TerraBaseTestServer.StartAsync();

        using var response = await SendAsync(server.Client, HttpMethod.Get, Organizations, Credential, EntityIds.New().ToString());

        await Assert.That(response.StatusCode).IsEqualTo(HttpStatusCode.Unauthorized);
    }

    [Test]
    public async Task Every_use_of_the_credential_is_logged_without_the_secret()
    {
        using var credential = new CredentialFile(Credential);
        var logs = new CapturedLogs();
        await using var server = await TerraBaseTestServer.StartAsync(
            settings => settings["management-credential-file"] = credential.Path,
            services => services.AddSingleton<ILoggerProvider>(logs));
        var user = EntityIds.New().ToString();

        using var accepted = await SendAsync(server.Client, HttpMethod.Post, Organizations, Credential, user, new { name = "Audited" });
        using var rejected = await SendAsync(server.Client, HttpMethod.Get, Organizations, Credential + "x", user);
        var audit = logs.Messages.Where(message => message.Contains("Management service credential", StringComparison.Ordinal)).ToList();

        await Assert.That(accepted.StatusCode).IsEqualTo(HttpStatusCode.Created);
        await Assert.That(rejected.StatusCode).IsEqualTo(HttpStatusCode.Unauthorized);
        await Assert.That(audit).Contains($"Management service credential acted as user {user}: POST {Organizations}");
        await Assert.That(audit).Contains($"Management service credential rejected: GET {Organizations}");
        await Assert.That(logs.Messages).All().Satisfy(message => message.DoesNotContain(Credential[..20]));
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

    /// <summary>A secret written to a file in its own temporary directory, deleted on dispose.</summary>
    private sealed class CredentialFile : IDisposable
    {
        private readonly string _directory = Directory.CreateTempSubdirectory("terrabase-credential-").FullName;

        public CredentialFile(string secret)
        {
            Path = System.IO.Path.Combine(_directory, "credential");
            File.WriteAllText(Path, secret + "\n");
        }

        public string Path { get; }

        public void Dispose() => Directory.Delete(_directory, recursive: true);
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
