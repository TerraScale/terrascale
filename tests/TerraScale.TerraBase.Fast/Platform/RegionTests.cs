using System.Diagnostics;
using System.Net;
using System.Text.Json;
using TerraBase.Configuration;
using TerraBase.Testing;

namespace TerraBase.Fast.Platform;

public sealed class RegionTests
{
    private const string RegionHeader = "X-TerraBase-Region";
    private const string PinHeader = "X-TerraScale-Region-Pin";
    private const string Organizations = "/v1/management/organizations";
    private const string Database = "/v1/projects/{project}/databases/{database}";

    [Test]
    [Arguments("", "development", "Development")]
    [Arguments("a", "a", "Development")]
    [Arguments("gru-1", "gru-1", "Production")]
    [Arguments("a--1", "a--1", "Development")]
    [Arguments("abcdefghijklmnopqrstuvwxyzabcdefghijklmnopqrstuvwxyzabcdefghijk", "abcdefghijklmnopqrstuvwxyzabcdefghijklmnopqrstuvwxyzabcdefghijk", "Production")]
    public async Task Health_reports_the_configured_region_or_the_development_default(string configured, string expected, string environment)
    {
        await using var server = await TerraBaseTestServer.StartAsync(settings =>
        {
            if (configured.Length == 0)
            {
                settings.Remove("region");
            }
            else
            {
                settings["region"] = configured;
            }
        }, environmentName: environment);
        using var response = await server.Client.GetAsync(new Uri("/healthz", UriKind.Relative));
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();

        await Assert.That(response.StatusCode).IsEqualTo(HttpStatusCode.OK);
        await Assert.That(body.GetProperty("region").GetString()).IsEqualTo(expected);
        await Assert.That(response.Headers.Contains(RegionHeader)).IsFalse();
    }

    [Test]
    [Arguments("Production")]
    [Arguments("Staging")]
    public async Task A_region_is_required_outside_development(string environment) =>
        await AssertStartupRefusedAsync(environment, "", fromEnvironment: false);

    [Test]
    [Arguments("Gru-1", false)]
    [Arguments("1gru", false)]
    [Arguments("-gru", false)]
    [Arguments("gru-", false)]
    [Arguments("gru_1", false)]
    [Arguments("grú", false)]
    [Arguments("gru 1", false)]
    [Arguments("abcdefghijklmnopqrstuvwxyzabcdefghijklmnopqrstuvwxyzabcdefghijkl", false)]
    [Arguments("Gru-1", true)]
    public async Task Invalid_regions_stop_the_real_process_without_echoing_values(string region, bool fromEnvironment) =>
        await AssertStartupRefusedAsync("Development", region, fromEnvironment);

    [Test]
    public async Task Every_client_response_keeps_its_region_including_cleared_errors()
    {
        await using var api = await ManagementApi.StartAsync(settings => settings["region"] = "gru-1");
        var organization = await api.CreateOrganizationAsync("Region tests");
        var project = await api.CreateProjectAsync(organization, "Main");
        using var anonymous = api.DataClient();
        using var reader = api.DataClient(await api.Server.IssueAsync(organization, "documents:read"));

        using var success = await api.Client.GetAsync(new Uri(Organizations, UriKind.Relative));
        using var unauthorized = await anonymous.GetAsync(new Uri(Organizations, UriKind.Relative));
        using var forbidden = await reader.PutAsync(new Uri($"/v1/projects/{project}/databases/main", UriKind.Relative), null);
        using var unknown = await anonymous.GetAsync(new Uri("/no-such-route", UriKind.Relative));
        using var wrongListener = await anonymous.GetAsync(new Uri("/metrics", UriKind.Relative));
        using var wrongMethod = await api.Client.PutAsync(new Uri(Organizations, UriKind.Relative), null);
        using var invalid = await api.Client.PostAsJsonAsync(new Uri(Organizations, UriKind.Relative), new { name = "" });
        using var malformed = await api.Client.PostAsync(new Uri(Organizations, UriKind.Relative), new StringContent("{", null, "application/json"));

        foreach (var (response, status) in new[]
        {
            (success, HttpStatusCode.OK),
            (unauthorized, HttpStatusCode.Unauthorized),
            (forbidden, HttpStatusCode.Forbidden),
            (unknown, HttpStatusCode.NotFound),
            (wrongListener, HttpStatusCode.NotFound),
            (invalid, HttpStatusCode.BadRequest),
            (malformed, HttpStatusCode.BadRequest),
            // The framework's method-rejection endpoint has no client metadata, so the listener guard returns 404.
            (wrongMethod, HttpStatusCode.NotFound),
        })
        {
            await Assert.That(response.StatusCode).IsEqualTo(status);
            await Assert.That(response.Headers.GetValues(RegionHeader)).IsEquivalentTo(["gru-1"]);
        }
    }

