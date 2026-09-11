using System.Net;
using System.Text;
using System.Text.Json;
using TerraScale.DotNet.Generated;

namespace TerraScale.DotNet.Tests;

public sealed class ProducerHttpConformanceTests
{
    [Test]
    public async Task GeneratedRoutesMatchEveryPublishedOperation()
    {
        using var owner = await ReadFixtureAsync("openapi.json");
        var operations = owner.RootElement.GetProperty("paths").EnumerateObject()
            .SelectMany(path => path.Value.EnumerateObject()
                .Where(method => method.Value.ValueKind == JsonValueKind.Object && method.Value.TryGetProperty("operationId", out _))
                .Select(method => (Id: method.Value.GetProperty("operationId").GetString()!, Method: method.Name.ToUpperInvariant(), Path: path.Name)))
            .ToArray();

        await Assert.That(operations.Length).IsGreaterThan(0);
        await Assert.That(TerraScaleContractRoutes.All.Count).IsEqualTo(operations.Length);
        foreach (var operation in operations)
        {
            var route = TerraScaleContractRoutes.Find(operation.Id);
            await Assert.That(route.Method).IsEqualTo(operation.Method);
            await Assert.That(route.PathTemplate).IsEqualTo(operation.Path);
        }
    }

    [Test]
    public async Task GeneratedRouteMetadataMatchesPublishedOperations()
    {
        using var owner = await ReadFixtureAsync("openapi.json");
        var operations = owner.RootElement.GetProperty("paths").EnumerateObject()
            .SelectMany(path => path.Value.EnumerateObject()
                .Where(method => method.Value.ValueKind == JsonValueKind.Object && method.Value.TryGetProperty("operationId", out _))
                .Select(method => (Id: method.Value.GetProperty("operationId").GetString()!, Path: path.Value, Operation: method.Value)))
            .ToArray();

        foreach (var operation in operations)
        {
            var route = TerraScaleContractRoutes.Find(operation.Id);
            var responses = operation.Operation.GetProperty("responses");
            var success = responses.EnumerateObject()
                .Where(response => IsSuccessStatus(response.Name))
                .Select(response => response.Name)
                .Order(StringComparer.Ordinal)
                .ToArray();
            var errors = responses.EnumerateObject()
                .Where(response => !IsSuccessStatus(response.Name))
                .Select(response => response.Name)
                .Order(StringComparer.Ordinal)
                .ToArray();
            var requestMediaTypes = operation.Operation.TryGetProperty("requestBody", out var requestBody)
                ? requestBody.GetProperty("content").EnumerateObject().Select(content => content.Name).Order(StringComparer.Ordinal).ToArray()
                : [];
            var responseMediaTypes = responses.EnumerateObject()
                .SelectMany(response => response.Value.TryGetProperty("content", out var content)
                    ? content.EnumerateObject().Select(media => media.Name)
                    : [])
                .Distinct(StringComparer.Ordinal)
                .Order(StringComparer.Ordinal)
                .ToArray();
            var parameters = EnumerateParameters(operation.Path)
                .Concat(EnumerateParameters(operation.Operation))
                .ToArray();

            await Assert.That(route.SuccessStatuses.SequenceEqual(success)).IsTrue();
            await Assert.That(route.ErrorStatuses.SequenceEqual(errors)).IsTrue();
            await Assert.That(route.RequestMediaTypes.SequenceEqual(requestMediaTypes)).IsTrue();
            await Assert.That(route.ResponseMediaTypes.SequenceEqual(responseMediaTypes)).IsTrue();
            var expectedParameters = parameters.Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToArray();
            if (!route.Parameters.SequenceEqual(expectedParameters))
            {
                throw new InvalidOperationException($"{operation.Id}: generated parameters [{string.Join(",", route.Parameters)}], source [{string.Join(",", expectedParameters)}]");
            }
        }
    }

    [Test]
    public async Task PublishedHttpCorpusPreservesTypedErrorsForEveryOperation()
    {
        using var corpus = await ReadFixtureAsync("http.json");
        var operations = corpus.RootElement.GetProperty("operations").EnumerateArray().ToArray();
        var identities = operations.Select(operation => operation.GetProperty("operationId").GetString()!).Order().ToArray();
        await Assert.That(identities.SequenceEqual(TerraScaleContractRoutes.All.Select(route => route.OperationId).Order())).IsTrue();

        foreach (var operation in operations)
        {
            foreach (var fixture in operation.GetProperty("errors").EnumerateArray())
            {
                var body = fixture.GetProperty("body");
                if (operation.GetProperty("operationId").GetString() == "health" && fixture.GetProperty("status").GetInt32() == 503)
                {
                    await Assert.That(body.GetProperty("healthy").GetBoolean()).IsFalse();
                    await Assert.That(body.GetProperty("region").GetString()).IsEqualTo("sample");
                    await Assert.That(body.GetProperty("version").GetString()).IsEqualTo("sample");
                    continue;
                }
                var error = body.GetProperty("error");
                using var response = new HttpResponseMessage((HttpStatusCode)fixture.GetProperty("status").GetInt32())
                {
                    Content = new StringContent(body.GetRawText(), Encoding.UTF8, fixture.GetProperty("mediaType").GetString()!),
                };
                var failure = await TerraScaleApiException.CreateAsync(response, CancellationToken.None);
                await Assert.That(failure.StatusCode).IsEqualTo(response.StatusCode);
                await Assert.That(failure.ErrorCode).IsEqualTo(error.GetProperty("code").GetString());
                await Assert.That(failure.Message).IsEqualTo(error.GetProperty("message").GetString());
                await Assert.That(failure.Fix).IsEqualTo(error.GetProperty("fix").GetString());
            }
        }
    }

    private static async Task<JsonDocument> ReadFixtureAsync(string name)
    {
        await using var stream = File.OpenRead(Path.Combine(AppContext.BaseDirectory, "conformance", name));
        return await JsonDocument.ParseAsync(stream);
    }

    private static bool IsSuccessStatus(string status) =>
        status.Length == 3 && status[0] is '2' or '3' && int.TryParse(status, out _);

    private static IEnumerable<string> EnumerateParameters(JsonElement operation) =>
        operation.TryGetProperty("parameters", out var parameters)
            ? parameters.EnumerateArray().Select(parameter => parameter.GetProperty("name").GetString()!)
            : [];
}
