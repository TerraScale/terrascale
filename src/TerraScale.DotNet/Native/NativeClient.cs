using System.Collections.Immutable;
using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;

namespace TerraScale.DotNet.Native;

/// <summary>Native v1 transport. Credentials and admission are resolved anew for every finite request.</summary>
/// <remarks>
/// At most 16 operations per client remain outstanding, including callbacks and cleanup that outlive a caller deadline.
/// Additional calls wait within their original deadline. Provider, handler and content tasks must represent all their work;
/// detached or async-void work is unsupported. Callback invocation and context-capturing continuations use owned workers.
/// Callbacks that opt out of context capture (for example, with ConfigureAwait(false) or Task.Run) must keep shared-pool
/// continuations nonblocking; starvation caused by such callback code is outside the finite-deadline guarantee.
/// Unexpected provider programming and lifecycle exceptions propagate. Invalid credentials and admissions return distinct local failure codes.
/// </remarks>
public sealed class NativeClient : IDisposable
{
    private readonly Guid owner = Guid.NewGuid();
    private readonly NativeTransport.Isolation isolation = new();
    private readonly HttpClient client;
    private readonly INativeAdmissionProvider admissionProvider;
    private readonly Func<CancellationToken, ValueTask<string>> keyProvider;
    private readonly TimeSpan timeout;
    private readonly TimeProvider deadlineClock;
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

    internal NativeClient(NativeScope scope, INativeAdmissionProvider admissionProvider,
        Func<CancellationToken, ValueTask<string>> keyProvider, TimeSpan timeout, HttpMessageHandler handler, TimeProvider deadlineClock)
        : this(scope, admissionProvider, keyProvider, timeout, NativeTransport.CreateClient(handler), ownsClient: true, deadlineClock)
    {
    }

    internal Task WaitForIdleAsync(CancellationToken cancellationToken) => isolation.WaitForIdleAsync(cancellationToken);

