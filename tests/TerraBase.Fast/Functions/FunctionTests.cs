using System.Net;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using TerraBase.Features.Functions;
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

    [Test]
    public async Task Failing_or_invalid_functions_are_refused_and_reported_and_deletion_removes_them()
    {
        var token = TUnit.Core.TestContext.Current!.Execution.CancellationToken;
        await using var fixture = await ProjectApi.StartAsync(token);
        var path = fixture.Database.Data + "/functions";
        using (var invalid = await fixture.Client.PutAsJsonAsync(ManagementApi.Uri(path + "/broken"), new { source = "function handler( {" }, token))
        {
            await Assert.That(invalid.StatusCode).IsEqualTo(HttpStatusCode.BadRequest);
        }

        using (var deployed = await fixture.Client.PutAsJsonAsync(ManagementApi.Uri(path + "/spin"),
            new { source = "function handler(e) { if (e.body === 'throw') throw new Error('boom'); while (true) {} }" }, token))
        {
            await Assert.That(deployed.StatusCode).IsEqualTo(HttpStatusCode.OK);
        }

        using (var thrown = await fixture.Client.PostAsync(ManagementApi.Uri(path + "/spin/invoke"), new StringContent("throw"), token))
        {
            await Assert.That(thrown.StatusCode).IsEqualTo(HttpStatusCode.InternalServerError);
            var error = await thrown.Content.ReadFromJsonAsync<JsonElement>(token);
            await Assert.That(error.GetProperty("error").GetProperty("code").GetString()).IsEqualTo("function_error");
            await Assert.That(error.GetProperty("error").GetProperty("message").GetString()).Contains("boom");
        }

        using (var spun = await fixture.Client.PostAsync(ManagementApi.Uri(path + "/spin/invoke"), new StringContent("loop"), token))
        {
            await Assert.That(spun.StatusCode).IsEqualTo(HttpStatusCode.InternalServerError);
        }

        var logs = await fixture.JsonAsync(path + "/spin/logs", token);
        await Assert.That(logs.GetArrayLength()).IsEqualTo(2);
        await Assert.That(logs[0].GetProperty("status").GetString()).IsEqualTo("error");
        await Assert.That(logs[0].GetProperty("error").GetString()).IsEqualTo("the function exceeded 10,000,000 statements")
            .Or.IsEqualTo("the function exceeded 2 s");

        using (var reader = fixture.ProjectClient("documents:read"))
        using (var refused = await reader.DeleteAsync(ManagementApi.Uri(path + "/spin"), token))
        {
            await Assert.That(refused.StatusCode).IsEqualTo(HttpStatusCode.Forbidden);
        }

        using (var deleted = await fixture.Client.DeleteAsync(ManagementApi.Uri(path + "/spin"), token))
        {
            await Assert.That(deleted.StatusCode).IsEqualTo(HttpStatusCode.NoContent);
        }

        using var gone = await fixture.Client.PostAsync(ManagementApi.Uri(path + "/spin/invoke"), new StringContent("throw"), token);
        await Assert.That(gone.StatusCode).IsEqualTo(HttpStatusCode.NotFound);
    }

    [Test]
    public async Task A_cron_function_runs_on_its_schedule()
    {
        var token = TUnit.Core.TestContext.Current!.Execution.CancellationToken;
        await using var fixture = await ProjectApi.StartAsync(token);
        var path = fixture.Database.Data + "/functions";
        using (var invalid = await fixture.Client.PutAsJsonAsync(ManagementApi.Uri(path + "/tick"), new { source = "function handler(e) {}", cron = "every minute" }, token))
        {
            await Assert.That(invalid.StatusCode).IsEqualTo(HttpStatusCode.BadRequest);
        }

        using (var deployed = await fixture.Client.PutAsJsonAsync(ManagementApi.Uri(path + "/tick"),
            new { source = "function handler(e) { console.log(e.trigger, e.scheduledAt); }", cron = "* * * * *" }, token))
        {
            await Assert.That(deployed.StatusCode).IsEqualTo(HttpStatusCode.OK);
        }

        var scheduler = fixture.Api.Server.Services.GetRequiredService<FunctionScheduler>();
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(token);
        deadline.CancelAfter(TimeSpan.FromSeconds(60));
        var runs = 0;
        // 15 s steps, each seen by a scheduler tick before the next one: three minutes give at least two due runs.
        for (var step = 0; step < 16 && runs < 3; step++)
        {
            var tick = scheduler.NextTickAsync();
            fixture.Clock.Advance(TimeSpan.FromSeconds(15));
            await tick.WaitAsync(deadline.Token);
            runs = (await fixture.JsonAsync(path + "/tick/logs", deadline.Token)).GetArrayLength();
        }

        var logs = await fixture.JsonAsync(path + "/tick/logs", token);
        await Assert.That(logs.GetArrayLength()).IsGreaterThanOrEqualTo(2);
        await Assert.That(logs[0].GetProperty("trigger").GetString()).IsEqualTo("cron");
        await Assert.That(logs[0].GetProperty("lines")[0].GetString()).StartsWith("cron ");
    }
}
