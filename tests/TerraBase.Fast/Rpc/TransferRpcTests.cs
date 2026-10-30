using System.Net;
using System.Text;
using Grpc.Core;
using Grpc.Core.Interceptors;
using Grpc.Net.Client;
using MagicOnion;
using MagicOnion.Client;
using MagicOnion.Serialization.MessagePack;
using MessagePack;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using TerraBase.Features.FileStorage;
using TerraBase.Testing;
using TerraScale.TerraBase.Contracts;
using TerraScale.TerraBase.Contracts.FileStorage;
using TerraScale.TerraBase.Contracts.Import;
using TerraScale.TerraBase.Contracts.Webhooks;
using FileObject = TerraBase.Features.FileStorage.FileObject;

namespace TerraBase.Fast.Rpc;

public sealed class TransferRpcTests
{
    [Test]
    public async Task Import_rpc_preserves_parts_replays_options_and_preview_confirmation()
    {
        await using var api = await ImportClient.StartAsync();
        using var channel = Channel(api.Api);
        var imports = Client<IImportService>(channel, ImportClient.Token(api.Api, api.Project, ImportClient.AllScopes));
        var route = await api.CreateDestinationAsync();
        var database = route.Split('/')[5];
        var source = Encoding.UTF8.GetBytes("{\"name\":\"Ada\"}\n{\"name\":\"Grace\"}\n");
        var options = new TerraBaseImportOptions(Collection: "people");
        var created = await imports.CreateImportAsync(api.Project, database, TerraBaseImportFormat.Ndjson, source.Length, ImportClient.Sha256(source), options);
        var replay = await imports.CreateImportAsync(api.Project, database, TerraBaseImportFormat.Ndjson, source.Length, ImportClient.Sha256(source), options);
        await Assert.That(created.Id).IsEqualTo(replay.Id);
        await Assert.That(created.State).IsEqualTo(TerraBaseImportState.AwaitingSource);
        await Assert.That(created.Source.Format).IsEqualTo(TerraBaseImportFormat.Ndjson);
        await Assert.That((await imports.GetImportAsync(api.Project, database)).Source.Bytes).IsEqualTo(source.Length);

        var partial = await imports.UploadImportSourceAsync(api.Project, database, 0, source[..10]);
        var received = await imports.UploadImportSourceAsync(api.Project, database, 0, source[..10]);
        await Assert.That(partial.Source.ReceivedBytes).IsEqualTo(10);
        await Assert.That(received.Source.ReceivedBytes).IsEqualTo(10);
        var offset = await Fault(() => imports.UploadImportSourceAsync(api.Project, database, 11, source[10..]).ResponseAsync);
        await Assert.That((offset.Status, offset.Code)).IsEqualTo((409, "offset_mismatch"));
        _ = await imports.UploadImportSourceAsync(api.Project, database, 10, source[10..]);
        var ready = await Ready(imports, api.Project, database);
        await Assert.That(ready.Preview!.Tables.Single().Rows).IsEqualTo(2);
        await Assert.That(ready.Preview.Tables.Single().Collection).IsEqualTo("people");

        _ = await imports.UpdateImportAsync(api.Project, database, options);
        ready = await Ready(imports, api.Project, database);
        var cancelled = await imports.CancelImportAsync(api.Project, database);
        await Assert.That(cancelled.State).IsEqualTo(TerraBaseImportState.Cancelled);
        _ = await imports.ResumeImportAsync(api.Project, database);
        ready = await Ready(imports, api.Project, database);
        var stale = await Fault(() => imports.StartImportAsync(api.Project, database, "stale-preview").ResponseAsync);
        await Assert.That((stale.Status, stale.Code)).IsEqualTo((409, "preview_changed"));
        var started = await imports.StartImportAsync(api.Project, database, ready.Preview!.Id);
        await Assert.That(started.ConfirmedPreviewId).IsEqualTo(ready.Preview.Id);
        await TerraBaseTestServer.EventuallyAsync(async ct =>
        {
            var job = await imports.WithCancellationToken(ct).GetImportAsync(api.Project, database);
            return job.State == TerraBaseImportState.Completed;
        }, TimeSpan.FromSeconds(30));

        var reader = Client<IImportService>(channel, ImportClient.Token(api.Api, api.Project, "databases:read"));
        var denied = await Fault(() => reader.CancelImportAsync(api.Project, database).ResponseAsync);
        await Assert.That((denied.Status, denied.Code)).IsEqualTo((403, "forbidden_scope"));
    }

