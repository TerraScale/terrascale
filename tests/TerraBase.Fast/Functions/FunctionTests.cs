using System.Net;
using System.Text.Json;
using TerraBase.Testing;

namespace TerraBase.Fast.Functions;

/// <summary>Functions deploy, run over HTTPS, write documents and keep logs through the public client listener.</summary>
public sealed class FunctionTests
{
    private const string Hello = """
        function handler(event) {
            console.log("hello", event.body.name, event.query.lang);
            const id = db.insert("audit", { name: event.body.name });
            return { status: 201, body: { greeting: "hello " + event.body.name, id } };
        }
        """;

    [Test]
    public async Task A_deployed_function_answers_over_https_writes_its_documents_and_logs_the_run()
    {
        var token = TUnit.Core.TestContext.Current!.Execution.CancellationToken;
        await using var fixture = await ProjectApi.StartAsync(token);
        await ManagementApi.CreateCollectionAsync(fixture.Client, fixture.Database, "audit", token);
        var path = fixture.Database.Data + "/functions";

        using (var deployed = await fixture.Client.PutAsJsonAsync(ManagementApi.Uri(path + "/hello"), new { source = Hello }, token))
        {
            await Assert.That(deployed.StatusCode).IsEqualTo(HttpStatusCode.OK).Because(await deployed.Content.ReadAsStringAsync(token));
        }

        using var invoked = await fixture.Client.PostAsJsonAsync(ManagementApi.Uri(path + "/hello/invoke?lang=en"), new { name = "Ada" }, token);
        await Assert.That(invoked.StatusCode).IsEqualTo(HttpStatusCode.Created).Because(await invoked.Content.ReadAsStringAsync(token));
        var answer = await invoked.Content.ReadFromJsonAsync<JsonElement>(token);
        await Assert.That(answer.GetProperty("greeting").GetString()).IsEqualTo("hello Ada");
        var document = await fixture.JsonAsync(ManagementApi.DocumentPath(fixture.Database, "audit", answer.GetProperty("id").GetString()!), token);
        await Assert.That(document.GetProperty("document").GetProperty("name").GetString()).IsEqualTo("Ada");

        var logs = await fixture.JsonAsync(path + "/hello/logs", token);
        await Assert.That(logs.GetArrayLength()).IsEqualTo(1);
        await Assert.That(logs[0].GetProperty("trigger").GetString()).IsEqualTo("http");
        await Assert.That(logs[0].GetProperty("status").GetString()).IsEqualTo("ok");
        await Assert.That(logs[0].GetProperty("lines")[0].GetString()).IsEqualTo("hello Ada en");

        var list = await fixture.JsonAsync(path, token);
        await Assert.That(list.GetArrayLength()).IsEqualTo(1);
        await Assert.That(list[0].GetProperty("name").GetString()).IsEqualTo("hello");
        await Assert.That(list[0].GetProperty("source").ValueKind).IsEqualTo(JsonValueKind.Null);
        await Assert.That((await fixture.JsonAsync(path + "/hello", token)).GetProperty("source").GetString()).IsEqualTo(Hello);
    }

}
