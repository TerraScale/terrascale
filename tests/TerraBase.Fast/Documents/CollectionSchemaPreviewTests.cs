using System.Diagnostics.Metrics;
using System.Net;
using System.Text;
using Bogus;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.DependencyInjection;
using TerraBase.Fast.Platform;
using TerraBase.Features.Management.Logs;
using TerraBase.Hosting;

namespace TerraBase.Fast.Documents;

public sealed class CollectionSchemaPreviewTests
{
    private const string Schema = """{"properties":{"n":{"minimum":0}}}""";

    [Test]
    public async Task Large_preview_is_read_only_redacted_bound_and_prospective()
    {
        await using var api = await CollectionAccessRulesTests.StartAsync();
        var path = $"{api.Collections}/items/schema";
        var preview = path + "/preview";
        var secret = new Faker().Random.String2(12000);
        var ids = Enumerable.Range(0, 101).Select(_ => DocumentApi.NewId()).ToArray();
        var ops = ids.Select((id, i) => (object)new { op = "put", collection = "items", id, document = new { n = i % 2 == 0 ? 1 : -1, secret } }).ToArray();
        (await api.TransactAsync(ops)).Status.ShouldBe(HttpStatusCode.OK);
        var original = await api.GetAsync(ids[0]);
        var first = await api.SendRawAsync(HttpMethod.Post, preview, Schema, "application/json");
        first.Status.ShouldBe(HttpStatusCode.OK);
        await Assert.That(first.Json.GetProperty("examined").GetInt32()).IsLessThan(100);
        await Assert.That(first.Json.GetProperty("diagnostics").GetArrayLength()).IsEqualTo(16);
        await Assert.That(first.Json.GetRawText().Contains(secret, StringComparison.Ordinal)).IsFalse();
        await Assert.That(first.Json.GetRawText().Contains(ids[0], StringComparison.Ordinal)).IsFalse();
        var cursor = first.Json.GetProperty("next_cursor").GetString()!;
        var continuation = preview + "?cursor=" + Uri.EscapeDataString(cursor);
        await Assert.That((await api.SendRawAsync(HttpMethod.Post, continuation, "true", "application/json")).Code).IsEqualTo("preview_stale");
        await Assert.That((await api.SendRawAsync(HttpMethod.Post, preview + "?cursor=forged", Schema, "application/json")).Code).IsEqualTo("invalid_cursor");
        using var outsider = await api.Management.CreateTenantAsync("documents:read", "documents:write");
        (await DocumentApi.SendAsync(outsider.Data, HttpMethod.Post, continuation, new StringContent(Schema, Encoding.UTF8, "application/json"))).Status.ShouldBe(HttpStatusCode.NotFound);
        using var reader = CollectionAccessRulesTests.Reader(api, DocumentApi.NewId());
        (await DocumentApi.SendAsync(reader, HttpMethod.Post, preview, new StringContent(Schema, Encoding.UTF8, "application/json"))).Status.ShouldBe(HttpStatusCode.Forbidden);
        using var readOnly = await api.ClientWithScopesAsync("documents:read");
        (await DocumentApi.SendAsync(readOnly, HttpMethod.Post, preview, new StringContent(Schema, Encoding.UTF8, "application/json"))).Status.ShouldBe(HttpStatusCode.Forbidden);
        var valid = first.Json.GetProperty("valid").GetInt32();
        var invalid = first.Json.GetProperty("invalid").GetInt32();
        var page = first;
        while (!page.Json.GetProperty("complete").GetBoolean())
        {
            page = await api.SendRawAsync(HttpMethod.Post, preview + "?cursor=" + Uri.EscapeDataString(page.Json.GetProperty("next_cursor").GetString()!), Schema, "application/json");
            page.Status.ShouldBe(HttpStatusCode.OK);
            await Assert.That(page.Json.GetProperty("frontier").GetString()).IsEqualTo(first.Json.GetProperty("frontier").GetString());
            valid += page.Json.GetProperty("valid").GetInt32();
            invalid += page.Json.GetProperty("invalid").GetInt32();
        }

        await Assert.That((valid, invalid)).IsEqualTo((51, 50));
        await Assert.That((await api.GetAsync(ids[0])).Version).IsEqualTo(original.Version);
        await Assert.That((await api.SendAsync(HttpMethod.Get, path)).Json.GetProperty("schema").ValueKind).IsEqualTo(System.Text.Json.JsonValueKind.Null);
        (await api.PutAsync(ids[0], new { n = 2 })).Status.ShouldBe(HttpStatusCode.OK);
        await Assert.That((await api.SendRawAsync(HttpMethod.Post, continuation, Schema, "application/json")).Code).IsEqualTo("preview_stale");
        (await api.SendRawAsync(HttpMethod.Put, path, Schema, "application/json")).Status.ShouldBe(HttpStatusCode.OK);
        await Assert.That((await api.PutAsync(DocumentApi.NewId(), new { n = -1 })).Code).IsEqualTo("schema_validation_failed");
        (await api.TransactAsync([.. ids.Select(id => (object)new { op = "put", collection = "items", id, document = new { n = 1 } })])).Status.ShouldBe(HttpStatusCode.OK);
        var repaired = await api.SendRawAsync(HttpMethod.Post, preview, Schema, "application/json");
        await Assert.That(repaired.Json.GetProperty("invalid").GetInt32()).IsEqualTo(0);
        var tail = await api.SendRawAsync(HttpMethod.Post, preview + "?cursor=" + Uri.EscapeDataString(repaired.Json.GetProperty("next_cursor").GetString()!), Schema, "application/json");
        await Assert.That(tail.Json.GetProperty("invalid").GetInt32()).IsEqualTo(0);
        await Assert.That(tail.Json.GetProperty("complete").GetBoolean()).IsTrue();
    }