    [Test]
    [Arguments(null)]
    [Arguments("gru-1")]
    public async Task An_absent_or_exact_region_pin_is_accepted(string? pin)
    {
        await using var api = await ManagementApi.StartAsync(settings => settings["region"] = "gru-1");
        using var request = new HttpRequestMessage(HttpMethod.Get, new Uri(Organizations, UriKind.Relative));
        if (pin is not null)
        {
            request.Headers.TryAddWithoutValidation(PinHeader, pin);
        }

        using var response = await api.Client.SendAsync(request);
        await Assert.That(response.StatusCode).IsEqualTo(HttpStatusCode.OK);
        await Assert.That(response.Headers.GetValues(RegionHeader)).IsEquivalentTo(["gru-1"]);
    }

    [Test]
    [Arguments("iad-1", null)]
    [Arguments("GRU-1", null)]
    [Arguments("", null)]
    [Arguments("gru-1,gru-1", null)]
    [Arguments("gru-1", "gru-1")]
    public async Task Rejected_pins_precede_authentication_and_leave_no_database(string first, string? second)
    {
        await using var api = await ManagementApi.StartAsync(settings => settings["region"] = "gru-1");
        var organization = await api.CreateOrganizationAsync("Region tests");
        var project = await api.CreateProjectAsync(organization, "Main");
        var databases = $"{Organizations}/{organization}/projects/{project}/databases";
        var before = Directory.EnumerateFiles(api.Server.DataDirectory, "*.db", SearchOption.AllDirectories).ToList();
        string[] pins = second is null ? [first] : [first, second];

        using var mutation = new HttpRequestMessage(HttpMethod.Post, new Uri(databases, UriKind.Relative))
        {
            Content = JsonContent.Create(new { name = "rejected" }),
        };
        mutation.Headers.TryAddWithoutValidation(PinHeader, pins);
        using var deniedMutation = await api.Client.SendAsync(mutation);
        await AssertMisdirectedAsync(deniedMutation, first);

        using var anonymous = api.DataClient();
        using var unauthenticated = new HttpRequestMessage(HttpMethod.Put, new Uri($"/v1/projects/{project}/databases/rejected", UriKind.Relative));
        unauthenticated.Headers.TryAddWithoutValidation(PinHeader, pins);
        using var deniedAnonymous = await anonymous.SendAsync(unauthenticated);
        await AssertMisdirectedAsync(deniedAnonymous, first);

        var list = await api.Client.GetFromJsonAsync<JsonElement>(new Uri(databases, UriKind.Relative));
        await Assert.That(list.GetProperty("items").GetArrayLength()).IsEqualTo(0);
        await Assert.That(Directory.EnumerateFiles(api.Server.DataDirectory, "*.db", SearchOption.AllDirectories)).IsEquivalentTo(before);
    }

    [Test]
    public async Task Admin_and_peer_listeners_ignore_client_region_pins_and_never_send_region_headers()
    {
        using var peerTls = new PeerTls();
        await using var server = await TerraBaseTestServer.StartAsync(settings =>
        {
            settings["region"] = "gru-1";
            peerTls.Apply(settings);
        });
        server.Client.DefaultRequestHeaders.Add(PinHeader, "iad-1");
        using var health = await server.Client.GetAsync(new Uri("/healthz", UriKind.Relative));
        using var metrics = await server.Client.GetAsync(new Uri("/metrics", UriKind.Relative));
        using var peer = new HttpClient(peerTls.CreateHandler())
        {
            BaseAddress = server.PeerAddress,
            DefaultRequestVersion = HttpVersion.Version20,
            DefaultVersionPolicy = HttpVersionPolicy.RequestVersionExact,
            Timeout = TimeSpan.FromSeconds(30),
        };
        peer.DefaultRequestHeaders.Add(PinHeader, "iad-1");
        using var wrongListener = await peer.GetAsync(new Uri("/healthz", UriKind.Relative));

        await Assert.That(health.StatusCode).IsEqualTo(HttpStatusCode.OK);
        await Assert.That(metrics.StatusCode).IsEqualTo(HttpStatusCode.OK);
        await Assert.That(wrongListener.StatusCode).IsEqualTo(HttpStatusCode.NotFound);
        foreach (var response in new[] { health, metrics, wrongListener })
        {
            await Assert.That(response.Headers.Contains(RegionHeader)).IsFalse();
        }
    }