    [Test]
    public async Task Import_rpc_accepts_four_MiB_parts_and_preserves_REST_upload_limits()
    {
        await using var api = await ImportClient.StartAsync();
        using var channel = Channel(api.Api);
        var imports = Client<IImportService>(channel, ImportClient.Token(api.Api, api.Project, ImportClient.AllScopes));
        var route = await api.CreateDestinationAsync();
        var database = route.Split('/')[5];
        var source = new byte[ImportLimits.MaxSourcePartBytes + 1];
        _ = await imports.CreateImportAsync(api.Project, database, TerraBaseImportFormat.Ndjson, source.Length, ImportClient.Sha256(source), null);
        var tooLarge = await Fault(() => imports.UploadImportSourceAsync(api.Project, database, 0, source).ResponseAsync);
        var empty = await Fault(() => imports.UploadImportSourceAsync(api.Project, database, 0, []).ResponseAsync);
        await Assert.That(tooLarge.Status).IsEqualTo(413);
        await Assert.That(empty.Status).IsEqualTo(400);
        var part = source[..ImportLimits.MaxSourcePartBytes];
        var uploaded = await imports.UploadImportSourceAsync(api.Project, database, 0, part);
        var replay = await imports.UploadImportSourceAsync(api.Project, database, 0, part);
        await Assert.That(uploaded.Source.ReceivedBytes).IsEqualTo(ImportLimits.MaxSourcePartBytes);
        await Assert.That(replay.Source.ReceivedBytes).IsEqualTo(ImportLimits.MaxSourcePartBytes);
        await Assert.That(replay.State).IsEqualTo(TerraBaseImportState.AwaitingSource);
        _ = await imports.CancelImportAsync(api.Project, database);
    }

    [Test]
    public async Task File_rpc_manages_rules_files_and_returns_browser_URLs()
    {
        await using var api = await ProjectApi.StartAsync(services => services.Replace(ServiceDescriptor.Singleton<IFileObjectStore>(new Objects())), CancellationToken.None);
        using var channel = Channel(api.Api);
        var files = Client<IFileStorageService>(channel, ImportClient.Token(api.Api, api.Database.Project, "documents:read documents:write"));
        var project = api.Database.Project;
        var rules = new TerraBaseFileRules("/owner", new(true, false, []), new(true, false, []));
        var bucket = await files.CreateFileBucketAsync(project, "avatars", rules);
        var changed = await files.SetFileBucketRulesAsync(project, bucket.Id, rules with { Read = new(false, true, []) });
        await Assert.That(changed.Rules.Read.Public).IsTrue();
        var buckets = await files.ListFileBucketsAsync(project, null);
        await Assert.That(buckets.Items.Select(item => item.Id)).Contains(bucket.Id);
        await Assert.That((await files.ListFileBucketsAsync(project, bucket.Id)).Items).IsEmpty();
        var upload = await files.CreateFileUploadAsync(project, bucket.Id,
            new TerraBaseFileUpload("hello.txt", "text/plain", 5, Convert.ToBase64String(new byte[32]), System.Text.Json.JsonElement.Parse("{}")));
        await Assert.That(upload.Url).IsEqualTo("https://files.example/upload");
        await Assert.That(upload.Headers["Content-Type"]).IsEqualTo("text/plain");
        await Assert.That((await files.ListBucketFilesAsync(project, bucket.Id, null)).Items).IsEmpty();
        var pending = await Fault(() => files.CreateFileDownloadAsync(project, bucket.Id, upload.Id).ResponseAsync);
        await Assert.That(pending.Status).IsEqualTo(409);
        var complete = await files.CompleteFileUploadAsync(project, bucket.Id, upload.Id);
        await Assert.That(complete.Status).IsEqualTo("ready");
        await Assert.That((await files.ListBucketFilesAsync(project, bucket.Id, null)).Items.Select(item => item.Id)).Contains(upload.Id);
        await Assert.That((await files.ListBucketFilesAsync(project, bucket.Id, upload.Id)).Items).IsEmpty();
        var download = await files.CreateFileDownloadAsync(project, bucket.Id, upload.Id);
        await Assert.That(download.Url).IsEqualTo("https://files.example/download");
        var reader = Client<IFileStorageService>(channel, ImportClient.Token(api.Api, project, "documents:read"));
        var denied = await Fault(() => reader.DeleteBucketFileAsync(project, bucket.Id, upload.Id).ResponseAsync);
        await Assert.That((denied.Status, denied.Code)).IsEqualTo((403, "forbidden_scope"));
        await files.DeleteBucketFileAsync(project, bucket.Id, upload.Id);
        var gone = await Fault(() => files.CreateFileDownloadAsync(project, bucket.Id, upload.Id).ResponseAsync);
        await Assert.That(gone.Status).IsEqualTo(404);
        var empty = await files.CreateFileBucketAsync(project, "empty", rules);
        await files.DeleteFileBucketAsync(project, empty.Id);
        await Assert.That((await files.ListFileBucketsAsync(project, null)).Items.Select(item => item.Id)).DoesNotContain(empty.Id);
    }

