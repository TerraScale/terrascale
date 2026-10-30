using System.Net;
using System.Text.Json;
using Grpc.Core;
using Grpc.Core.Interceptors;
using Grpc.Net.Client;
using MagicOnion;
using MagicOnion.Client;
using MagicOnion.Serialization.MessagePack;
using MessagePack;
using Microsoft.Extensions.DependencyInjection;
using TerraBase.Features.Management.Logs;
using TerraBase.Testing;
using TerraScale.TerraBase.Contracts;
using TerraScale.TerraBase.Contracts.Changes;
using TerraScale.TerraBase.Contracts.Documents;
using TerraScale.TerraBase.Contracts.Functions;
using TerraScale.TerraBase.Contracts.Logs;
using TerraScale.TerraBase.Contracts.Management;
using TerraScale.TerraBase.Contracts.Schema;

namespace TerraBase.Fast.Rpc;

public sealed class DataRpcTests
{
    [Test]
    public async Task Schema_rpc_preserves_inferred_declared_and_preview_contracts_and_authorization()
    {
        var ct = TUnit.Core.TestContext.Current!.Execution.CancellationToken;
        await using var api = await DocumentApi.StartAsync(cancellationToken: ct);
        var token = Token(api, "documents:read documents:write databases:read");
        using var channel = Channel(api);
        var schema = Client<ISchemaService>(channel, token).WithCancellationToken(ct);
        var documents = Client<IDocumentsService>(channel, token).WithCancellationToken(ct);
        var id = DocumentApi.NewId();
        using var body = JsonDocument.Parse("""{"n":1}""");
        await documents.PutDocumentAsync(api.Tenant.ProjectId, DocumentApi.Database, DocumentApi.Collection, id, body.RootElement, null);
        var inferred = await schema.GetInferredSchemaAsync(api.Tenant.ProjectId, DocumentApi.Database, DocumentApi.Collection);
        var stats = await Client<IManagementService>(channel, token).WithCancellationToken(ct).ListCollectionStatsAsync(api.Tenant.ProjectId, DocumentApi.Database);
        var detached = await schema.GetDeclaredSchemaAsync(api.Tenant.ProjectId, DocumentApi.Database, DocumentApi.Collection);
        using var proposed = JsonDocument.Parse("""{"type":"object","properties":{"n":{"minimum":2}}}""");
        var preview = await schema.PreviewSchemaAsync(api.Tenant.ProjectId, DocumentApi.Database, DocumentApi.Collection, proposed.RootElement, null);
        var attached = await schema.PutDeclaredSchemaAsync(api.Tenant.ProjectId, DocumentApi.Database, DocumentApi.Collection, proposed.RootElement);
        var refused = await Fault(() => documents.PutDocumentAsync(api.Tenant.ProjectId, DocumentApi.Database, DocumentApi.Collection, DocumentApi.NewId(), body.RootElement, null).ResponseAsync);
        var reader = Token(api, "documents:read");
        var forbidden = await Fault(() => Client<ISchemaService>(channel, reader).PutDeclaredSchemaAsync(api.Tenant.ProjectId, DocumentApi.Database, DocumentApi.Collection, proposed.RootElement).ResponseAsync);
        var removed = await schema.DeleteDeclaredSchemaAsync(api.Tenant.ProjectId, DocumentApi.Database, DocumentApi.Collection);

        await Assert.That(inferred.Documents).IsEqualTo(1);
        await Assert.That(inferred.Fields.Any(field => field.Path == "n")).IsTrue();
        await Assert.That(stats.Items.Single(item => item.Name == DocumentApi.Collection).Documents).IsEqualTo(1);
        await Assert.That(detached.Schema).IsNull();
        await Assert.That((preview.Examined, preview.Invalid, preview.Complete)).IsEqualTo((1, 1, true));
        await Assert.That(preview.Diagnostics.Single().IdHash.Length).IsEqualTo(64);
        await Assert.That(attached.Schema!.Value.GetProperty("type").GetString()).IsEqualTo("object");
        await Assert.That(refused.Status).IsEqualTo(400);
        await Assert.That(forbidden.Status).IsEqualTo(403);
        await Assert.That(removed.Schema).IsNull();
    }

