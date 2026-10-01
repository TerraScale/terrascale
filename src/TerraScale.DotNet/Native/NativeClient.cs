using System.Collections.Immutable;
using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;

namespace TerraScale.DotNet.Native;

/// <summary>Native v1 transport. Credentials and admission are resolved anew for every finite request.</summary>
/// <remarks>Unexpected provider programming and lifecycle exceptions propagate. Invalid credentials and admissions return distinct local failure codes.</remarks>
public sealed class NativeClient : IDisposable
{
    private readonly Guid owner = Guid.NewGuid();
    private readonly HttpClient client;
    private readonly INativeAdmissionProvider admissionProvider;
    private readonly Func<CancellationToken, ValueTask<string>> keyProvider;
    private readonly TimeSpan timeout;
    private readonly string? explicitOrigin;
    private readonly bool ownsClient;
    private int disposed;

    /// <summary>Creates a client with an owned strict transport and current scoped credential/admission providers.</summary>
    public NativeClient(NativeScope scope, INativeAdmissionProvider admissionProvider,
        Func<CancellationToken, ValueTask<string>> keyProvider, TimeSpan timeout)
        : this(scope, admissionProvider, keyProvider, timeout, NativeTransport.CreateClient(), ownsClient: true)
    {
    }

    /// <summary>Creates an owned strict transport for a previously admitted direct origin.</summary>
    /// <remarks>This constructor does not discover eligibility or mint a grant. HTTP loopback requires explicit development mode.</remarks>
    public NativeClient(NativeScope scope, string admittedOrigin,
        Func<CancellationToken, ValueTask<string>> keyProvider, TimeSpan timeout, bool development = false)
        : this(scope, new NativeDirectAdmissionProvider(admittedOrigin, development), keyProvider, timeout,
            NativeTransport.CreateClient(), ownsClient: true)
    {
    }

    /// <summary>Creates an owned transport and transfers disposal ownership of the supplied handler to this client.</summary>
    /// <remarks>The handler must disable redirects, cookies and automatic decompression; do not share or dispose it separately.</remarks>
    public NativeClient(NativeScope scope, INativeAdmissionProvider admissionProvider,
        Func<CancellationToken, ValueTask<string>> keyProvider, TimeSpan timeout, HttpMessageHandler handler)
        : this(scope, admissionProvider, keyProvider, timeout,
            NativeTransport.CreateClient(handler ?? throw new ArgumentNullException(nameof(handler))), ownsClient: true)
    {
    }

    /// <summary>Creates a native client without changing or taking ownership of the caller's HttpClient.</summary>
    /// <remarks>The caller must disable redirects, cookies and automatic decompression. Its own timeout can shorten the native deadline.</remarks>
    public NativeClient(HttpClient httpClient, NativeScope scope, INativeAdmissionProvider admissionProvider,
        Func<CancellationToken, ValueTask<string>> keyProvider, TimeSpan timeout)
        : this(scope, admissionProvider, keyProvider, timeout, httpClient, ownsClient: false)
    {
    }

    /// <summary>Uses a caller-owned HttpClient with a previously admitted direct origin.</summary>
    /// <remarks>This constructor proves no discovery eligibility or scoped grant. HTTP loopback requires explicit development mode.
    /// The caller must disable redirects, cookies and automatic decompression; its timeout can shorten the native deadline.</remarks>
    public NativeClient(HttpClient httpClient, NativeScope scope, string admittedOrigin,
        Func<CancellationToken, ValueTask<string>> keyProvider, TimeSpan timeout, bool development = false)
        : this(scope, new NativeDirectAdmissionProvider(admittedOrigin, development), keyProvider, timeout, httpClient, ownsClient: false)
    {
    }

    private NativeClient(NativeScope scope, INativeAdmissionProvider admissionProvider,
        Func<CancellationToken, ValueTask<string>> keyProvider, TimeSpan timeout, HttpClient httpClient, bool ownsClient)
    {
        try
        {
            Scope = NativeCodec.ValidateScope(scope);
            this.admissionProvider = admissionProvider ?? throw new ArgumentNullException(nameof(admissionProvider));
            this.keyProvider = keyProvider ?? throw new ArgumentNullException(nameof(keyProvider));
            if (timeout <= TimeSpan.Zero || timeout > TimeSpan.FromMinutes(5)) throw new ArgumentOutOfRangeException(nameof(timeout));
            this.timeout = timeout;
            explicitOrigin = (admissionProvider as NativeDirectAdmissionProvider)?.Origin;
            client = httpClient ?? throw new ArgumentNullException(nameof(httpClient));
            this.ownsClient = ownsClient;
        }
        catch
        {
            if (ownsClient) httpClient?.Dispose();
            throw;
        }
    }