    [Test]
    public async Task Canceled_preview_releases_admission_and_allows_a_subsequent_preview()
    {
        using var cancellation = new CancellationTokenSource();
        var requests = new CollectionSchemaTests.CanceledSchemaRequest(cancellation.Token);
        await using var api = await DocumentApi.StartAsync(services: services => services.AddSingleton<IStartupFilter>(requests));
        var meter = api.Server.Services.GetRequiredService<IMeterFactory>().Create(Telemetry.MeterName);
        using var listener = new MeterListener();
        listener.InstrumentPublished = (instrument, observer) =>
        {
            if (instrument.Name == "terrabase.schema.admission_keyword_evaluations" && ReferenceEquals(instrument.Meter, meter))
            {
                observer.EnableMeasurementEvents(instrument);
            }
        };
        listener.SetMeasurementEventCallback<long>((_, _, _, _) => cancellation.Cancel());
        listener.Start();
        var path = $"{api.Collections}/items/schema/preview";
        await api.Server.Services.GetRequiredService<IRequestLogs>().FlushAsync(CancellationToken.None);
        var baseline = await api.ReservedBytesAsync() - MemoryAccounting.Retained(api.Server);
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Post, new Uri(path, UriKind.Relative)) { Content = new StringContent(Schema, Encoding.UTF8, "application/json") };
            request.Headers.Add(CollectionSchemaTests.CanceledSchemaRequest.Header, "true");
            using var canceled = await api.Client.SendAsync(request);
            await Assert.That(canceled.IsSuccessStatusCode).IsFalse();
        }
        catch (HttpRequestException)
        {
            // Kestrel may abort the transport when server-side cancellation escapes the pipeline.
        }

        await Assert.That(await requests.Finished.Task.WaitAsync(TimeSpan.FromSeconds(30))).IsTrue();
        listener.Dispose();
        await api.Server.Services.GetRequiredService<IRequestLogs>().FlushAsync(CancellationToken.None);
        await Assert.That(await api.ReservedBytesAsync() - MemoryAccounting.Retained(api.Server)).IsEqualTo(baseline);
        (await api.SendRawAsync(HttpMethod.Post, path, Schema, "application/json")).Status.ShouldBe(HttpStatusCode.OK);
    }
}
