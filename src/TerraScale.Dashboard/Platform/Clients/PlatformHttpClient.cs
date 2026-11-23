using System.Globalization;
using System.Net.Http.Headers;
using System.Text;
using static TerraScale.Dashboard.Platform.Clients.ServiceTransport;

namespace TerraScale.Dashboard.Platform.Clients;

public sealed class PlatformHttpClient(ServiceTransport transport)
{
    public const string HttpClientName = "ts-platform";

    private static ClientJson Wire => ClientJson.Default;

    public Task<TerraBaseFunctionListOutcome> ListFunctionsAsync(ServiceCaller caller, string projectId, string database,
        CancellationToken cancellationToken) =>
        transport.SendAsync<IReadOnlyList<TerraBaseFunctionInfo>, TerraBaseFunctionListOutcome>(caller, HttpMethod.Get, Functions(projectId, database), Wire.IReadOnlyListTerraBaseFunctionInfo, cancellationToken);

    public Task<TerraBaseFunctionOutcome> GetFunctionAsync(ServiceCaller caller, string projectId, string database, string name,
        CancellationToken cancellationToken) =>
        transport.SendAsync<TerraBaseFunctionInfo, TerraBaseFunctionOutcome>(caller, HttpMethod.Get, Function(projectId, database, name), Wire.TerraBaseFunctionInfo, cancellationToken);

    public Task<TerraBaseFunctionInvocationsOutcome> FunctionLogsAsync(ServiceCaller caller, string projectId, string database,
        string name, int? limit, CancellationToken cancellationToken) =>
        transport.SendAsync<IReadOnlyList<TerraBaseFunctionInvocation>, TerraBaseFunctionInvocationsOutcome>(caller, HttpMethod.Get, string.Concat(Function(projectId, database, name), "/logs",
            Query(("limit", limit?.ToString(CultureInfo.InvariantCulture)))), Wire.IReadOnlyListTerraBaseFunctionInvocation, cancellationToken);

    public Task<TerraBaseFunctionStatsOutcome> FunctionStatsAsync(ServiceCaller caller, string projectId, string database, string name,
        string? window, CancellationToken cancellationToken) =>
        transport.SendAsync<TerraBaseFunctionStats, TerraBaseFunctionStatsOutcome>(caller, HttpMethod.Get, string.Concat(Function(projectId, database, name), "/stats", Query(("window", window))),
            Wire.TerraBaseFunctionStats, cancellationToken);

    public Task<TerraBaseActionOutcome> DeleteFunctionAsync(ServiceCaller caller, string projectId, string database, string name,
        CancellationToken cancellationToken) =>
        transport.SendAsync(caller, HttpMethod.Delete, Function(projectId, database, name), null, cancellationToken);

    public Task<TerraBaseFunctionOutcome> DeployFunctionAsync(ServiceCaller caller, string projectId, string database, string name,
        TerraBaseFunctionDeploy request, CancellationToken cancellationToken) =>
        transport.SendAsync<TerraBaseFunctionInfo, TerraBaseFunctionOutcome>(caller, HttpMethod.Put, Function(projectId, database, name), Json(request, Wire.TerraBaseFunctionDeploy), Wire.TerraBaseFunctionInfo, null,
            cancellationToken);

    public Task<TerraBaseFunctionSecretsOutcome> ListSecretsAsync(ServiceCaller caller, string projectId, string database,
        string name, CancellationToken cancellationToken) =>
        transport.SendAsync<IReadOnlyList<TerraBaseFunctionSecret>, TerraBaseFunctionSecretsOutcome>(caller, HttpMethod.Get, string.Concat(Function(projectId, database, name), "/secrets"), Wire.IReadOnlyListTerraBaseFunctionSecret,
            cancellationToken);

    public Task<TerraBaseFunctionSecretOutcome> SetSecretAsync(ServiceCaller caller, string projectId, string database, string name, string secret,
        TerraBaseFunctionSecretValue request, CancellationToken cancellationToken) =>
        transport.SendAsync<TerraBaseFunctionSecret, TerraBaseFunctionSecretOutcome>(caller, HttpMethod.Put, string.Concat(Function(projectId, database, name), "/secrets/", Segment(secret)),
            Json(request, Wire.TerraBaseFunctionSecretValue), Wire.TerraBaseFunctionSecret, null, cancellationToken);

    public Task<TerraBaseActionOutcome> DeleteSecretAsync(ServiceCaller caller, string projectId, string database, string name, string secret,
        CancellationToken cancellationToken) =>
        transport.SendAsync(caller, HttpMethod.Delete, string.Concat(Function(projectId, database, name), "/secrets/", Segment(secret)), null, cancellationToken);

    public async Task<TerraBaseFunctionCallOutcome> InvokeFunctionAsync(ServiceCaller caller, string projectId, string database, string name,
        string body, IReadOnlyDictionary<string, string>? query, CancellationToken cancellationToken)
    {
        var path = string.Concat(Function(projectId, database, name), "/invoke", Query([.. (query ?? new Dictionary<string, string>()).Select(static pair => (pair.Key, (string?)pair.Value))]));
        var content = new StringContent(body, Encoding.UTF8, new MediaTypeHeaderValue("application/json"));
        var outcome = await transport.SendRawAsync(caller, HttpMethod.Post, path, content, null, cancellationToken);
        return outcome switch
        {
            RawAnswer { FunctionResponse: true } answer => new TerraBaseFunctionCall(answer.Status, answer.ContentType, answer.Body),
            RawAnswer { Status: >= 200 and < 300 } answer => new TerraBaseFunctionCall(answer.Status, answer.ContentType, answer.Body),
            RawAnswer answer => ServiceTransport.Refusal(answer),
            TerraBaseRefusal refusal => refusal,
        };
    }