    /// <summary>The immutable tenant, database and shard bound to every native request.</summary>
    public NativeScope Scope { get; }

    /// <summary>Validates and captures immutable body bytes and original identity before any dispatch.</summary>
    public NativePreparedCommit PrepareCommit(NativeCommit commit)
    {
        ThrowIfDisposed();
        var encoded = NativeCodec.EncodeCommit(Scope, commit);
        return new NativePreparedCommit(owner, Scope, explicitOrigin, encoded.Identity, encoded.Body, CommitCapabilities(commit));
    }

    /// <summary>Restoration revalidates the retained scope, explicit-origin binding and canonical effect.</summary>
    public NativePreparedCommit RestoreCommit(NativeRetainedOperation retained)
    {
        ThrowIfDisposed();
        if (retained.Scope != Scope || retained.ExplicitOrigin != explicitOrigin)
            throw new InvalidDataException("Retained native operation belongs to another scope or explicit origin.");
        using var document = NativeCodec.Parse(retained.RequestBody);
        var commit = NativeCodec.DecodeCommit(Scope, document.RootElement, out var identity);
        NativeCodec.RequireIdentity(identity, retained.Identity);
        return new NativePreparedCommit(owner, Scope, explicitOrigin, identity, retained.RequestBody, CommitCapabilities(commit));
    }

    /// <summary>Fixes the collection, typed predicate and limit across opaque continuation pages.</summary>
    public NativePreparedQuery PrepareQuery(string collection, NativeQueryPredicate predicate, int limit)
    {
        ThrowIfDisposed();
        NativeCodec.CanonicalUuid(collection, "collection");
        NativeCodec.EncodeQuery(predicate, limit);
        return new NativePreparedQuery(owner, collection, predicate, limit);
    }

    /// <summary>Reads a complete native Point using current scope, credentials and endpoint admission.</summary>
    public Task<NativeResult<NativePoint>> ReadAsync(string collection, string document, CancellationToken cancellationToken = default)
    {
        var path = CollectionPath(collection) + "/documents/" + NativeCodec.CanonicalUuid(document, "document");
        return RequestAsync(path, HttpMethod.Get, null, root =>
        {
            var point = NativeCodec.DecodePoint(root);
            NativeCodec.RequireScope(point, Scope);
            return point;
        }, ["client_v1", "read"], cancellationToken);
    }

    /// <summary>Fetches one bounded page for the prepared query; cursors remain producer-issued and opaque.</summary>
    public Task<NativeResult<NativeQueryPage>> QueryAsync(NativePreparedQuery query, string? cursor = null,
        CancellationToken cancellationToken = default)
    {
        if (query.Owner != owner) throw new InvalidDataException("Prepared native query belongs to another client.");
        var body = NativeCodec.EncodeQuery(query.Predicate, query.Limit, cursor);
        return RequestAsync(CollectionPath(query.Collection) + "/documents/query", HttpMethod.Post, body, root =>
        {
            var page = NativeCodec.DecodeQueryPage(root, query.Limit);
            foreach (var row in page.Rows) NativeCodec.RequireScope(row.Point, Scope);
            return page;
        }, ["client_v1", "read", "query"], cancellationToken);
    }

    /// <summary>One attempt only. Repeating this method sends exactly the prepared body and identity.</summary>
    public Task<NativeResult<NativeCommitReceipt>> CommitAsync(NativePreparedCommit prepared, CancellationToken cancellationToken = default)
    {
        RequirePrepared(prepared);
        return RequestAsync(NativeCodec.ScopePath(Scope) + "/transactions", HttpMethod.Post, prepared.RequestBody, root =>
        {
            var receipt = NativeCodec.DecodeReceipt(root);
            NativeCodec.RequireIdentity(receipt.Identity, prepared.Identity);
            NativeCodec.RequireScope(receipt.Event, Scope);
            return receipt;
        }, prepared.Capabilities, cancellationToken, prepared.Identity, mutation: true);
    }

