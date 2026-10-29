using System.Net;
using System.Text;
using System.Text.Json;

namespace TerraScale.Endpoints.Tests;

public sealed class PipelineTests
{
    [Test]
    public async Task GeneratorRegistersEveryConcreteEndpointInOrder()
    {
        var types = GeneratedEndpoints.All.Select(registration => registration.EndpointType).ToArray();
        await Assert.That(types).Contains(typeof(ProbeEndpoint));
        await Assert.That(types).Contains(typeof(BoundProbeEndpoint));
        await Assert.That(types).DoesNotContain(typeof(AbstractProbeEndpoint));
        await Assert.That(types.Select(type => type.FullName).ToArray())
            .IsEquivalentTo(types.Select(type => type.FullName).OrderBy(name => name, StringComparer.Ordinal).ToArray());
    }

    [Test]
    public async Task JsonBodyIsBoundAndResponseIsSerialized()
    {
        await using var app = await StartAsync(EndpointRegistration.Create<JsonProbeEndpoint>());
        using var client = Client(app);
        using var response = await client.PostAsync("/json-probe", Json("""{"value":"hi"}"""));
        await Assert.That(response.StatusCode).IsEqualTo(HttpStatusCode.OK);
        await Assert.That(response.Content.Headers.ContentType?.MediaType).IsEqualTo("application/json");
        await Assert.That(await response.Content.ReadAsStringAsync()).IsEqualTo("""{"value":"hi!"}""");
    }

    [Test]
    public async Task RejectsNonJsonInvalidJsonAndMissingBodies()
    {
        await using var app = await StartAsync(EndpointRegistration.Create<JsonProbeEndpoint>());
        using var client = Client(app);
        using var text = await client.PostAsync("/json-probe", new StringContent("hi", Encoding.UTF8, "text/plain"));
        using var invalid = await client.PostAsync("/json-probe", Json("{"));
        using var empty = await client.PostAsync("/json-probe", Json("null"));
        await Assert.That(text.StatusCode).IsEqualTo(HttpStatusCode.UnsupportedMediaType);
        await Assert.That(invalid.StatusCode).IsEqualTo(HttpStatusCode.BadRequest);
        await Assert.That(empty.StatusCode).IsEqualTo(HttpStatusCode.BadRequest);
        await Assert.That(invalid.Content.Headers.ContentType?.MediaType).IsEqualTo("application/problem+json");
        using var problem = JsonDocument.Parse(await invalid.Content.ReadAsStringAsync());
        await Assert.That(problem.RootElement.GetProperty("status").GetInt32()).IsEqualTo(400);
        await Assert.That(problem.RootElement.GetProperty("detail").GetString()).IsEqualTo("The JSON request body is invalid.");
    }

    [Test]
    public async Task SenderErrorHelpersSetStatusAndBody()
    {
        await using var app = await StartAsync(EndpointRegistration.Create<ErrorSenderProbeEndpoint>());
        using var client = Client(app);
        using var conflict = await client.GetAsync("/error-sender/conflict");
        using var forbidden = await client.GetAsync("/error-sender/forbidden");
        using var validation = await client.GetAsync("/error-sender/validation");
        await Assert.That(conflict.StatusCode).IsEqualTo(HttpStatusCode.Conflict);
        await Assert.That(await conflict.Content.ReadAsStringAsync()).IsEqualTo("""{"value":"taken"}""");
        await Assert.That(forbidden.StatusCode).IsEqualTo(HttpStatusCode.Forbidden);
        await Assert.That(validation.StatusCode).IsEqualTo(HttpStatusCode.BadRequest);
        using var problem = JsonDocument.Parse(await validation.Content.ReadAsStringAsync());
        await Assert.That(problem.RootElement.GetProperty("errors").GetProperty("name")[0].GetString()).IsEqualTo("required");
    }

    [Test]
    public async Task BindAsyncOverrideReplacesBodyBinding()
    {
        await using var app = await StartAsync(EndpointRegistration.Create<BoundProbeEndpoint>());
        using var client = Client(app);
        await Assert.That(await client.GetStringAsync("/bound/42")).IsEqualTo("42");
        using var rejected = await client.GetAsync("/bound/nope");
        await Assert.That(rejected.StatusCode).IsEqualTo(HttpStatusCode.BadRequest);
    }

