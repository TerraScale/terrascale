using System.Diagnostics.Metrics;
using System.Net;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.DependencyInjection;
using TerraBase.Fast.Management;
using TerraBase.Hosting;

namespace TerraBase.Fast.Documents;

public sealed class CollectionSchemaAttachmentTests
{
    [Test]
    [Arguments("$ref")]
    [Arguments("definitions")]
    [Arguments("dependencies")]
    public Task Unsupported_or_oversized_schema_graphs_are_rejected_before_meta_evaluation_and_preserve_state(string keyword)
    {
        var graph = $$"""{"allOf":[{{string.Join(',', Enumerable.Repeat("true", 2000))}}]}""";
        var invalid = keyword == "$ref"
            ? $$"""{"$ref":"#/examples/0","examples":[{{graph}}]}"""
            : "{\"" + keyword + "\":{\"value\":" + graph + "}}";
        return RejectedBeforeEvaluationAsync(invalid, HttpStatusCode.BadRequest, "invalid_schema");
    }

    [Test]
    public Task Long_dependency_paths_exceed_memory_admission_before_meta_evaluation_and_preserve_state()
    {
        var key = new string('x', 30 * 1024);
        var dependencies = string.Join(',', Enumerable.Range(0, 2000).Select(index => $"\"p{index}\""));
        var schema = "{\"dependentRequired\":{\"" + key + "\":[" + dependencies + "]}}";
        return RejectedBeforeEvaluationAsync(schema, HttpStatusCode.ServiceUnavailable, "storage_busy");
    }

    [Test]
    public async Task Valid_referenced_annotation_schema_is_admitted_and_enforced()
    {
        await using var api = await DocumentApi.StartAsync(settings => settings["memory-budget"] = "16777216");
        await api.Management.SetPlanAsync(api.Tenant.User, api.Tenant.OrganizationId, "enterprise");
        var path = $"/v1/projects/{api.Tenant.ProjectId}/databases/{DocumentApi.Database}/collections/{DocumentApi.Collection}/schema";
        const string Schema = """{"$ref":"#/examples/0","examples":[{"properties":{"name":{"type":"string"}}}]}""";
        (await api.SendRawAsync(HttpMethod.Put, path, Schema, "application/json")).Status.ShouldBe(HttpStatusCode.OK);
        var id = DocumentApi.NewId();
        var original = await api.PutAsync(id, new { name = "original" });
        original.Status.ShouldBe(HttpStatusCode.Created);

        var rejected = await api.PutAsync(id, new { name = 42 });
        await Assert.That((rejected.Status, rejected.Code)).IsEqualTo((HttpStatusCode.BadRequest, "schema_validation_failed"));
        await Assert.That(rejected.Json.GetProperty("error").GetProperty("details").EnumerateArray().Any(detail =>
            detail.GetProperty("path").GetString() == "/name" && detail.GetProperty("keyword").GetString() == "type")).IsTrue();
        var unchanged = await api.GetAsync(id);
        await Assert.That(unchanged.Version).IsEqualTo(original.Version);
        await Assert.That(unchanged.Json.GetProperty("document").GetProperty("name").GetString()).IsEqualTo("original");
    }

    [Test]
    [Arguments(false)]
    [Arguments(true)]
    public async Task Canceling_schema_meta_validation_preserves_state_and_releases_the_writer(bool cancelStorageCheck)
    {
        using var cancellation = new CancellationTokenSource();
        var requests = new CanceledAttachment(cancellation.Token);
        var storage = new UsageCommitHook();
        await using var api = await DocumentApi.StartAsync(services: services =>
        {
            storage.Install(services);
            services.AddSingleton<IStartupFilter>(requests);
        });
        var path = $"/v1/projects/{api.Tenant.ProjectId}/databases/{DocumentApi.Database}/collections/{DocumentApi.Collection}/schema";
        const string OriginalSchema = """{"properties":{"name":{"type":"string"}}}""";
        (await api.SendRawAsync(HttpMethod.Put, path, OriginalSchema, "application/json")).Status.ShouldBe(HttpStatusCode.OK);
        var id = DocumentApi.NewId();
        var original = await api.PutAsync(id, new { name = "original" });
        original.Status.ShouldBe(HttpStatusCode.Created);

        long transactions = 0;
        storage.BeforeBegin = (_, _) =>
        {
            Interlocked.Increment(ref transactions);
            return ValueTask.CompletedTask;
        };
        var meter = api.Server.Services.GetRequiredService<IMeterFactory>().Create(Telemetry.MeterName);
        var counter = cancelStorageCheck ? "terrabase.schema.keyword_evaluations" : "terrabase.schema.admission_keyword_evaluations";
        long typeEvaluations = 0;
        using var listener = new MeterListener();
        listener.InstrumentPublished = (instrument, observer) =>
        {
            if (instrument.Name == counter && ReferenceEquals(instrument.Meter, meter))
            {
                observer.EnableMeasurementEvents(instrument);
            }
        };
        listener.SetMeasurementEventCallback<long>((_, value, tags, _) =>
        {
            foreach (var tag in tags)
            {
                if (tag.Key == "keyword" && Equals(tag.Value, "type") && Interlocked.Add(ref typeEvaluations, value) == 3)
                {
                    cancellation.Cancel();
                }
            }
        });
        listener.Start();

        using var request = new HttpRequestMessage(HttpMethod.Put, new Uri(path, UriKind.Relative))
        {
            Content = new StringContent("""{"properties":{"name":{"type":"integer"}}}""", Encoding.UTF8, "application/json"),
        };
        request.Headers.Add(CanceledAttachment.Header, "true");
        try
        {
            using var response = await api.Client.SendAsync(request, TUnit.Core.TestContext.Current!.Execution.CancellationToken);
            await Assert.That(response.IsSuccessStatusCode).IsFalse();
        }
        catch (HttpRequestException)
        {
            // Kestrel can abort the transport when request cancellation escapes the pipeline.
        }

        await Assert.That(await requests.Finished.Task.WaitAsync(TimeSpan.FromSeconds(30))).IsTrue();
        await Assert.That(Interlocked.Read(ref typeEvaluations)).IsEqualTo(3);
        await Assert.That(Interlocked.Read(ref transactions)).IsEqualTo(cancelStorageCheck ? 1 : 0);
        listener.Dispose();
        storage.BeforeBegin = null;

        var schema = await api.SendAsync(HttpMethod.Get, path);
        schema.Status.ShouldBe(HttpStatusCode.OK);
        using var expected = JsonDocument.Parse(OriginalSchema);
        await Assert.That(JsonElement.DeepEquals(schema.Json.GetProperty("schema"), expected.RootElement)).IsTrue();
        var unchanged = await api.GetAsync(id);
        await Assert.That(unchanged.Version).IsEqualTo(original.Version);
        var written = await api.PutAsync(id, new { name = "after cancellation" });
        written.Status.ShouldBe(HttpStatusCode.OK);
        var updated = await api.GetAsync(id);
        await Assert.That(updated.Json.GetProperty("document").GetProperty("name").GetString()).IsEqualTo("after cancellation");
    }

