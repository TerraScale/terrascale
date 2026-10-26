using System.Net;
using TerraBase.Primitives;
using TerraBase.Testing;

namespace TerraBase.Fast.Management;

public sealed class ToolingAccessTests
{
    [Test]
    public async Task Tooling_routes_answer_404_to_users_outside_the_organization()
    {
        await using var api = await ManagementApi.StartAsync();
        var database = await api.CreateManagedDatabaseAsync();
        using var outsider = api.As(EntityIds.New().ToString());
        var d = database.Path;
        var p = database.ProjectPath;
        (HttpMethod Method, string Path, string? Body)[] routes =
        [
            (HttpMethod.Get, $"{d}/backups", null),
            (HttpMethod.Post, $"{d}/backups", null),
            (HttpMethod.Get, $"{d}/backup-policy", null),
            (HttpMethod.Put, $"{d}/backup-policy", """{"fullBackupCron":null,"retentionDays":7,"disabled":false}"""),
            (HttpMethod.Post, $"{d}/backups/{EntityIds.New()}/restore", "{}"),
            (HttpMethod.Get, $"{d}/collections", null),
            (HttpMethod.Get, $"{d}/collections/people/schema", null),
            (HttpMethod.Get, $"{d}/indexes", null),
            (HttpMethod.Post, $"{d}/indexes", """{"collection":"people","fields":["email"]}"""),
            (HttpMethod.Delete, $"{d}/indexes/people_email", null),
            (HttpMethod.Get, $"{d}/index-recommendations", null),
            (HttpMethod.Post, $"{d}/index-recommendations/people:email/apply", null),
            (HttpMethod.Post, $"{d}/sql", """{"sql":"SELECT * FROM people"}"""),
            (HttpMethod.Post, $"{d}/sql/check", """{"sql":"SELECT * FROM people"}"""),
            (HttpMethod.Get, $"{d}/usage", null),
            (HttpMethod.Get, $"{p}/usage", null),
            (HttpMethod.Get, $"{p}/saved-queries", null),
            (HttpMethod.Post, $"{p}/saved-queries", """{"name":"All","sql":"SELECT * FROM people"}"""),
            (HttpMethod.Get, $"{p}/saved-queries/{EntityIds.New()}", null),
            (HttpMethod.Put, $"{p}/saved-queries/{EntityIds.New()}", """{"name":"All","sql":"SELECT * FROM people"}"""),
            (HttpMethod.Delete, $"{p}/saved-queries/{EntityIds.New()}", null),
        ];

        foreach (var (method, path, body) in routes)
        {
            using var request = new HttpRequestMessage(method, ManagementApi.Uri(path));
            if (body is not null)
            {
                request.Content = new StringContent(body, null, "application/json");
            }

            using var response = await outsider.SendAsync(request);
            await Assert.That(response.StatusCode).IsEqualTo(HttpStatusCode.NotFound).Because($"{method} {path}");
        }

        // The owner still sees an untouched database: nothing the outsider sent was applied.
        using var backups = await api.Client.GetAsync(ManagementApi.Uri($"{d}/backups"));
        await Assert.That(await backups.Content.ReadAsStringAsync()).IsEqualTo("""{"items":[],"next":null}""");
    }

    [Test]
    public async Task Tooling_routes_need_a_ts_auth_user_token()
    {
        await using var api = await ManagementApi.StartAsync();
        var database = await api.CreateManagedDatabaseAsync();
        var usage = ManagementApi.Uri($"{database.Path}/usage");
        using var apiKey = await api.CreateClientAsync(database.Organization, "documents:read", "documents:write");
        using var anonymous = api.WithToken(null);
        using var unsigned = api.WithToken(TestIssuer.MintUnsigned(api.User));
        using var headerOnly = api.WithToken(null);
        headerOnly.DefaultRequestHeaders.Add("X-TerraBase-User-Id", api.User);

        using var withApiKey = await apiKey.GetAsync(usage);
        using var withoutToken = await anonymous.GetAsync(usage);
        using var withUnsignedToken = await unsigned.GetAsync(usage);
        using var withUserHeaderOnly = await headerOnly.GetAsync(usage);
        using var withToken = await api.Client.GetAsync(usage);

        await Assert.That(withApiKey.StatusCode).IsEqualTo(HttpStatusCode.Unauthorized);
        await Assert.That(withoutToken.StatusCode).IsEqualTo(HttpStatusCode.Unauthorized);
        await Assert.That(withUnsignedToken.StatusCode).IsEqualTo(HttpStatusCode.Unauthorized);
        // On the client listener a header never names the acting user.
        await Assert.That(withUserHeaderOnly.StatusCode).IsEqualTo(HttpStatusCode.Unauthorized);
        await Assert.That(withToken.StatusCode).IsEqualTo(HttpStatusCode.OK);
    }
}