    [Test]
    public async Task SenderEndpointsSendTheirResultOrNoContent()
    {
        await using var app = await StartAsync(
            EndpointRegistration.Create<SenderProbeEndpoint>(),
            EndpointRegistration.Create<SilentProbeEndpoint>());
        using var client = Client(app);
        using var sent = await client.PostAsync("/sender-probe", Json("""{"value":"x"}"""));
        using var silent = await client.DeleteAsync("/silent");
        await Assert.That(await sent.Content.ReadAsStringAsync()).IsEqualTo("""{"value":"x"}""");
        await Assert.That(silent.StatusCode).IsEqualTo(HttpStatusCode.NoContent);
    }

    [Test]
    public async Task OpenApiDescribesBaseClassEndpoints()
    {
        await using var app = await StartAsync(
            EndpointRegistration.Create<JsonProbeEndpoint>(),
            EndpointRegistration.Create<ProbeEndpoint>());
        using var client = Client(app);
        using var document = JsonDocument.Parse(await client.GetStringAsync("/openapi/v1.json"));
        var paths = document.RootElement.GetProperty("paths");
        var post = paths.GetProperty("/json-probe").GetProperty("post");
        await Assert.That(post.GetProperty("requestBody").GetProperty("content").TryGetProperty("application/json", out _)).IsTrue();
        var okContent = post.GetProperty("responses").GetProperty("200").GetProperty("content");
        await Assert.That(okContent.TryGetProperty("application/json", out _)).IsTrue();
        await Assert.That(paths.GetProperty("/probe").GetProperty("get").GetProperty("operationId").GetString()).IsEqualTo("probe");
    }

    private static async Task<WebApplication> StartAsync(params EndpointRegistration[] endpoints)
    {
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        builder.Services.AddTerraScaleEndpoints(endpoints);
        var app = builder.Build();
        app.MapTerraScaleEndpoints();
        app.MapTerraScaleOpenApi();
        await app.StartAsync();
        return app;
    }

    private static HttpClient Client(WebApplication app) => new() { BaseAddress = new Uri(app.Urls.Single()) };

    private static StringContent Json(string body) => new(body, Encoding.UTF8, "application/json");
}

public sealed record JsonProbe(string Value);

public sealed class JsonProbeEndpoint : Endpoint<JsonProbe, JsonProbe>
{
    public override void Configure() => Post("/json-probe");

    public override ValueTask<JsonProbe> HandleAsync(JsonProbe request, CancellationToken cancellationToken) =>
        ValueTask.FromResult(new JsonProbe(request.Value + "!"));
}

public sealed class BoundProbeEndpoint : Endpoint<int, string>
{
    public override void Configure() => Get("/bound/{id}");

    protected override ValueTask<int> BindAsync(CancellationToken cancellationToken) =>
        int.TryParse(HttpContext.Request.RouteValues["id"] as string, out var id)
            ? ValueTask.FromResult(id)
            : throw new BadHttpRequestException("id must be an integer");

    public override ValueTask<string> HandleAsync(int request, CancellationToken cancellationToken) =>
        ValueTask.FromResult(request.ToString(System.Globalization.CultureInfo.InvariantCulture));
}

public sealed class SilentProbeEndpoint : EndpointWithoutRequest
{
    public override void Configure() => Delete("/silent");

    public override ValueTask HandleAsync(CancellationToken cancellationToken) => ValueTask.CompletedTask;
}

public sealed class ErrorSenderProbeEndpoint : EndpointWithoutRequest
{
    public override void Configure() => Get("/error-sender/{kind}");

    public override ValueTask HandleAsync(CancellationToken cancellationToken) =>
        (HttpContext.Request.RouteValues["kind"] as string) switch
        {
            "conflict" => Send.ConflictAsync(new JsonProbe("taken"), cancellationToken),
            "forbidden" => Send.ForbiddenAsync(cancellationToken),
            _ => Send.ValidationProblemAsync(new Dictionary<string, string[]> { ["name"] = ["required"] }, cancellationToken),
        };
}

public abstract class AbstractProbeEndpoint : EndpointWithoutRequest<string>;
