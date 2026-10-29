using System.Net;
using System.Text;
using System.Text.Json;
using TerraBase.Primitives;
using TerraBase.Testing;

namespace TerraBase.Fast.Management;

/// <summary>
/// The cross-project error contract (docs/terrabase/cross-project-errors.md): another project's database answers on every
/// HTTP data protocol exactly like a database that does not exist, and the request changes nothing there.
/// </summary>
public sealed class CrossProjectErrorTests
{
    private const string AllScopes = "databases:read databases:write documents:read documents:write";
    private const string Sql = """{"statements":[{"sql":"DROP TABLE IF EXISTS kept"}]}""";

    [Test]
    [Arguments("GET", "/collections", null)]
    [Arguments("DELETE", "/collections/people", null)]
    [Arguments("GET", "/export", null)]
    [Arguments("POST", "/sql/execute", Sql)]
    public async Task Another_projects_database_answers_like_one_that_does_not_exist(string method, string suffix, string? body)
    {
        await using var api = await ManagementApi.StartAsync();
        var own = await api.CreateManagedDatabaseAsync("own");
        var otherProject = await api.CreateProjectAsync(own.Organization, "Other project");
        var foreign = await api.CreateManagedDatabaseAsync(own.Organization, otherProject, "foreign");
        using var owner = api.WithToken(Token(api, foreign.Project));
        await ManagementApi.CreateCollectionAsync(owner, foreign, "people");
        using var kept = await owner.PostAsync(ManagementApi.Uri(foreign.Data + "/sql/execute"), Json("""{"statements":[{"sql":"CREATE TABLE kept (a TEXT)"}]}"""));
        kept.EnsureSuccessStatusCode();
        using var client = api.WithToken(Token(api, own.Project));

        string[] targets =
        [
            $"/v1/projects/{foreign.Project}/databases/{foreign.Name}",
            $"/v1/projects/{foreign.Project}/databases/{foreign.Id}",
            $"/v1/projects/{own.Project}/databases/{foreign.Id}",
            $"/v1/projects/{own.Project}/databases/{foreign.Name}",
            $"/v1/projects/{own.Project}/databases/{EntityIds.New()}",
            $"/v1/projects/{EntityIds.New()}/databases/{foreign.Name}",
        ];
        var answers = new List<(HttpStatusCode Status, string Body)>();
        foreach (var target in targets)
        {
            using var request = new HttpRequestMessage(new HttpMethod(method), ManagementApi.Uri(target + suffix)) { Content = body is null ? null : Json(body) };
            using var response = await client.SendAsync(request);
            answers.Add((response.StatusCode, await response.Content.ReadAsStringAsync()));
        }

        // Every target answers the same status, code and message. A route naming another project is refused before its
        // whether the database exists never shows.
        foreach (var (status, text) in answers)
        {
            await Assert.That((status, Error(text))).IsEqualTo((HttpStatusCode.NotFound, ("unknown_database", "the database does not exist")));
        }

        await Assert.That(answers[1].Body).IsEqualTo(answers[0].Body);
        await Assert.That(answers[5].Body).IsEqualTo(answers[0].Body);
        foreach (var text in answers[3..5].Select(answer => answer.Body))
        {
            await Assert.That(text).IsEqualTo(answers[2].Body);
        }

        using var collections = await owner.GetAsync(ManagementApi.Uri(foreign.Data + "/collections"));
        await Assert.That(await collections.Content.ReadAsStringAsync()).Contains("\"people\"");
        using var table = await owner.PostAsync(ManagementApi.Uri(foreign.Data + "/sql/execute"), Json("""{"statements":[{"sql":"SELECT count(*) FROM kept"}]}"""));
        await Assert.That(table.StatusCode).IsEqualTo(HttpStatusCode.OK);
    }

    private static string Token(ManagementApi api, string project) =>
        api.Issuer.Mint(EntityIds.New().ToString(), token =>
        {
            token.Claims["project_id"] = project;
            token.Claims["scope"] = AllScopes;
        });

    private static StringContent Json(string body) => new(body, Encoding.UTF8, "application/json");


    private static (string? Code, string? Message) Error(string body)
    {
        var json = JsonElement.Parse(body);
        var error = json.TryGetProperty("error", out var nested) && nested.ValueKind == JsonValueKind.Object ? nested : json;
        return (error.TryGetProperty("code", out var code) ? code.GetString() : null, error.TryGetProperty("message", out var message) ? message.GetString() : null);
    }
}
