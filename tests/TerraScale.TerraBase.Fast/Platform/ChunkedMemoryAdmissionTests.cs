using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using Bogus;
using Microsoft.Extensions.DependencyInjection;
using TerraBase.Fast.Storage;
using TerraBase.Features.Memory;
using TerraBase.Testing;

namespace TerraBase.Fast.Platform;

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
        const long OperationBytes = 16 * 1024 + BodyBytes * 3;
        await using var api = await ManagementApi.StartAsync(settings => settings["memory-budget"] = "4194304");
        using var tenant = await api.CreateTenantAsync("documents:read", "documents:write");
        var database = await tenant.ContextAsync(api.Server, "app", deadline.Token);
        await StorageFixture.CreateCollectionAsync(tenant, "app", "items", deadline.Token);
        var uri = tenant.Database("app", $"/collections/items/documents/{StorageFixture.Id("fragmented")}");
        var padding = new Faker().Random.String2(BodyBytes - "{\"padding\":\"\"}".Length, "abcdef");
        var json = Encoding.UTF8.GetBytes($"{{\"padding\":\"{padding}\"}}");
        await Assert.That(json.Length).IsEqualTo(BodyBytes);
        var budget = api.Server.Services.GetRequiredService<IMemoryBudget>();
        using (var held = budget.TryReserve(MemoryLane.Bulk, TenantBytes - OperationBytes, database))
        {
            await Assert.That(held).IsNotNull();
            using var content = new FragmentedContent(json, fragmentBytes);
            using var written = await tenant.Data.PutAsync(uri, content, deadline.Token);
            written.EnsureSuccessStatusCode();
            // Reuse exactly the bytes held by middleware and decoder; all body leases must have returned.
            using var returned = budget.TryReserve(MemoryLane.Foreground, OperationBytes, database);
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
