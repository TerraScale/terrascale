using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using TerraScale.Datastar;
using TerraScale.Datastar.DependencyInjection;

namespace TerraScale.Dashboard.FastTests;

public sealed partial class DatastarTests
{
    [Test]
    public async Task PatchElementsUsesDatastarWireFormat()
    {
        var (service, body) = CreateService();
        await service.PatchElementsAsync("<div id=\"status\">ready</div>");

        await Assert.That(body()).IsEqualTo(
            "event: datastar-patch-elements\n" +
            "data: elements <div id=\"status\">ready</div>\n\n");
    }

    [Test]
    public async Task SourceGeneratedSignalsUseWebJsonWithoutReflectionMetadata()
    {
        var (service, body) = CreateService();
        await service.PatchSignalsAsync(
            new TestSignals("ready", 2),
            TestJsonContext.Default.TestSignals);

        await Assert.That(body()).IsEqualTo(
            "event: datastar-patch-signals\n" +
            "data: signals {\"status\":\"ready\",\"count\":2}\n\n");
    }

    [Test]
    public async Task SourceGeneratedSignalsCanBeReadWithoutReflectionMetadata()
    {
        var (service, _) = CreateService("{\"status\":\"ready\",\"count\":3}");
        var signals = await service.ReadSignalsAsync(TestJsonContext.Default.TestSignals);

        await Assert.That(signals).IsNotNull();
        await Assert.That(signals!.Status).IsEqualTo("ready");
        await Assert.That(signals.Count).IsEqualTo(3);
    }

    [Test]
    public async Task RemoveElementDoesNotEmitAnEmptyElementsPayload()
    {
        var (service, body) = CreateService();
        await service.RemoveElementAsync("#status");

        await Assert.That(body()).IsEqualTo(
            "event: datastar-patch-elements\n" +
            "data: selector #status\n" +
            "data: mode remove\n\n");
    }

    private static (IDatastarService Service, Func<string> Body) CreateService(string? requestBody = null)
    {
        var services = new ServiceCollection();
        services.AddDatastar();
        var provider = services.BuildServiceProvider();
        var context = new DefaultHttpContext { RequestServices = provider };
        var output = new MemoryStream();
        context.Response.Body = output;
        if (requestBody is not null)
        {
            context.Request.Method = HttpMethods.Post;
            context.Request.Body = new MemoryStream(Encoding.UTF8.GetBytes(requestBody));
        }
        provider.GetRequiredService<IHttpContextAccessor>().HttpContext = context;
        var service = provider.GetRequiredService<IDatastarService>();
        return (service, () =>
        {
            output.Position = 0;
            return Encoding.UTF8.GetString(output.ToArray());
        });
    }

    private sealed record TestSignals(string Status, int Count);

    [JsonSourceGenerationOptions(JsonSerializerDefaults.Web)]
    [JsonSerializable(typeof(TestSignals))]
    private partial class TestJsonContext : JsonSerializerContext;
}
