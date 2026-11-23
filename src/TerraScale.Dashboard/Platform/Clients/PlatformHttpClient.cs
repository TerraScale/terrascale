using TerraScale.Results;
using System.Globalization;
using System.Net.Http.Headers;
using System.Text;
using static TerraScale.Dashboard.Platform.Clients.ServiceTransport;

namespace TerraScale.Dashboard.Platform.Clients;

public sealed class PlatformHttpClient(ServiceTransport transport)
{
    public const string HttpClientName = "ts-platform";

    private static ClientJson Wire => ClientJson.Default;

    public Task<Result<IReadOnlyList<TerraBaseFunctionInfo>, TerraBaseRefusal>> ListFunctionsAsync(ServiceCaller caller, string projectId, string database,
        CancellationToken cancellationToken) =>
        transport.SendAsync(caller, HttpMethod.Get, Functions(projectId, database), Wire.IReadOnlyListTerraBaseFunctionInfo, cancellationToken);

    public Task<Result<TerraBaseFunctionInfo, TerraBaseRefusal>> GetFunctionAsync(ServiceCaller caller, string projectId, string database, string name,
        CancellationToken cancellationToken) =>
        transport.SendAsync(caller, HttpMethod.Get, Function(projectId, database, name), Wire.TerraBaseFunctionInfo, cancellationToken);

    public Task<Result<IReadOnlyList<TerraBaseFunctionInvocation>, TerraBaseRefusal>> FunctionLogsAsync(ServiceCaller caller, string projectId, string database,
        string name, int? limit, CancellationToken cancellationToken) =>
        transport.SendAsync(caller, HttpMethod.Get, string.Concat(Function(projectId, database, name), "/logs",
            Query(("limit", limit?.ToString(CultureInfo.InvariantCulture)))), Wire.IReadOnlyListTerraBaseFunctionInvocation, cancellationToken);

    public Task<Result<TerraBaseFunctionStats, TerraBaseRefusal>> FunctionStatsAsync(ServiceCaller caller, string projectId, string database, string name,
        string? window, CancellationToken cancellationToken) =>
        transport.SendAsync(caller, HttpMethod.Get, string.Concat(Function(projectId, database, name), "/stats", Query(("window", window))),
            Wire.TerraBaseFunctionStats, cancellationToken);

    public Task<Result<Done, TerraBaseRefusal>> DeleteFunctionAsync(ServiceCaller caller, string projectId, string database, string name,
        CancellationToken cancellationToken) =>
        transport.SendAsync(caller, HttpMethod.Delete, Function(projectId, database, name), null, cancellationToken);

    public Task<Result<TerraBaseFunctionInfo, TerraBaseRefusal>> DeployFunctionAsync(ServiceCaller caller, string projectId, string database, string name,
        TerraBaseFunctionDeploy request, CancellationToken cancellationToken) =>
        transport.SendAsync(caller, HttpMethod.Put, Function(projectId, database, name), Json(request, Wire.TerraBaseFunctionDeploy), Wire.TerraBaseFunctionInfo, null,
            cancellationToken);

    public Task<Result<IReadOnlyList<TerraBaseFunctionSecret>, TerraBaseRefusal>> ListSecretsAsync(ServiceCaller caller, string projectId, string database,
        string name, CancellationToken cancellationToken) =>
        transport.SendAsync(caller, HttpMethod.Get, string.Concat(Function(projectId, database, name), "/secrets"), Wire.IReadOnlyListTerraBaseFunctionSecret,
            cancellationToken);

    public Task<Result<TerraBaseFunctionSecret, TerraBaseRefusal>> SetSecretAsync(ServiceCaller caller, string projectId, string database, string name, string secret,
        TerraBaseFunctionSecretValue request, CancellationToken cancellationToken) =>
        transport.SendAsync(caller, HttpMethod.Put, string.Concat(Function(projectId, database, name), "/secrets/", Segment(secret)),
            Json(request, Wire.TerraBaseFunctionSecretValue), Wire.TerraBaseFunctionSecret, null, cancellationToken);

    public Task<Result<Done, TerraBaseRefusal>> DeleteSecretAsync(ServiceCaller caller, string projectId, string database, string name, string secret,
        CancellationToken cancellationToken) =>
        transport.SendAsync(caller, HttpMethod.Delete, string.Concat(Function(projectId, database, name), "/secrets/", Segment(secret)), null, cancellationToken);

    public async Task<Result<TerraBaseFunctionCall, TerraBaseRefusal>> InvokeFunctionAsync(ServiceCaller caller, string projectId, string database, string name,
        string body, IReadOnlyDictionary<string, string>? query, CancellationToken cancellationToken)
    {
        var path = string.Concat(Function(projectId, database, name), "/invoke", Query([.. (query ?? new Dictionary<string, string>()).Select(static pair => (pair.Key, (string?)pair.Value))]));
        var content = new StringContent(body, Encoding.UTF8, new MediaTypeHeaderValue("application/json"));
        return await transport.SendRawAsync(caller, HttpMethod.Post, path, content, null, cancellationToken) switch
        {
            RawAnswer { FunctionResponse: true } answer => new TerraBaseFunctionCall(answer.Status, answer.ContentType, answer.Body),
            RawAnswer { Status: >= 200 and < 300 } answer => new TerraBaseFunctionCall(answer.Status, answer.ContentType, answer.Body),
            RawAnswer answer => ServiceTransport.Refusal(answer),
            TerraBaseRefusal refusal => refusal,
        };
    }