    private static ImmutableArray<string> CommitCapabilities(NativeCommit commit)
    {
        var capabilities = ImmutableArray.CreateBuilder<string>();
        capabilities.Add("client_v1");
        capabilities.Add("write");
        if (commit.Writes.Any(write => write.Condition is not NativeCondition.None)) capabilities.Add("conditional_mutation");
        if (commit.Writes.Length > 1) capabilities.Add("atomic_groups");
        if (commit.Authority is NativeAuthority.SingleWriter) capabilities.Add("security_state");
        return capabilities.ToImmutable();
    }

    /// <summary>Resolves status for the original immutable identity without submitting a mutation.</summary>
    public Task<NativeResult<NativeTransactionStatus>> TransactionStatusAsync(NativeTransactionIdentity identity,
        CancellationToken cancellationToken = default)
    {
        var body = NativeCodec.EncodeStatus(identity);
        IReadOnlyCollection<string> capabilities = identity.Authority is NativeAuthority.SingleWriter
            ? ["client_v1", "write", "security_state"] : ["client_v1", "write"];
        return RequestAsync(NativeCodec.ScopePath(Scope) + "/transactions/status", HttpMethod.Post, body, root =>
        {
            var status = NativeCodec.DecodeStatus(root);
            NativeCodec.RequireIdentity(status.Identity, identity);
            if (status.Receipt is not null) NativeCodec.RequireScope(status.Receipt.Event, Scope);
            return status;
        }, capabilities, cancellationToken, identity);
    }

    internal void RequirePrepared(NativePreparedCommit prepared)
    {
        ThrowIfDisposed();
        if (prepared.Owner != owner || prepared.Scope != Scope || prepared.ExplicitOrigin != explicitOrigin)
            throw new InvalidDataException("Prepared native commit belongs to another client, scope or explicit origin.");
    }

    private string CollectionPath(string collection) => NativeCodec.ScopePath(Scope) + "/collections/" + NativeCodec.CanonicalUuid(collection, "collection");

