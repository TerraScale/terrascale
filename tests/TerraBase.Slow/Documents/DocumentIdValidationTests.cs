using System.Net;
using TerraBase.Testing;

namespace TerraBase.Slow.Documents;

public sealed class DocumentIdValidationTests
{
    [Test]
    [Arguments(1)]
    [Arguments(2)]
    public async Task Trailing_NULs_are_rejected_on_document_and_transaction_routes_without_touching_the_valid_id(int nulCount)
    {
        await using var api = await DocumentApi.StartAsync();
        var id = DocumentApi.NewId();
        var original = await api.PutAsync(id, new { n = 1 });
        await Assert.That(original.Status).IsEqualTo(HttpStatusCode.Created);
        var malformedId = id + new string('\0', nulCount);
        var route = api.Document(id) + string.Concat(Enumerable.Repeat("%00", nulCount));

        foreach (var response in new[]
        {
            await api.SendAsync(HttpMethod.Get, route),
            await api.SendAsync(HttpMethod.Put, route, new { n = 2 }),
            await api.SendAsync(HttpMethod.Delete, route),
            await api.TransactAsync(new { op = "put", collection = "items", id = malformedId, document = new { n = 2 } }),
            await api.TransactAsync(new { op = "delete", collection = "items", id = malformedId }),
            await api.TransactAsync(new { op = "assert", collection = "items", id = malformedId, absent = true }),
        })
        {
            await Assert.That(response.Status).IsEqualTo(HttpStatusCode.BadRequest);
        }

        var read = await api.GetAsync(id);
        await Assert.That(read.Version).IsEqualTo(original.Version);
        await Assert.That(read.Json.GetProperty("document").GetProperty("n").GetInt32()).IsEqualTo(1);
        await Assert.That((await api.QueryAsync(new { })).Ids).IsEquivalentTo([id]);
    }
}
