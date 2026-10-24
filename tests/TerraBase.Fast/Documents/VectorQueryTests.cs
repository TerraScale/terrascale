using System.Globalization;
using System.Net;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Time.Testing;
using TerraBase.Engine;

namespace TerraBase.Fast.Documents;

public sealed class VectorQueryTests
{
    private static readonly double[] Query = [1, 0, 0];

    [Test]
    public async Task Near_sort_ranks_embedded_documents_by_distance_and_combines_with_filters()
    {
        await using var api = await StartAsync();
        var ids = new Dictionary<string, string>();
        foreach (var (name, kind, embedding) in new (string, string, object)[]
        {
            ("far", "doc", new[] { 0.0, 0.0, 1.0 }),
            ("same", "doc", new[] { 2.0, 0.0, 0.0 }),
            ("close", "doc", new[] { 0.8, 0.6, 0.0 }),
            ("short", "doc", new[] { 1.0, 0.0 }),
            ("text", "doc", new object[] { "x", 0, 0 }),
            ("nested", "doc", new object[] { new[] { 1 }, 0, 0 }),
            ("flag", "doc", new object[] { true, 0, 0 }),
            ("other", "note", new[] { 1.0, 0.0, 0.0 }),
        })
        {
            ids[name] = DocumentApi.NewId();
            (await api.PutAsync(ids[name], new { kind, embedding })).Status.ShouldBe(HttpStatusCode.Created);
        }

        var filter = new { eq = new object[] { "/kind", "doc" } };
        var cosine = await api.QueryAsync(new { filter, sort = new[] { new { near = new { path = "/embedding", vector = Query } } } });
        var l2 = await api.QueryAsync(new { filter, sort = new[] { new { near = new { path = "/embedding", vector = Query, metric = "l2" } } }, limit = 2 });

        await Assert.That(cosine.Status).IsEqualTo(HttpStatusCode.OK);
        await Assert.That(Ranked(cosine, ids)).IsEqualTo("same 0, close 0.2, far 1");
        await Assert.That(Ranked(l2, ids)).IsEqualTo("close 0.632, same 1");
        await Assert.That(l2.Json.GetProperty("next_cursor").ValueKind).IsEqualTo(JsonValueKind.Null);
    }

    [Test]
    public async Task Near_sort_rejects_requests_outside_its_limits()
    {
        await using var api = await StartAsync();
        var near = new { near = new { path = "/v", vector = Query } };
        (ApiResponse Response, string Message)[] cases =
        [
            (await api.QueryAsync(new { sort = new object[] { near, new { path = "/a" } } }), "near is the only sort key, without path or order"),
            (await api.QueryAsync(new { sort = new[] { near }, cursor = "x" }), "near returns one page of at most 1000 documents, without a cursor"),
            (await api.QueryAsync(new { sort = new[] { new { near = new { path = "/v", vector = new double[4097] } } } }), "near vector has 1 to 4096 numbers within the float32 range"),
            (await api.QueryAsync(new { sort = new[] { new { near = new { path = "/v", vector = new double[3] } } } }), "a cosine near vector is not all zeros"),
            (await api.QueryAsync(new { sort = new[] { new { near = new { path = "/v", vector = Query, metric = "dot" } } } }), "near metric is cosine or l2"),
            (await api.QueryAsync(new { sort = new[] { new { near = new { path = "$id", vector = Query } } } }), "near path is a JSON Pointer"),
        ];

        foreach (var (response, message) in cases)
        {
            await Assert.That((response.Status, response.Code, response.Message)).IsEqualTo((HttpStatusCode.BadRequest, "invalid_query", message));
        }
    }

    private static string Ranked(ApiResponse result, Dictionary<string, string> ids) => string.Join(", ", result.Json.GetProperty("items").EnumerateArray().Select(item =>
        $"{ids.Single(pair => pair.Value == item.GetProperty("id").GetString()).Key} {Math.Round(item.GetProperty("distance").GetDouble(), 3).ToString(CultureInfo.InvariantCulture)}"));

    private static Task<DocumentApi> StartAsync() => DocumentApi.StartAsync(services: services =>
        services.AddKeyedSingleton<TimeProvider>(EngineExtensions.QueryDeadlineClock, new FakeTimeProvider()));
}
