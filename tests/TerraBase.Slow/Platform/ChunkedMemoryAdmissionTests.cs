using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using Bogus;
using Microsoft.Extensions.DependencyInjection;
using TerraBase.Engine;
using TerraBase.Features.Memory;
using TerraBase.Slow.Management;
using TerraBase.Testing;

namespace TerraBase.Slow.Platform;

public sealed class ChunkedMemoryAdmissionTests
{
    [Test]
    [Arguments(17)]
    [Arguments(4096)]
    public async Task Fragmented_chunked_body_at_exact_admission_limit_does_not_charge_for_eof(int fragmentBytes)
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(TUnit.Core.TestContext.Current!.Execution.CancellationToken);
        const long TenantBytes = 262_144;
        const int BodyBytes = 4096;
        const long BodyAdmissionBytes = 16 * 1024 + BodyBytes * 3;
        await using var api = await RequestLogMemoryFixture.StartAsync(settings => settings["memory-budget"] = "4194304");
        using var tenant = await api.CreateTenantAsync("documents:read", "documents:write");
        var database = await tenant.ContextAsync(api.Server, "app", deadline.Token);
        await StorageFixture.CreateCollectionAsync(tenant, "app", "items", deadline.Token);
        var store = (await api.Server.Services.GetRequiredService<IStorageEngine>().OpenDatabaseAsync(database, deadline.Token))!;
        long metadataAdmissionBytes;
        await using (var transaction = await store.BeginTransactionAsync(write: false, deadline.Token))
        {
            var metadata = (await transaction.GetAsync(DocumentDelta.Collections, "items", deadline.Token))!;
            // The write parses collection metadata (8x) and retains its stored result (3x + 2048).
            metadataAdmissionBytes = metadata.Json.Length * 11L + 2048;
        }
        // Extra EOF admission of one 4 KiB chunk must still exceed the available bytes.
        await Assert.That(metadataAdmissionBytes).IsLessThan(BodyBytes * 3L);
        var operationBytes = BodyAdmissionBytes + metadataAdmissionBytes;
        var uri = tenant.Database("app", $"/collections/items/documents/by-key?id={StorageFixture.Id("fragmented")}");
        var padding = new Faker().Random.String2(BodyBytes - "{\"padding\":\"\"}".Length, "abcdef");
        var json = Encoding.UTF8.GetBytes($"{{\"padding\":\"{padding}\"}}");
        await Assert.That(json.Length).IsEqualTo(BodyBytes);
        await RequestLogMemoryFixture.DrainAsync(api.Server, deadline.Token);
        var budget = api.Server.Services.GetRequiredService<IMemoryBudget>();
        using (var held = MemoryAccounting.ReserveAvailable(api.Server, MemoryLane.Bulk, TenantBytes - operationBytes, database))
        {
            await Assert.That(held).IsNotNull();
            using var content = new FragmentedContent(json, fragmentBytes);
            using var written = await tenant.Data.PutAsync(uri, content, deadline.Token);
            written.EnsureSuccessStatusCode();
            await RequestLogMemoryFixture.DrainAsync(api.Server, deadline.Token);
            // Reuse the complete request and metadata allowance; every operation lease must have returned.
            using var returned = budget.TryReserve(MemoryLane.Foreground, MemoryAccounting.Available(api.Server, TenantBytes, database) - held!.Bytes, database);
            await Assert.That(returned).IsNotNull();
        }
        using var read = await tenant.Data.GetAsync(uri, deadline.Token);
        read.EnsureSuccessStatusCode();
        await Assert.That((await read.Content.ReadFromJsonAsync<JsonElement>(deadline.Token)).GetProperty("document").GetProperty("padding").GetString())
            .IsEqualTo(padding);
    }

    private sealed class FragmentedContent : HttpContent
    {
        private readonly byte[] _payload;
        private readonly int _fragmentBytes;

        public FragmentedContent(byte[] payload, int fragmentBytes)
        {
            _payload = payload;
            _fragmentBytes = fragmentBytes;
            Headers.ContentType = new MediaTypeHeaderValue("application/json");
        }

        protected override Task SerializeToStreamAsync(Stream stream, TransportContext? context) =>
            SerializeToStreamAsync(stream, context, CancellationToken.None);

        protected override async Task SerializeToStreamAsync(Stream stream, TransportContext? context, CancellationToken cancellationToken)
        {
            for (var offset = 0; offset < _payload.Length; offset += _fragmentBytes)
            {
                await stream.WriteAsync(_payload.AsMemory(offset, Math.Min(_fragmentBytes, _payload.Length - offset)), cancellationToken);
                await stream.FlushAsync(cancellationToken);
            }
        }

        protected override bool TryComputeLength(out long length)
        {
            length = 0;
            return false;
        }
    }
}