    [Test]
    public async Task Changes_rpc_preserves_transaction_checkpoints_resume_filters_and_snapshot_export()
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(TUnit.Core.TestContext.Current!.Execution.CancellationToken);
        deadline.CancelAfter(TimeSpan.FromSeconds(30));
        var ct = deadline.Token;
        await using var api = await DocumentApi.StartAsync(cancellationToken: ct);
        var token = Token(api, "documents:read documents:write");
        using var channel = Channel(api);
        var changes = Client<IChangesService>(channel, token).WithCancellationToken(ct);
        var documents = Client<IDocumentsService>(channel, token).WithCancellationToken(ct);
        var first = DocumentApi.NewId();
        var second = DocumentApi.NewId();
        using var body = JsonDocument.Parse("""{"n":1}""");
        string checkpoint;
        using (var feed = await changes.StreamChangesAsync(api.Tenant.ProjectId, DocumentApi.Database, "now", null, [DocumentApi.Collection]))
        {
            await Assert.That(await feed.ResponseStream.MoveNext(ct)).IsTrue();
            var before = feed.ResponseStream.Current.Cursor;
            await documents.TransactAsync(api.Tenant.ProjectId, DocumentApi.Database,
                [TerraBaseTransactionOperation.Put(DocumentApi.Collection, first, body.RootElement),
                 TerraBaseTransactionOperation.Put(DocumentApi.Collection, second, body.RootElement)]);
            // Cursor-only events may precede the commit; the first change of a transaction resumes from the checkpoint
            // before it, and only its last change advances the cursor.
            while (await feed.ResponseStream.MoveNext(ct) && feed.ResponseStream.Current.Change is null)
            {
                before = feed.ResponseStream.Current.Cursor;
            }

            var one = feed.ResponseStream.Current;
            await Assert.That(await feed.ResponseStream.MoveNext(ct)).IsTrue();
            var two = feed.ResponseStream.Current;
            await Assert.That(one.Cursor).IsEqualTo(before);
            await Assert.That(two.Cursor == before).IsFalse();
            string[] delivered = [one.Change!.Id, two.Change!.Id];
            await Assert.That(delivered).IsEquivalentTo([first, second]);
            checkpoint = two.Cursor!;
        }

        using (var resumed = await changes.StreamChangesAsync(api.Tenant.ProjectId, DocumentApi.Database, "invalid", checkpoint, [DocumentApi.Collection]))
        {
            await Assert.That(await resumed.ResponseStream.MoveNext(ct)).IsTrue();
            var third = DocumentApi.NewId();
            await documents.PutDocumentAsync(api.Tenant.ProjectId, DocumentApi.Database, DocumentApi.Collection, third, body.RootElement, null);
            await Assert.That(await resumed.ResponseStream.MoveNext(ct)).IsTrue();
            await Assert.That(resumed.ResponseStream.Current.Change!.Id).IsEqualTo(third);
        }

        using (var expired = await changes.StreamChangesAsync(api.Tenant.ProjectId, DocumentApi.Database, null, checkpoint, ["another"]))
        {
            var fault = await Fault(() => expired.ResponseStream.MoveNext(ct));
            await Assert.That((fault.Status, fault.Code)).IsEqualTo((410, "cursor_expired"));
        }

        using var export = await changes.ExportAsync(api.Tenant.ProjectId, DocumentApi.Database);
        var lines = new List<TerraBaseExportLine>();
        while (await export.ResponseStream.MoveNext(ct))
        {
            lines.Add(export.ResponseStream.Current);
        }