    [Test]
    public async Task Webhook_rpc_preserves_the_producers_missing_route_refusals()
    {
        await using var api = await ProjectApi.StartAsync(CancellationToken.None);
        using var channel = Channel(api.Api);
        var hooks = Client<IWebhooksService>(channel, ImportClient.Token(api.Api, api.Database.Project, "documents:read documents:write"));
        var project = api.Database.Project;
        var database = api.Database.Name;
        var id = DocumentApi.NewId();
        var list = await Fault(() => hooks.ListChangeWebhooksAsync(project, database, "items", null, 100).ResponseAsync);
        var create = await Fault(() => hooks.CreateChangeWebhookAsync(project, database, "items", "https://hooks.example/changes").ResponseAsync);
        var deliveries = await Fault(() => hooks.ListChangeWebhookDeliveriesAsync(project, database, "items", id, null, 100).ResponseAsync);
        var delete = await Fault(() => hooks.DeleteChangeWebhookAsync(project, database, "items", id).ResponseAsync);
        using var rest = await api.Client.GetAsync(new Uri($"/v1/projects/{project}/databases/{database}/collections/items/webhooks?limit=100", UriKind.Relative));
        await Assert.That(rest.StatusCode).IsEqualTo(HttpStatusCode.NotFound);
        await Assert.That(list.Status).IsEqualTo((int)rest.StatusCode);
        await Assert.That((create.Status, deliveries.Status, delete.Status)).IsEqualTo((404, 404, 404));
    }

    private static async Task<TerraBaseImportJob> Ready(IImportService imports, string project, string database)
    {
        TerraBaseImportJob? ready = null;
        await TerraBaseTestServer.EventuallyAsync(async ct =>
        {
            ready = await imports.WithCancellationToken(ct).GetImportAsync(project, database);
            return ready.State == TerraBaseImportState.Ready;
        }, TimeSpan.FromSeconds(30));
        return ready!;
    }

    private static GrpcChannel Channel(ManagementApi api) => GrpcChannel.ForAddress(api.Server.BaseAddress, new GrpcChannelOptions
    {
        MaxReceiveMessageSize = TerraBaseRpc.MaxMessageBytes,
        MaxSendMessageSize = TerraBaseRpc.MaxMessageBytes,
    });

    private static T Client<T>(GrpcChannel channel, string token) where T : IService<T>
    {
        var invoker = channel.Intercept(metadata =>
        {
            metadata.Add(TerraBaseRpc.PeerCredentialHeader, PeerTls.Credential.CorrelationKey ?? throw new InvalidOperationException("The test peer credential is missing"));
            metadata.Add(TerraBaseRpc.AuthorizationHeader, "Bearer " + token);
            return metadata;
        });
        return MagicOnionClient.Create<T>(invoker, MessagePackMagicOnionSerializerProvider.Default.WithOptions(TerraBaseRpc.SerializerOptions));
    }

    private static async Task<TerraBaseFault> Fault<T>(Func<Task<T>> call)
    {
        var error = await Assert.ThrowsAsync<RpcException>(async () => await call());
        var bytes = error!.Trailers.GetValueBytes(TerraBaseRpc.FaultTrailer) ?? throw new InvalidOperationException("The refusal carries no fault");
        return MessagePackSerializer.Deserialize<TerraBaseFault>(bytes, TerraBaseRpc.SerializerOptions);
    }

    private static async Task<TerraBaseFault> Fault(Func<Task> call)
    {
        var error = await Assert.ThrowsAsync<RpcException>(call);
        var bytes = error!.Trailers.GetValueBytes(TerraBaseRpc.FaultTrailer) ?? throw new InvalidOperationException("The refusal carries no fault");
        return MessagePackSerializer.Deserialize<TerraBaseFault>(bytes, TerraBaseRpc.SerializerOptions);
    }

    private sealed class Objects : IFileObjectStore
    {
        public ValueTask<FileSignedUrl> UploadAsync(string region, string key, FileObject file, CancellationToken cancellationToken) =>
            ValueTask.FromResult(new FileSignedUrl(file.Id, "https://files.example/upload", file.UploadExpiresAt, new() { ["Content-Type"] = file.ContentType }));

        public ValueTask<FileSignedUrl> DownloadAsync(string region, string key, FileObject file, CancellationToken cancellationToken) =>
            ValueTask.FromResult(new FileSignedUrl(file.Id, "https://files.example/download", DateTimeOffset.UtcNow.AddMinutes(1), []));

        public ValueTask<bool> VerifyAsync(string region, string key, FileObject file, CancellationToken cancellationToken) => ValueTask.FromResult(true);

        public ValueTask DeleteAsync(string region, string key, CancellationToken cancellationToken) => ValueTask.CompletedTask;
    }
}
