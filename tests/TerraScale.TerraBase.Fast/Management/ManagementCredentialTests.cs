using System.Collections.Concurrent;
using System.Net;
using System.Text.Json;
using Bogus;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using TerraScale.TerraBase.Testing;

namespace TerraScale.TerraBase.Fast.Management;

/// <summary>
/// The management service credential (TS-519, COS decision 2026-10-05): bootstrap access to the Management API before
/// ts-auth exists, on the loopback administrative listener only.
/// </summary>
public sealed class ManagementCredentialTests
{
    private const string Credential = "test-management-credential-0123456789abcdef";
    private const string UserHeader = "X-TerraBase-User-Id";
    private const string Organizations = "/v1/management/organizations";

    private static readonly Faker Faker = new();

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
        var user = Faker.Random.Guid().ToString();

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
        var user = Faker.Random.Guid().ToString();

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
    public async Task The_credential_needs_the_exact_secret_and_one_valid_user()
    {
        using var credential = new CredentialFile(Credential);
        await using var server = await TerraBaseTestServer.StartAsync(settings => settings["management-credential-file"] = credential.Path);
        var user = Faker.Random.Guid().ToString();
        (string? Bearer, string[] Users)[] refused =
        [
            (null, [user]),
            (Credential + "x", [user]),
            (Credential[..^1], [user]),
            (Credential, []),
            (Credential, [new string('u', 257)]),
            (Credential, ["bad\u0001user"]),
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

        using var response = await SendAsync(server.Client, HttpMethod.Get, Organizations, Credential, Faker.Random.Guid().ToString());

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
        var user = Faker.Random.Guid().ToString();

        using var accepted = await SendAsync(server.Client, HttpMethod.Post, Organizations, Credential, user, new { name = "Audited" });
        using var rejected = await SendAsync(server.Client, HttpMethod.Get, Organizations, Credential + "x", user);
        var audit = logs.Messages.Where(message => message.Contains("Management service credential", StringComparison.Ordinal)).ToList();

        await Assert.That(accepted.StatusCode).IsEqualTo(HttpStatusCode.Created);
        await Assert.That(rejected.StatusCode).IsEqualTo(HttpStatusCode.Unauthorized);
        await Assert.That(audit).Contains($"Management service credential acted as user {user}: POST {Organizations}");
        await Assert.That(audit).Contains($"Management service credential rejected: GET {Organizations}");
        await Assert.That(logs.Messages).All().Satisfy(message => message.DoesNotContain(Credential[..20]));
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
