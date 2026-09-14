using System.Text.Json;
using TerraBase.Integration;

namespace TerraBase.Api.Tests;

/// <summary>Smoke coverage for the real engine, producer, and consumer path.</summary>
public sealed class IntegrationAdapterTests
{
    [Test]
    public async Task ProducerAndConsumerShareTheOpenedEngine()
    {
        var adapter = new TerraBaseEngineAdapter(TerraBaseEngine.Open());
        try
        {
            var key = new DocumentKey(new TenantId("integration-tenant"), new CollectionId("documents"), "one");
            using var body = JsonDocument.Parse("{\"source\":\"producer\"}");

            var written = await adapter.Producer.UpsertAsync(key, body.RootElement, DocumentPrecondition.Missing);
            await Assert.That(written.Outcome).IsEqualTo(WriteOutcome.Created);

            var read = await adapter.Consumer.ReadAsync(key);
            await Assert.That(read?.Body.GetProperty("source").GetString()).IsEqualTo("producer");

            var scanned = new List<Document>();
            await foreach (var document in adapter.Consumer.ScanAsync(key.Tenant, key.Collection))
            {
                scanned.Add(document);
            }

            await Assert.That(scanned.Count).IsEqualTo(1);
            await Assert.That(scanned[0].Key).IsEqualTo(key);
            await Assert.That(adapter.Health().IsReady).IsTrue();

            var deleted = await adapter.Producer.DeleteAsync(key, DocumentPrecondition.Version(read!.Version));
            await Assert.That(deleted.Outcome).IsEqualTo(WriteOutcome.Deleted);
            await Assert.That(await adapter.Consumer.ReadAsync(key)).IsNull();
        }
        finally
        {
            await adapter.DisposeAsync();
        }

        async Task ReadDisposedAsync()
        {
            await adapter.Engine.Documents.ReadAsync(
                new DocumentKey(new TenantId("integration-tenant"), new CollectionId("documents"), "one"));
        }

        await Assert.That(ReadDisposedAsync).Throws<ObjectDisposedException>();
    }
}
