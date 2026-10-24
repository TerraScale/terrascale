using System.Net;
using System.Text.Json;
using Bogus;
using TerraBase.Testing;

namespace TerraBase.Fast.Documents;

public sealed class VectorIndexTests
{
    private const int Dimensions = 24;
    private static readonly string[] Fields = ["embedding"];

    [Test]
    public async Task A_vector_index_is_backfilled_maintained_and_answers_the_exact_nearest_documents()
    {
        await using var api = await DocumentApi.StartAsync();
        var random = new Randomizer(945);
        var vectors = new Dictionary<string, double[]>();
        double[] Vector()
        {
            return [.. Enumerable.Range(0, Dimensions).Select(_ => random.Double(-1, 1))];
        }

        // One transaction per step keeps the test within the organization's request rate.
        async Task WriteAsync(IEnumerable<object> ops)
        {
            var written = await api.TransactAsync([.. ops]);
            await Assert.That((written.Status, written.Code)).IsEqualTo((HttpStatusCode.OK, (string?)null));
        }

        object Put(string id, int kind, double[] embedding)
        {
            return new { op = "put", collection = DocumentApi.Collection, id, document = new { kind, embedding } };
        }

        object Add(int kind)
        {
            var id = DocumentApi.NewId();
            return Put(id, kind, vectors[id] = Vector());
        }

        await WriteAsync([.. Enumerable.Range(0, 120).Select(i => Add(i % 2))]);

        using var owner = api.Management.As(api.Tenant.User);
        var indexes = $"/v1/management/organizations/{api.Tenant.OrganizationId}/projects/{api.Tenant.ProjectId}/databases/{(await api.TenantOfAsync()).DatabaseId}/indexes";
        var path = ManagementApi.Uri(indexes);
        using (var missing = await owner.PostAsJsonAsync(path, new { collection = DocumentApi.Collection, kind = "vector", fields = Fields }))
        {
            await Assert.That(missing.StatusCode).IsEqualTo(HttpStatusCode.BadRequest);
        }

        using (var created = await owner.PostAsJsonAsync(path, new { name = "embeddings", collection = DocumentApi.Collection, kind = "vector", fields = Fields, dimensions = Dimensions }))
        {
            await Assert.That(created.StatusCode).IsEqualTo(HttpStatusCode.Created);
            var body = await created.Content.ReadFromJsonAsync<JsonElement>();
            await Assert.That((body.GetProperty("kind").GetString(), body.GetProperty("dimensions").GetInt32(), body.GetProperty("state").GetString()))
                .IsEqualTo(("vector", Dimensions, "building"));
        }

        // Written after the build starts: new, replaced, removed, and no longer a vector of the index's length.
        var ids = vectors.Keys.ToList();
        await WriteAsync([.. Enumerable.Range(0, 80).Select(i => Add(i % 2))]);
        await WriteAsync([.. ids.Take(20).Select(id => Put(id, 0, vectors[id] = Vector()))]);
        await WriteAsync([.. ids.Skip(20).Take(10).Select(id => new { op = "delete", collection = DocumentApi.Collection, id })]);
        await WriteAsync([.. ids.Skip(30).Take(10).Select(id => Put(id, 0, [.. vectors[id].Take(Dimensions - 1)]))]);
        foreach (var id in ids.Skip(20).Take(20))
        {
            vectors.Remove(id);
        }

        // The background build adds the documents written before the index; near queries scan until it is active.
        for (var attempt = 0; (await owner.GetFromJsonAsync<JsonElement>(path)).GetProperty("items")[0].GetProperty("state").GetString() != "active"; attempt++)
        {
            await Assert.That(attempt).IsLessThan(150);
            await Task.Delay(200);
        }

        for (var q = 0; q < 5; q++)
        {
            var query = Vector();
            var result = await api.QueryAsync(new { sort = new[] { new { near = new { path = "/embedding", vector = query } } }, limit = 10 });
            await Assert.That(result.Status).IsEqualTo(HttpStatusCode.OK);
            await Assert.That(string.Join(",", result.Ids)).IsEqualTo(string.Join(",", Nearest(vectors, query, 10)));
        }

        using (var dropped = await owner.DeleteAsync(ManagementApi.Uri($"{indexes}/embeddings")))
        {
            await Assert.That(dropped.StatusCode).IsEqualTo(HttpStatusCode.NoContent);
        }

        var scan = Vector();
        var unindexed = await api.QueryAsync(new { sort = new[] { new { near = new { path = "/embedding", vector = scan } } }, limit = 5 });
        await Assert.That(string.Join(",", unindexed.Ids)).IsEqualTo(string.Join(",", Nearest(vectors, scan, 5)));
    }

    private static List<string> Nearest(Dictionary<string, double[]> vectors, double[] query, int count) =>
        [.. vectors.OrderBy(pair => Cosine(pair.Value, query)).ThenBy(pair => pair.Key, StringComparer.Ordinal).Take(count).Select(pair => pair.Key)];

    private static double Cosine(double[] left, double[] right)
    {
        double dot = 0, a = 0, b = 0;
        for (var i = 0; i < left.Length; i++)
        {
            dot += (float)left[i] * (double)(float)right[i];
            a += (float)left[i] * (double)(float)left[i];
            b += (float)right[i] * (double)(float)right[i];
        }

        return 1 - (dot / (Math.Sqrt(a) * Math.Sqrt(b)));
    }
}