    private async Task<NativeResult<T>> RequestAsync<T>(string path, HttpMethod method, string? body,
        Func<JsonElement, T> decode, IReadOnlyCollection<string> capabilities, CancellationToken cancellationToken,
        NativeTransactionIdentity? identity = null, bool mutation = false)
    {
        ThrowIfDisposed();
        using var budget = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        budget.CancelAfter(timeout);
        var sent = false;
        int? status = null;
        TimeSpan? retryAfter = null;
        var phase = "credential";
        try
        {
            budget.Token.ThrowIfCancellationRequested();
            var key = ValidateKey(await keyProvider(budget.Token).AsTask().WaitAsync(budget.Token).ConfigureAwait(false));
            phase = "admission";
            var admission = await admissionProvider.ResolveAsync(Scope, key, capabilities, budget.Token).AsTask()
                .WaitAsync(budget.Token).ConfigureAwait(false);
            using var request = new HttpRequestMessage(method, new Uri(admission.Origin, path));
            request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", key);
            if (body is not null)
            {
                request.Content = new ByteArrayContent(Encoding.UTF8.GetBytes(body));
                request.Content.Headers.ContentType = new MediaTypeHeaderValue("application/json");
            }
            budget.Token.ThrowIfCancellationRequested();
            admission.AssertCurrent();
            budget.Token.ThrowIfCancellationRequested();
            phase = "response";
            sent = true;
            using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, budget.Token).ConfigureAwait(false);
            status = (int)response.StatusCode;
            retryAfter = RetryAfter(response);
            if (response.IsSuccessStatusCode && response.StatusCode != HttpStatusCode.OK)
                throw new InvalidDataException("Native protocol requires HTTP 200 success.");
            if (response.Content.Headers.ContentType?.MediaType != "application/json" || response.Content.Headers.ContentEncoding.Count > 0)
                throw new InvalidDataException("Native response has unsupported media type or encoding.");
            var bytes = await ReadBodyAsync(response, budget.Token).ConfigureAwait(false);
            using var document = NativeCodec.Parse(bytes);
            if (!response.IsSuccessStatusCode)
            {
                var code = NativeCodec.DecodeError(document.RootElement);
                var outcome = mutation
                    ? code == "precondition_failed" && status == 412 ? NativeMutationOutcome.Rejected
                        : code == "commit_uncertain" ? NativeMutationOutcome.Uncertain : NativeMutationOutcome.Unknown
                    : NativeMutationOutcome.Unknown;
                if (code.Contains(key, StringComparison.Ordinal)) code = "invalid_response";
                return Failure<T>(code, status, retryAfter, identity, outcome);
            }
            return new NativeResult<T>.Success(decode(document.RootElement));
        }
        catch (NativeAdmissionException exception)
        {
            return Failure<T>(exception.Code, exception.Status, null, identity, NativeMutationOutcome.Unknown);
        }
        catch (OperationCanceledException)
        {
            return Failure<T>(cancellationToken.IsCancellationRequested ? "cancelled" : "deadline_exceeded", status, retryAfter,
                identity, mutation && sent ? NativeMutationOutcome.Uncertain : NativeMutationOutcome.Unknown);
        }
        catch (Exception exception) when (exception is InvalidDataException or JsonException or FormatException)
        {
            return Failure<T>(phase == "credential" ? "invalid_credential" : phase == "admission" ? "invalid_admission" : "invalid_response", status, retryAfter, identity,
                mutation && sent ? NativeMutationOutcome.Uncertain : NativeMutationOutcome.Unknown);
        }
        catch (Exception exception) when (exception is HttpRequestException or IOException)
        {
            return Failure<T>("transport_unavailable", status, retryAfter, identity,
                mutation && sent ? NativeMutationOutcome.Uncertain : NativeMutationOutcome.Unknown);
        }
    }

    private static NativeResult<T> Failure<T>(string code, int? status, TimeSpan? retryAfter,
        NativeTransactionIdentity? identity, NativeMutationOutcome outcome) => new NativeResult<T>.Failure(
            new NativeFailure(code, status, retryAfter, identity, identity is null ? null : outcome));

    private static string ValidateKey(string key)
    {
        if (string.IsNullOrEmpty(key) || key.Length > 4096 || key.Any(character => !char.IsAscii(character) || char.IsWhiteSpace(character) || char.IsControl(character)))
            throw new InvalidDataException("Native scoped key must be nonblank and bounded.");
        return key;
    }

    private static TimeSpan? RetryAfter(HttpResponseMessage response)
    {
        try
        {
            var header = response.Headers.RetryAfter;
            if (header?.Delta is { } delta && delta >= TimeSpan.Zero) return delta;
            if (header?.Date is { } date) return date > DateTimeOffset.UtcNow ? date - DateTimeOffset.UtcNow : TimeSpan.Zero;
        }
        catch (FormatException) { }
        return null;
    }

    private static async Task<byte[]> ReadBodyAsync(HttpResponseMessage response, CancellationToken cancellationToken)
    {
        if (response.Content.Headers.ContentLength > NativeCodec.MaximumBodyBytes) throw new InvalidDataException("Native response exceeds the 4 MiB limit.");
        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        using var output = new MemoryStream();
        var buffer = new byte[81920];
        while (true)
        {
            var count = await stream.ReadAsync(buffer, cancellationToken).ConfigureAwait(false);
            if (count == 0) break;
            if (output.Length + count > NativeCodec.MaximumBodyBytes) throw new InvalidDataException("Native response exceeds the 4 MiB limit.");
            output.Write(buffer, 0, count);
        }
        return output.ToArray();
    }

    private void ThrowIfDisposed() => ObjectDisposedException.ThrowIf(Volatile.Read(ref disposed) != 0, this);

    /// <inheritdoc />
    public override string ToString() => "Native database client";

    /// <summary>Disposes an owned transport once; a caller-owned HttpClient remains usable.</summary>
    public void Dispose()
    {
        if (Interlocked.Exchange(ref disposed, 1) == 0 && ownsClient) client.Dispose();
    }
}

internal static class NativeTransport
{
    internal static HttpClient CreateClient(HttpMessageHandler? handler = null) => new(handler ?? new HttpClientHandler
    {
        AllowAutoRedirect = false,
        UseCookies = false,
        AutomaticDecompression = DecompressionMethods.None
    }, disposeHandler: true) { Timeout = Timeout.InfiniteTimeSpan };
}