    private NativeClient(NativeScope scope, INativeAdmissionProvider admissionProvider,
        Func<CancellationToken, ValueTask<string>> keyProvider, TimeSpan timeout, HttpClient httpClient, bool ownsClient, TimeProvider? deadlineClock = null)
    {
        try
        {
            Scope = NativeCodec.ValidateScope(scope);
            this.admissionProvider = admissionProvider ?? throw new ArgumentNullException(nameof(admissionProvider));
            this.keyProvider = keyProvider ?? throw new ArgumentNullException(nameof(keyProvider));
            if (timeout <= TimeSpan.Zero || timeout > TimeSpan.FromMinutes(5)) throw new ArgumentOutOfRangeException(nameof(timeout));
            this.timeout = timeout;
            this.deadlineClock = deadlineClock ?? TimeProvider.System;
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

    internal async Task<NativeResult<T>> RequestAsync<T>(string path, HttpMethod method, string? body,
        Func<JsonElement, T> decode, IReadOnlyCollection<string> capabilities, CancellationToken cancellationToken,
        NativeTransactionIdentity? identity = null, bool mutation = false)
    {
        ThrowIfDisposed();
        using var deadline = new CancellationTokenSource(timeout, deadlineClock);
        using var budget = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, deadline.Token);
        var budgetToken = budget.Token;
        var dispatchGate = new object();
        var sent = false;
        int? status = null;
        TimeSpan? retryAfter = null;
        var phase = "credential";
        try
        {
            using var transport = await isolation.StartAsync(budgetToken).ConfigureAwait(false);
            budgetToken.ThrowIfCancellationRequested();
            var key = ValidateKey(await transport.InvokeAsync(() => keyProvider(budgetToken).AsTask(), budgetToken).ConfigureAwait(false));
            phase = "admission";
            var admission = await transport.InvokeAsync(
                () => admissionProvider.ResolveAsync(Scope, key, capabilities, budgetToken).AsTask(), budgetToken).ConfigureAwait(false);
            var response = await transport.InvokeAsync(async () =>
            {
                budgetToken.ThrowIfCancellationRequested();
                // The transport invocation owns the request until it completes,
                // even if a synchronous handler outlives the caller's wait.
                using var request = new HttpRequestMessage(method, new Uri(admission.Origin, path));
                request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
                request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", key);
                if (body is not null)
                {
                    request.Content = new ByteArrayContent(Encoding.UTF8.GetBytes(body));
                    request.Content.Headers.ContentType = new MediaTypeHeaderValue("application/json");
                }
                admission.AssertCurrent();
                lock (dispatchGate)
                {
                    budgetToken.ThrowIfCancellationRequested();
                    phase = "response";
                    sent = true;
                }
                try
                {
                    return await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, budgetToken).ConfigureAwait(false);
                }
                catch (TaskCanceledException exception) when (!budgetToken.IsCancellationRequested && exception.InnerException is TimeoutException)
                {
                    throw new NativeTransport.HttpTimeoutException();
                }
            }, budgetToken, NativeTransport.DisposeResource).ConfigureAwait(false);
            await using var responseLifetime = transport.Own(response, budget.Token);
            status = (int)response.StatusCode;
            retryAfter = RetryAfter(response);
            if (response.IsSuccessStatusCode && response.StatusCode != HttpStatusCode.OK)
                throw new InvalidDataException("Native protocol requires HTTP 200 success.");
            if (!string.Equals(response.Content.Headers.ContentType?.MediaType, "application/json", StringComparison.OrdinalIgnoreCase) ||
                response.Content.Headers.ContentEncoding.Count > 0)
                throw new InvalidDataException("Native response has unsupported media type or encoding.");
            var bytes = await ReadBodyAsync(response, transport, budget.Token).ConfigureAwait(false);
            var success = response.IsSuccessStatusCode;
            var result = await transport.InvokeAsync(() =>
            {
                using var document = NativeCodec.Parse(bytes);
                if (!success)
                {
                    var code = NativeCodec.DecodeError(document.RootElement);
                    var outcome = mutation
                        ? code == "precondition_failed" && status == 412 ? NativeMutationOutcome.Rejected
                            : code == "commit_uncertain" ? NativeMutationOutcome.Uncertain : NativeMutationOutcome.Unknown
                        : NativeMutationOutcome.Unknown;
                    if (code.Contains(key, StringComparison.Ordinal)) code = "invalid_response";
                    return Task.FromResult(Failure<T>(code, status, retryAfter, identity, outcome));
                }
                return Task.FromResult<NativeResult<T>>(new NativeResult<T>.Success(decode(document.RootElement)));
            }, budgetToken).ConfigureAwait(false);
            await responseLifetime.DisposeAsync().ConfigureAwait(false);
            budgetToken.ThrowIfCancellationRequested();
            return result;
        }
        catch (NativeAdmissionException exception)
        {
            return Failure<T>(exception.Code, exception.Status, null, identity, NativeMutationOutcome.Unknown);
        }
        catch (OperationCanceledException) when (budgetToken.IsCancellationRequested)
        {
            return Failure<T>(cancellationToken.IsCancellationRequested ? "cancelled" : "deadline_exceeded", status, retryAfter,
                identity, MutationOutcome());
        }
        catch (NativeTransport.HttpTimeoutException)
        {
            return Failure<T>("deadline_exceeded", status, retryAfter, identity, MutationOutcome());
        }
        catch (Exception exception) when (exception is InvalidDataException or JsonException or FormatException)
        {
            return Failure<T>(phase == "credential" ? "invalid_credential" : phase == "admission" ? "invalid_admission" : "invalid_response", status, retryAfter, identity,
                MutationOutcome());
        }
        catch (Exception exception) when (exception is HttpRequestException or IOException)
        {
            return Failure<T>("transport_unavailable", status, retryAfter, identity,
                MutationOutcome());
        }

        NativeMutationOutcome MutationOutcome()
        {
            // Pair cancellation classification with the worker's dispatch
            // transition; a queued invocation cannot send after a definite failure.
            lock (dispatchGate) return mutation && sent ? NativeMutationOutcome.Uncertain : NativeMutationOutcome.Unknown;
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
            if (header?.Date is { } date)
            {
                var now = DateTimeOffset.UtcNow;
                return date > now ? date - now : TimeSpan.Zero;
            }
        }
        catch (FormatException) { }
        return null;
    }

    private static Task<byte[]> ReadBodyAsync(HttpResponseMessage response, NativeTransport.Operation transport, CancellationToken cancellationToken) =>
        NativeTransport.ReadBodyAsync(response.Content, transport, NativeCodec.MaximumBodyBytes,
            () => new InvalidDataException("Native response exceeds the 4 MiB limit."), cancellationToken);

    private void ThrowIfDisposed() => ObjectDisposedException.ThrowIf(Volatile.Read(ref disposed) != 0, this);

    /// <inheritdoc />
    public override string ToString() => "Native database client";

    /// <summary>Disposes an owned transport once; a caller-owned HttpClient remains usable.</summary>
    public void Dispose()
    {
        if (Interlocked.Exchange(ref disposed, 1) == 0)
        {
            isolation.Dispose();
            if (ownsClient) client.Dispose();
        }
    }
}

internal static class NativeTransport
{
    internal const int MaximumOutstandingOperations = 16;

    internal sealed class HttpTimeoutException : TimeoutException;

    internal static Task<byte[]> ReadBodyAsync(HttpContent content, Operation operation, int limit,
        Func<Exception> overflow, CancellationToken cancellationToken)
    {
        return operation.InvokeAsync(async () =>
        {
            // The length getter can invoke custom content synchronously too.
            if (content.Headers.ContentLength > limit) throw overflow();
            // Copy directly into a bounded destination. ReadAsStreamAsync can
            // materialize an undeclared custom content body before any check.
            using var destination = new BoundedHttpBodyStream(limit, overflow, cancellationToken);
            await content.CopyToAsync(destination, cancellationToken).ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();
            return destination.ToArray();
        }, cancellationToken);
    }