        await Assert.That(lines.Count).IsEqualTo(3);
        await Assert.That(lines.All(line => line.Collection == DocumentApi.Collection && line.Document.GetProperty("n").GetInt32() == 1)).IsTrue();
    }

    [Test]
    public async Task Functions_rpc_reads_source_invocations_and_deletes_through_the_rest_pipeline()
    {
        var ct = TUnit.Core.TestContext.Current!.Execution.CancellationToken;
        await using var api = await DocumentApi.StartAsync(cancellationToken: ct);
        var token = Token(api, "documents:read documents:write");
        const string source = "function handler(event) { console.log('rpc'); return {body: {ok: true}}; }";
        var path = api.DatabasePath() + "/functions/hello";
        using var customer = api.Management.WithToken(token);
        (await DocumentApi.SendAsync(customer, HttpMethod.Put, path, JsonContent.Create(new { source }), cancellationToken: ct)).Status.ShouldBe(HttpStatusCode.OK);
        (await DocumentApi.SendAsync(customer, HttpMethod.Post, path + "/invoke", JsonContent.Create(new { }), cancellationToken: ct)).Status.ShouldBe(HttpStatusCode.OK);
        using var channel = Channel(api);
        var functions = Client<IFunctionsService>(channel, token).WithCancellationToken(ct);
        var list = await functions.ListAsync(api.Tenant.ProjectId, DocumentApi.Database);
        var detail = await functions.GetAsync(api.Tenant.ProjectId, DocumentApi.Database, "hello");
        var logs = await functions.LogsAsync(api.Tenant.ProjectId, DocumentApi.Database, "hello", 25);
        var deleted = await functions.DeleteAsync(api.Tenant.ProjectId, DocumentApi.Database, "hello");
        var missing = await Fault(() => functions.GetAsync(api.Tenant.ProjectId, DocumentApi.Database, "hello").ResponseAsync);

        await Assert.That(list.Single().Name).IsEqualTo("hello");
        await Assert.That(list.Single().Source).IsNull();
        await Assert.That(detail.Source).IsEqualTo(source);
        await Assert.That(logs.Single().Lines.Single()).IsEqualTo("rpc");
        await Assert.That(deleted).IsTrue();
        await Assert.That(missing.Status).IsEqualTo(404);
    }

    [Test]
    public async Task Logs_rpc_preserves_public_filters_and_operator_drain_cursor_reads()
    {
        var ct = TUnit.Core.TestContext.Current!.Execution.CancellationToken;
        await using var api = await DocumentApi.StartAsync(cancellationToken: ct);
        var token = Token(api, "documents:read documents:write databases:read");
        using var channel = Channel(api);
        var documents = Client<IDocumentsService>(channel, token).WithCancellationToken(ct);
        var logs = Client<ILogsService>(channel, token).WithCancellationToken(ct);
        await documents.QueryAsync(api.Tenant.ProjectId, DocumentApi.Database, DocumentApi.Collection, new TerraBaseQuery(Count: true));
        await api.Server.Services.GetRequiredService<IRequestLogs>().FlushAsync(ct);
        var tenant = await api.TenantOfAsync(cancellationToken: ct);
        var recent = await logs.ReadAsync(api.Tenant.ProjectId, DocumentApi.Database, "2xx", null);
        var shapes = await logs.ListQueryShapesAsync(api.Tenant.ProjectId, DocumentApi.Database);
        var invalid = await Fault(() => logs.ReadAsync(api.Tenant.ProjectId, DocumentApi.Database, "invalid", null).ResponseAsync);
        const string after = "00000000000000000000000000";
        var drained = await Client<ILogsService>(channel, null).WithCancellationToken(ct).ReadDrainAsync(api.Tenant.ProjectId, tenant.DatabaseId!, after);
        var denied = await Fault(() => logs.ReadDrainAsync(api.Tenant.ProjectId, tenant.DatabaseId!, after).ResponseAsync);

        await Assert.That(recent.DatabaseId).IsEqualTo(tenant.DatabaseId);
        await Assert.That(recent.Items.Count > 0).IsTrue();
        await Assert.That(recent.Items.All(item => item.Status is >= 200 and <= 299 && !item.Route.Contains('?'))).IsTrue();
        await Assert.That(shapes.Items.Count <= 128).IsTrue();
        await Assert.That((invalid.Status, invalid.Code)).IsEqualTo((400, "invalid_status"));
        await Assert.That(drained.DatabaseId).IsEqualTo(tenant.DatabaseId);
        await Assert.That(drained.NextCursor).IsNotNull();
        await Assert.That(drained.Items.SequenceEqual(drained.Items.OrderBy(item => item.Id, StringComparer.Ordinal))).IsTrue();
        await Assert.That(denied.Status).IsEqualTo(404);
    }

    private static string Token(DocumentApi api, string scopes)
    {
        return api.Management.Issuer.Mint(api.Tenant.User, token =>
        {
            token.Claims["project_id"] = api.Tenant.ProjectId;
            token.Claims["scope"] = scopes;
        });
    }

    private static GrpcChannel Channel(DocumentApi api)
    {
        return GrpcChannel.ForAddress(api.Server.BaseAddress, new GrpcChannelOptions
        {
            MaxReceiveMessageSize = TerraBaseRpc.MaxMessageBytes,
            MaxSendMessageSize = TerraBaseRpc.MaxMessageBytes,
        });
    }

    private static T Client<T>(GrpcChannel channel, string? token) where T : IService<T>
    {
        var invoker = channel.Intercept(metadata =>
        {
            metadata.Add(TerraBaseRpc.PeerCredentialHeader, PeerTls.Credential.CorrelationKey ?? throw new InvalidOperationException("The test peer credential is missing."));
            if (token is not null)
            {
                metadata.Add(TerraBaseRpc.AuthorizationHeader, "Bearer " + token);
            }

            return metadata;
        });
        return MagicOnionClient.Create<T>(invoker, MessagePackMagicOnionSerializerProvider.Default.WithOptions(TerraBaseRpc.SerializerOptions));
    }

    private static async Task<TerraBaseFault> Fault<T>(Func<Task<T>> call)
    {
        var error = await Assert.ThrowsAsync<RpcException>(async () => await call());
        var bytes = error!.Trailers.GetValueBytes(TerraBaseRpc.FaultTrailer) ?? throw new InvalidOperationException("The refusal carries no fault.");
        return MessagePackSerializer.Deserialize<TerraBaseFault>(bytes, TerraBaseRpc.SerializerOptions);
    }
}