    private static async Task RejectedBeforeEvaluationAsync(string candidate, HttpStatusCode status, string code)
    {
        await using var api = await DocumentApi.StartAsync(settings => settings["memory-budget"] = "16777216");
        await api.Management.SetPlanAsync(api.Tenant.User, api.Tenant.OrganizationId, "enterprise");
        var path = $"/v1/projects/{api.Tenant.ProjectId}/databases/{DocumentApi.Database}/collections/{DocumentApi.Collection}/schema";
        const string OriginalSchema = """{"properties":{"name":{"type":"string"}}}""";
        (await api.SendRawAsync(HttpMethod.Put, path, OriginalSchema, "application/json")).Status.ShouldBe(HttpStatusCode.OK);
        var id = DocumentApi.NewId();
        var original = await api.PutAsync(id, new { name = "original" });
        original.Status.ShouldBe(HttpStatusCode.Created);

        var meter = api.Server.Services.GetRequiredService<IMeterFactory>().Create(Telemetry.MeterName);
        long evaluations = 0;
        using var listener = new MeterListener();
        listener.InstrumentPublished = (instrument, observer) =>
        {
            if (instrument.Name == "terrabase.schema.admission_keyword_evaluations" && ReferenceEquals(instrument.Meter, meter))
            {
                observer.EnableMeasurementEvents(instrument);
            }
        };
        listener.SetMeasurementEventCallback<long>((_, value, _, _) => Interlocked.Add(ref evaluations, value));
        listener.Start();

        var rejected = await api.SendRawAsync(HttpMethod.Put, path, candidate, "application/json");
        await Assert.That((rejected.Status, rejected.Code)).IsEqualTo((status, code));
        await Assert.That(Interlocked.Read(ref evaluations)).IsEqualTo(0);
        listener.Dispose();

        var schema = await api.SendAsync(HttpMethod.Get, path);
        schema.Status.ShouldBe(HttpStatusCode.OK);
        using var expected = JsonDocument.Parse(OriginalSchema);
        await Assert.That(JsonElement.DeepEquals(schema.Json.GetProperty("schema"), expected.RootElement)).IsTrue();
        var unchanged = await api.GetAsync(id);
        await Assert.That(unchanged.Version).IsEqualTo(original.Version);
        await Assert.That(unchanged.Json.GetProperty("document").GetProperty("name").GetString()).IsEqualTo("original");
        (await api.PutAsync(id, new { name = "after rejection" })).Status.ShouldBe(HttpStatusCode.OK);
        var updated = await api.GetAsync(id);
        await Assert.That(updated.Json.GetProperty("document").GetProperty("name").GetString()).IsEqualTo("after rejection");
    }

    private sealed class CanceledAttachment(CancellationToken cancellation) : IStartupFilter
    {
        public const string Header = "X-Cancel-Schema-Attachment";
        public TaskCompletionSource<bool> Finished { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public Action<IApplicationBuilder> Configure(Action<IApplicationBuilder> next) => app =>
        {
            app.Use(async (context, following) =>
            {
                if (!context.Request.Headers.ContainsKey(Header))
                {
                    await following(context);
                    return;
                }

                using var linked = CancellationTokenSource.CreateLinkedTokenSource(context.RequestAborted, cancellation);
                context.RequestAborted = linked.Token;
                try
                {
                    await following(context);
                }
                finally
                {
                    Finished.TrySetResult(linked.IsCancellationRequested);
                }
            });
            next(app);
        };
    }
}