    public Task<TerraBaseFileBucketOutcome> CreateBucketAsync(ServiceCaller caller, string projectId, string name, TerraBaseFileRules rules,
        CancellationToken cancellationToken) =>
        transport.SendAsync<TerraBaseFileBucket, TerraBaseFileBucketOutcome>(caller, HttpMethod.Post, Buckets(projectId), Json(new TerraBaseFileBucketRequest(name, rules), Wire.TerraBaseFileBucketRequest),
            Wire.TerraBaseFileBucket, null, cancellationToken);

    public Task<TerraBaseFileBucketPageOutcome> ListBucketsAsync(ServiceCaller caller, string projectId, string? after,
        CancellationToken cancellationToken) =>
        transport.SendAsync<TerraBaseFileBucketPage, TerraBaseFileBucketPageOutcome>(caller, HttpMethod.Get, string.Concat(Buckets(projectId), Query(("after", after))), Wire.TerraBaseFileBucketPage, cancellationToken);

    public Task<TerraBaseFileBucketOutcome> SetBucketRulesAsync(ServiceCaller caller, string projectId, string bucketId, TerraBaseFileRules rules,
        CancellationToken cancellationToken) =>
        transport.SendAsync<TerraBaseFileBucket, TerraBaseFileBucketOutcome>(caller, HttpMethod.Put, string.Concat(Bucket(projectId, bucketId), "/rules"),
            Json(new TerraBaseFileRulesRequest(rules), Wire.TerraBaseFileRulesRequest), Wire.TerraBaseFileBucket, null, cancellationToken);

    public Task<TerraBaseActionOutcome> DeleteBucketAsync(ServiceCaller caller, string projectId, string bucketId, CancellationToken cancellationToken) =>
        transport.SendAsync(caller, HttpMethod.Delete, Bucket(projectId, bucketId), null, cancellationToken);

    public Task<TerraBaseFileUrlOutcome> CreateUploadAsync(ServiceCaller caller, string projectId, string bucketId,
        TerraBaseFileUpload upload, CancellationToken cancellationToken) =>
        transport.SendAsync<TerraBaseFileUrl, TerraBaseFileUrlOutcome>(caller, HttpMethod.Post, string.Concat(Bucket(projectId, bucketId), "/files/upload"), Json(upload, Wire.TerraBaseFileUpload),
            Wire.TerraBaseFileUrl, null, cancellationToken);

    public Task<TerraBaseStoredFileOutcome> CompleteUploadAsync(ServiceCaller caller, string projectId, string bucketId, string fileId,
        CancellationToken cancellationToken) =>
        transport.SendAsync<TerraBaseStoredFile, TerraBaseStoredFileOutcome>(caller, HttpMethod.Post, string.Concat(File(projectId, bucketId, fileId), "/complete"), null, Wire.TerraBaseStoredFile, null,
            cancellationToken);

    public Task<TerraBaseStoredFilePageOutcome> ListFilesAsync(ServiceCaller caller, string projectId, string bucketId, string? after,
        CancellationToken cancellationToken) =>
        transport.SendAsync<TerraBaseStoredFilePage, TerraBaseStoredFilePageOutcome>(caller, HttpMethod.Get, string.Concat(Bucket(projectId, bucketId), "/files", Query(("after", after))), Wire.TerraBaseStoredFilePage,
            cancellationToken);

    public Task<TerraBaseFileUrlOutcome> CreateDownloadAsync(ServiceCaller caller, string projectId, string bucketId, string fileId,
        CancellationToken cancellationToken) =>
        transport.SendAsync<TerraBaseFileUrl, TerraBaseFileUrlOutcome>(caller, HttpMethod.Post, string.Concat(File(projectId, bucketId, fileId), "/download"), null, Wire.TerraBaseFileUrl, null,
            cancellationToken);

    public Task<TerraBaseActionOutcome> DeleteFileAsync(ServiceCaller caller, string projectId, string bucketId, string fileId,
        CancellationToken cancellationToken) =>
        transport.SendAsync(caller, HttpMethod.Delete, File(projectId, bucketId, fileId), null, cancellationToken);

    public Task<TerraBaseRawOutcome> HealthAsync(CancellationToken cancellationToken) =>
        transport.SendRawAsync(ServiceCaller.Operator, HttpMethod.Get, "/healthz", null, null, cancellationToken);

    private static string Functions(string projectId, string database) =>
        string.Concat("/v1/projects/", Segment(projectId), "/databases/", Segment(database), "/functions");

    private static string Function(string projectId, string database, string name) => string.Concat(Functions(projectId, database), "/", Segment(name));

    private static string Buckets(string projectId) => string.Concat("/v1/projects/", Segment(projectId), "/buckets");

    private static string Bucket(string projectId, string bucketId) => string.Concat(Buckets(projectId), "/", Segment(bucketId));

    private static string File(string projectId, string bucketId, string fileId) => string.Concat(Bucket(projectId, bucketId), "/files/", Segment(fileId));
}