    [Test]
    public async Task Openapi_documents_region_pins_headers_and_database_bodies_on_client_operations()
    {
        using var tls = new ClientTls();
        await using var server = await TerraBaseTestServer.StartAsync(tls.Apply);
        using var client = tls.CreateClient(server.ClientAddress!);
        var document = await client.GetFromJsonAsync<JsonElement>(new Uri("/openapi/v1.json", UriKind.Relative));
        var operation = document.GetProperty("paths").GetProperty(Database).GetProperty("get");
        var parameter = operation.GetProperty("parameters").EnumerateArray()
            .Single(item => item.GetProperty("name").GetString() == PinHeader);
        await Assert.That(parameter.GetProperty("in").GetString()).IsEqualTo("header");
        var responses = operation.GetProperty("responses");
        await Assert.That(responses.GetProperty("421").GetProperty("content").TryGetProperty("application/json", out _)).IsTrue();
        foreach (var response in responses.EnumerateObject())
        {
            await Assert.That(response.Value.GetProperty("headers").TryGetProperty(RegionHeader, out _)).IsTrue();
        }

        var schema = document.GetProperty("components").GetProperty("schemas").GetProperty("ManagedDatabaseBody");
        await Assert.That(schema.GetProperty("properties").TryGetProperty("region", out _)).IsTrue();
        foreach (var path in new[] { "/healthz", "/readyz" })
        {
            var probe = document.GetProperty("paths").GetProperty(path).GetProperty("get");
            await Assert.That(probe.GetProperty("parameters").EnumerateArray().Any(item => item.GetProperty("name").GetString() == PinHeader)).IsTrue();
            await Assert.That(probe.GetProperty("responses").TryGetProperty("421", out _)).IsTrue();
            await Assert.That(probe.GetProperty("responses").GetProperty("200").GetProperty("headers").TryGetProperty(RegionHeader, out _)).IsTrue();
        }
    }

    private static async Task AssertMisdirectedAsync(HttpResponseMessage response, string pin)
    {
        await Assert.That(response.StatusCode).IsEqualTo(HttpStatusCode.MisdirectedRequest);
        await Assert.That(response.Headers.GetValues(RegionHeader)).IsEquivalentTo(["gru-1"]);
        await Assert.That(response.Headers.CacheControl!.ToString()).IsEqualTo("no-store");
        await Assert.That(response.Headers.Contains(PinHeader)).IsFalse();
        var text = await response.Content.ReadAsStringAsync();
        using var body = JsonDocument.Parse(text);
        await Assert.That(body.RootElement.GetProperty("error").GetProperty("code").GetString()).IsEqualTo("misdirected_request");
        if (pin.Length > 0)
        {
            await Assert.That(text).DoesNotContain(pin);
        }
    }

    private static async Task AssertStartupRefusedAsync(string environment, string region, bool fromEnvironment)
    {
        var directory = Directory.CreateTempSubdirectory("terrabase-region-").FullName;
        try
        {
            // Like DataDirectoryTests, run the real binary; the child inherits the suite's low CPU priority.
            var start = new ProcessStartInfo("dotnet")
            {
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
            };
            start.Environment["DOTNET_ENVIRONMENT"] = environment;
            start.Environment["ASPNETCORE_ENVIRONMENT"] = environment;
            // Region validation must precede host construction, even with a malformed framework setting.
            start.Environment["DOTNET_HOSTBUILDER__RELOADCONFIGONCHANGE"] = "invalid-test-value";
            start.Environment["TERRABASE_REGION"] = fromEnvironment ? region : "gru-1";
            start.ArgumentList.Add(Path.Combine(AppContext.BaseDirectory, typeof(Program).Assembly.GetName().Name + ".dll"));
            foreach (var name in TerraBaseOptions.SettingNames)
            {
                if (name == "region" && fromEnvironment)
                {
                    continue;
                }

                var value = name switch
                {
                    "admin-port" => "0",
                    "data-directory" => directory,
                    "region" => region,
                    _ => string.Empty,
                };
                start.ArgumentList.Add($"--{name}={value}");
            }

            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(TUnit.Core.TestContext.Current!.Execution.CancellationToken);
            using var process = Process.Start(start) ?? throw new InvalidOperationException("the TerraBase process did not start");
            try
            {
                var output = process.StandardOutput.ReadToEndAsync(timeout.Token);
                var error = process.StandardError.ReadToEndAsync(timeout.Token);
                await Task.WhenAll(output, error, process.WaitForExitAsync(timeout.Token));

                await Assert.That(process.ExitCode).IsEqualTo(2);
                await Assert.That(await output).IsEmpty();
                await Assert.That(await error).Contains("region");
                if (region.Length > 0)
                {
                    await Assert.That(await error).DoesNotContain(region);
                }

                await Assert.That(Directory.EnumerateFileSystemEntries(directory)).IsEmpty();
            }
            finally
            {
                if (!process.HasExited)
                {
                    process.Kill(entireProcessTree: true);
                    using var cleanup = new CancellationTokenSource(TimeSpan.FromSeconds(5));
                    await process.WaitForExitAsync(cleanup.Token);
                }
            }
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }
}