    // Capacity belongs to the client/router, not process-wide state. A timed-out
    // caller releases its own reference, while callbacks and cleanup retain
    // theirs until actual completion. Queued callers use no blocking worker.
    internal sealed class Isolation : IDisposable
    {
        private readonly SemaphoreSlim capacity = new(MaximumOutstandingOperations, MaximumOutstandingOperations);
        private readonly NativeInvocationExecutor executor = new(MaximumOutstandingOperations);
        private readonly object gate = new();
        private int active;
        private bool disposed;

        private void EndOperation()
        {
            lock (gate) { if (--active == 0 && disposed) executor.Dispose(); }
        }
        public void Dispose()
        {
            lock (gate) { disposed = true; if (active == 0) executor.Dispose(); }
        }

        // Qualification can await every outstanding callback and cleanup,
        // rather than observing only the caller's cancellation return.
        internal async Task WaitForIdleAsync(CancellationToken cancellationToken)
        {
            var acquired = 0;
            try
            {
                for (; acquired < MaximumOutstandingOperations; acquired++)
                    await capacity.WaitAsync(cancellationToken).ConfigureAwait(false);
            }
            finally { if (acquired > 0) capacity.Release(acquired); }
        }

        internal async Task<Operation> StartAsync(CancellationToken cancellationToken)
        {
            await capacity.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                cancellationToken.ThrowIfCancellationRequested();
                lock (gate)
                {
                    ObjectDisposedException.ThrowIf(disposed, this);
                    active++;
                    return new Operation(capacity, executor, EndOperation);
                }
            }
            catch { capacity.Release(); throw; }
        }
    }

    internal sealed class Operation(SemaphoreSlim capacity, NativeInvocationExecutor executor, Action completed) : IDisposable
    {
        private NativeInvocationExecutor Executor => executor;
        private int references = 1;
        private int disposed;

        internal IAsyncDisposable Own(IDisposable resource, CancellationToken cancellationToken)
        {
            AddReference();
            return new ResourceLifetime(this, resource, cancellationToken);
        }

        internal async Task<T> InvokeAsync<T>(Func<Task<T>> invoke, CancellationToken cancellationToken, Action<T>? releaseLate = null)
        {
            cancellationToken.ThrowIfCancellationRequested();
            AddReference();
            Task<T>? operation = null;
            var transferred = false;
            try
            {
                // Isolation bounds invocation as well as the returned task.
                operation = executor.ExecuteAsync(() =>
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    return invoke();
                });
                var result = await operation.WaitAsync(cancellationToken).ConfigureAwait(false);
                cancellationToken.ThrowIfCancellationRequested();
                return result;
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested && operation is not null)
            {
                transferred = true;
                _ = CompleteLateAsync(operation, releaseLate);
                throw;
            }
            finally { if (!transferred) ReleaseReference(); }
        }

        private async Task CompleteLateAsync<T>(Task<T> pending, Action<T>? releaseLate)
        {
            try
            {
                var result = await pending.ConfigureAwait(false);
                if (releaseLate is not null)
                    await executor.ExecuteAsync(() =>
                    {
                        releaseLate(result);
                        return Task.FromResult(true);
                    }).ConfigureAwait(false);
            }
            catch (Exception) { /* Observe late faults without replacing the caller's outcome. */ }
            finally { ReleaseReference(); }
        }

        private void AddReference() => Interlocked.Increment(ref references);
        private void ReleaseReference()
        {
            if (Interlocked.Decrement(ref references) == 0)
            {
                capacity.Release();
                completed();
            }
        }

        public void Dispose()
        {
            if (Interlocked.Exchange(ref disposed, 1) == 0) ReleaseReference();
        }

        private sealed class ResourceLifetime(Operation operation, IDisposable resource, CancellationToken cancellationToken) : IAsyncDisposable
        {
            private IDisposable? owned = resource;

            public async ValueTask DisposeAsync()
            {
                var original = Interlocked.Exchange(ref owned, null);
                if (original is null) return;
                var cleanup = operation.Executor.ExecuteAsync(() =>
                {
                    try { DisposeResource(original); }
                    finally { operation.ReleaseReference(); }
                    return Task.FromResult(true);
                });
                try { await cleanup.WaitAsync(cancellationToken).ConfigureAwait(false); }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                {
                    // Cleanup retains operation capacity until eventual release.
                }
            }
        }
    }

    internal static void DisposeResource(IDisposable resource)
    {
        try { resource.Dispose(); }
        catch (Exception) { /* Cleanup cannot replace the protocol outcome. */ }
    }

    internal static HttpClient CreateClient(HttpMessageHandler? handler = null) => new(handler ?? new HttpClientHandler
    {
        AllowAutoRedirect = false,
        UseCookies = false,
        AutomaticDecompression = DecompressionMethods.None
    }, disposeHandler: true)
    { Timeout = Timeout.InfiniteTimeSpan };
}