    public Task<Result<TerraBaseFileBucket, TerraBaseRefusal>> CreateBucketAsync(ServiceCaller caller, string projectId, string name, TerraBaseFileRules rules,
        CancellationToken cancellationToken) =>
        transport.SendAsync(caller, HttpMethod.Post, Buckets(projectId), Json(new TerraBaseFileBucketRequest(name, rules), Wire.TerraBaseFileBucketRequest),
            Wire.TerraBaseFileBucket, null, cancellationToken);

    public Task<Result<TerraBaseFileBucketPage, TerraBaseRefusal>> ListBucketsAsync(ServiceCaller caller, string projectId, string? after,
        CancellationToken cancellationToken) =>
        transport.SendAsync(caller, HttpMethod.Get, string.Concat(Buckets(projectId), Query(("after", after))), Wire.TerraBaseFileBucketPage, cancellationToken);

    public Task<Result<TerraBaseFileBucket, TerraBaseRefusal>> SetBucketRulesAsync(ServiceCaller caller, string projectId, string bucketId, TerraBaseFileRules rules,
        CancellationToken cancellationToken) =>
        transport.SendAsync(caller, HttpMethod.Put, string.Concat(Bucket(projectId, bucketId), "/rules"),
            Json(new TerraBaseFileRulesRequest(rules), Wire.TerraBaseFileRulesRequest), Wire.TerraBaseFileBucket, null, cancellationToken);

    public Task<Result<Done, TerraBaseRefusal>> DeleteBucketAsync(ServiceCaller caller, string projectId, string bucketId, CancellationToken cancellationToken) =>
        transport.SendAsync(caller, HttpMethod.Delete, Bucket(projectId, bucketId), null, cancellationToken);

    public Task<Result<TerraBaseFileUrl, TerraBaseRefusal>> CreateUploadAsync(ServiceCaller caller, string projectId, string bucketId,
        TerraBaseFileUpload upload, CancellationToken cancellationToken) =>
        transport.SendAsync(caller, HttpMethod.Post, string.Concat(Bucket(projectId, bucketId), "/files/upload"), Json(upload, Wire.TerraBaseFileUpload),
            Wire.TerraBaseFileUrl, null, cancellationToken);

    public Task<Result<TerraBaseStoredFile, TerraBaseRefusal>> CompleteUploadAsync(ServiceCaller caller, string projectId, string bucketId, string fileId,
        CancellationToken cancellationToken) =>
        transport.SendAsync(caller, HttpMethod.Post, string.Concat(File(projectId, bucketId, fileId), "/complete"), null, Wire.TerraBaseStoredFile, null,
            cancellationToken);

    public Task<Result<TerraBaseStoredFilePage, TerraBaseRefusal>> ListFilesAsync(ServiceCaller caller, string projectId, string bucketId, string? after,
        CancellationToken cancellationToken) =>
        transport.SendAsync(caller, HttpMethod.Get, string.Concat(Bucket(projectId, bucketId), "/files", Query(("after", after))), Wire.TerraBaseStoredFilePage,
            cancellationToken);

    public Task<Result<TerraBaseFileUrl, TerraBaseRefusal>> CreateDownloadAsync(ServiceCaller caller, string projectId, string bucketId, string fileId,
        CancellationToken cancellationToken) =>
        transport.SendAsync(caller, HttpMethod.Post, string.Concat(File(projectId, bucketId, fileId), "/download"), null, Wire.TerraBaseFileUrl, null,
            cancellationToken);

    public Task<Result<Done, TerraBaseRefusal>> DeleteFileAsync(ServiceCaller caller, string projectId, string bucketId, string fileId,
        CancellationToken cancellationToken) =>
        transport.SendAsync(caller, HttpMethod.Delete, File(projectId, bucketId, fileId), null, cancellationToken);

    public Task<Result<RawAnswer, TerraBaseRefusal>> HealthAsync(CancellationToken cancellationToken) =>
        transport.SendRawAsync(ServiceCaller.Operator, HttpMethod.Get, "/healthz", null, null, cancellationToken);

    private static string Functions(string projectId, string database) =>
        string.Concat("/v1/projects/", Segment(projectId), "/databases/", Segment(database), "/functions");

    private static string Function(string projectId, string database, string name) => string.Concat(Functions(projectId, database), "/", Segment(name));

    private static string Buckets(string projectId) => string.Concat("/v1/projects/", Segment(projectId), "/buckets");

    private static string Bucket(string projectId, string bucketId) => string.Concat(Buckets(projectId), "/", Segment(bucketId));

    private static string File(string projectId, string bucketId, string fileId) => string.Concat(Bucket(projectId, bucketId), "/files/", Segment(fileId));
}
