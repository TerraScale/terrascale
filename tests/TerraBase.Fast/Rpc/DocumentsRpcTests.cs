using System.Text.Json;
using Grpc.Core;
using Grpc.Core.Interceptors;
using Grpc.Net.Client;
using MagicOnion.Client;
using MagicOnion.Serialization.MessagePack;
using MessagePack;
using TerraBase.Testing;
using TerraScale.TerraBase.Contracts;
using TerraScale.TerraBase.Contracts.Documents;

namespace TerraBase.Fast.Rpc;

public sealed class DocumentsRpcTests
{
    [Test]
    public async Task Documents_rpc_reads_and_writes_with_the_callers_scopes()
    {
        await using var api = await DocumentApi.StartAsync();
        var token = await api.Server.IssueAsync(api.Tenant.OrganizationId, ["documents:read", "documents:write"]);
        using var channel = Channel(api);
        var documents = Client(channel, PeerTls.Credential.CorrelationKey, token);
        var id = DocumentApi.NewId();
        using var body = JsonDocument.Parse("""{"name":"ada","n":1}""");

        var created = await documents.PutDocumentAsync(api.Tenant.ProjectId, DocumentApi.Database, DocumentApi.Collection, id, body.RootElement, TerraBasePrecondition.Absent);
        var read = await documents.GetDocumentAsync(api.Tenant.ProjectId, DocumentApi.Database, DocumentApi.Collection, id);
        var absent = await documents.GetDocumentAsync(api.Tenant.ProjectId, DocumentApi.Database, DocumentApi.Collection, DocumentApi.NewId());
        var stale = await Fault(() => documents.PutDocumentAsync(api.Tenant.ProjectId, DocumentApi.Database, DocumentApi.Collection, id, body.RootElement, TerraBasePrecondition.Absent).ResponseAsync);
        using var filter = JsonDocument.Parse("""{"eq":["/name","ada"]}""");
        var page = await documents.QueryAsync(api.Tenant.ProjectId, DocumentApi.Database, DocumentApi.Collection, new TerraBaseQuery(Filter: filter.RootElement, Count: true));
        var transaction = await documents.TransactAsync(api.Tenant.ProjectId, DocumentApi.Database,
            [TerraBaseTransactionOperation.Delete(DocumentApi.Collection, id, read!.Version)]);
        var gone = await documents.GetDocumentAsync(api.Tenant.ProjectId, DocumentApi.Database, DocumentApi.Collection, id);

        await Assert.That(created.Created).IsTrue();
        await Assert.That(read.Version).IsEqualTo(created.Version);
        await Assert.That(read.Document.GetProperty("name").GetString()).IsEqualTo("ada");
        await Assert.That(absent).IsNull();
        await Assert.That((stale.Status, stale.Code)).IsEqualTo((412, "precondition_failed"));
        await Assert.That(page.Items.Select(item => item.Id)).IsEquivalentTo([id]);
        await Assert.That(page.Count!.Value).IsEqualTo(1);
        await Assert.That(transaction.Results.Count).IsEqualTo(1);
        await Assert.That(gone).IsNull();
    }

    [Test]
    public async Task Documents_rpc_refuses_missing_credentials_and_scopes()
    {
        await using var api = await DocumentApi.StartAsync();
        var reader = await api.Server.IssueAsync(api.Tenant.OrganizationId, ["documents:read"]);
        using var channel = Channel(api);
        using var body = JsonDocument.Parse("""{"n":1}""");

        var anonymous = await Assert.ThrowsAsync<RpcException>(async () =>
            await Client(channel, credential: null, token: null).GetDocumentAsync(api.Tenant.ProjectId, DocumentApi.Database, DocumentApi.Collection, DocumentApi.NewId()));
        var forged = await Assert.ThrowsAsync<RpcException>(async () =>
            await Client(channel, new string('0', 64), reader).GetDocumentAsync(api.Tenant.ProjectId, DocumentApi.Database, DocumentApi.Collection, DocumentApi.NewId()));
        var denied = await Fault(() => Client(channel, PeerTls.Credential.CorrelationKey, reader)
            .PutDocumentAsync(api.Tenant.ProjectId, DocumentApi.Database, DocumentApi.Collection, DocumentApi.NewId(), body.RootElement, null).ResponseAsync);

        await Assert.That(anonymous!.StatusCode).IsEqualTo(StatusCode.Unauthenticated);
        await Assert.That(forged!.StatusCode).IsEqualTo(StatusCode.Unauthenticated);
        await Assert.That((denied.Status, denied.Code)).IsEqualTo((403, "forbidden_scope"));
    }

    private static GrpcChannel Channel(DocumentApi api) => GrpcChannel.ForAddress(api.Server.BaseAddress, new GrpcChannelOptions
    {
        MaxReceiveMessageSize = TerraBaseRpc.MaxMessageBytes,
        MaxSendMessageSize = TerraBaseRpc.MaxMessageBytes,
    });

    private static IDocumentsService Client(GrpcChannel channel, string? credential, string? token)
    {
        var invoker = channel.Intercept(metadata =>
        {
            if (credential is not null)
            {
                metadata.Add(TerraBaseRpc.PeerCredentialHeader, credential);
            }

            if (token is not null)
            {
                metadata.Add(TerraBaseRpc.AuthorizationHeader, "Bearer " + token);
            }

            return metadata;
        });
        return MagicOnionClient.Create<IDocumentsService>(invoker, MessagePackMagicOnionSerializerProvider.Default.WithOptions(TerraBaseRpc.SerializerOptions));
    }

    private static async Task<TerraBaseFault> Fault<T>(Func<Task<T>> call)
    {
        var error = await Assert.ThrowsAsync<RpcException>(async () => await call());
        var trailer = error!.Trailers.GetValueBytes(TerraBaseRpc.FaultTrailer) ?? throw new InvalidOperationException("The refusal carries no fault");
        return MessagePackSerializer.Deserialize<TerraBaseFault>(trailer, TerraBaseRpc.SerializerOptions);
    }
}
