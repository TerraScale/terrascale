using System.Text;
using TerraBase.Replication;
using TerraBase.Transport;

namespace TerraBase.Api.Tests;

public sealed class TransportTests
{
    [Test]
    public async Task BoundedQueueAppliesBackpressureAndDrainsInOrder()
    {
        await using var queue = new BoundedTransportQueue<int>(1);
        await Assert.That(queue.TryEnqueue(1)).IsEqualTo(TransportEnqueueResult.Accepted);
        await Assert.That(queue.TryEnqueue(2)).IsEqualTo(TransportEnqueueResult.Full);
        await Assert.That(queue.Count).IsEqualTo(1);
        await Assert.That(queue.TryDequeue(out var value)).IsTrue();
        await Assert.That(value).IsEqualTo(1);
        await Assert.That(queue.TryEnqueue(2)).IsEqualTo(TransportEnqueueResult.Accepted);
        queue.Complete();
        await Assert.That(queue.TryEnqueue(3)).IsEqualTo(TransportEnqueueResult.Closed);
    }

    [Test]
    public async Task RetryOnlyRepeatsRetrySafeTransientCalls()
    {
        var policy = new InterNodeRetryPolicy(maxAttempts: 3, initialDelay: TimeSpan.Zero, maxDelay: TimeSpan.Zero);
        var attempts = 0;
        var result = await InterNodeRetry.ExecuteAsync(
            InterNodeRequestContext.Read("Replication", "Ping"),
            policy,
            _ =>
            {
                attempts++;
                return attempts < 3
                    ? ValueTask.FromException<int>(new InterNodeTransportException(InterNodeTransportErrorCode.Unavailable, "temporary", true))
                    : ValueTask.FromResult(7);
            });
        await Assert.That(result).IsEqualTo(7);
        await Assert.That(attempts).IsEqualTo(3);

        attempts = 0;
        await Assert.ThrowsAsync<InterNodeTransportException>(() => InterNodeRetry.ExecuteAsync(
            new InterNodeRequestContext("Replication", "Mutate"),
            policy,
            _ =>
            {
                attempts++;
                return ValueTask.FromException<int>(new InterNodeTransportException(InterNodeTransportErrorCode.Unavailable, "temporary", true));
            }));
        await Assert.That(attempts).IsEqualTo(1);
    }

    [Test]
    public async Task IdempotencyCoalescesDuplicatesAndRejectsDigestReuse()
    {
        var calls = 0;
        var inner = new DelegateTransport((_, _, _) =>
        {
            calls++;
            return ValueTask.FromResult(new InterNodeResponse<string>($"response-{calls}"));
        });
        var transport = new IdempotentInterNodeTransport(inner, new InMemoryIdempotencyStore());
        var context = InterNodeRequestContext.Mutation("Replication", "Apply", "request-1", "digest-a");

        var first = await transport.InvokeAsync<string, string>(context, "payload");
        var second = await transport.InvokeAsync<string, string>(context, "payload");
        await Assert.That(first.Value).IsEqualTo("response-1");
        await Assert.That(second.Value).IsEqualTo("response-1");
        await Assert.That(second.Replayed).IsTrue();
        await Assert.That(calls).IsEqualTo(1);

        var conflict = InterNodeRequestContext.Mutation("Replication", "Apply", "request-1", "digest-b");
        await Assert.ThrowsAsync<IdempotencyConflictException>(() => transport.InvokeAsync<string, string>(conflict, "other"));
    }

    [Test]
    public async Task ReplicationChunksResumeFromAnAcknowledgedSequence()
    {
        var key = new DocumentKey(new TenantId("tenant"), new CollectionId("items"), "one");
        var records = Enumerable.Range(1, 3)
            .Select(sequence => ReplicationRecord.FromCanonicalBytes(
                key,
                new ReplicationDot("region-a", (ulong)sequence),
                new Dictionary<string, ulong>(),
                sequence,
                false,
                Encoding.UTF8.GetBytes($"{{\"n\":{sequence}}}")))
            .ToArray();
        var request = new ReplicationStreamRequest("stream-1", maxRecords: 1, maxChunkBytes: 1024);
        var store = new ResumableReplicationStore(maximumRetainedChunks: 4);
        store.CreateStream(request, records);

        var firstPass = new List<ReplicationStreamChunk>();
        await foreach (var chunk in store.StreamReplicationAsync(request))
        {
            firstPass.Add(chunk);
            if (chunk.Sequence == 0) break;
        }

        var resumedRequest = new ReplicationStreamRequest("stream-1", maxRecords: 1, maxChunkBytes: 1024, resumeAfterSequence: 0);
        var resumed = new List<ReplicationStreamChunk>();
        await foreach (var chunk in store.StreamReplicationAsync(resumedRequest)) resumed.Add(chunk);
        await Assert.That(firstPass).HasCount(1);
        await Assert.That(resumed).HasCount(3);
        await Assert.That(resumed[0].Sequence).IsEqualTo(1);
        await Assert.That(resumed[^1].IsFinal).IsTrue();
        await Assert.That(resumed.All(chunk => chunk.Checksum.Length == 64)).IsTrue();
    }

    private sealed class DelegateTransport(
        Func<string, object?, CancellationToken, ValueTask<InterNodeResponse<object?>>> callback) : IInterNodeTransport
    {
        public async ValueTask<InterNodeResponse<TResponse>> InvokeAsync<TRequest, TResponse>(
            InterNodeRequestContext context,
            TRequest request,
            CancellationToken cancellationToken = default)
        {
            var response = await callback(context.Method, request, cancellationToken);
            return new InterNodeResponse<TResponse>((TResponse)response.Value!, response.Replayed, response.CommitId);
        }

        public async IAsyncEnumerable<InterNodeStreamItem<TResponse>> StreamAsync<TRequest, TResponse>(
            InterNodeRequestContext context,
            TRequest request,
            [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            await Task.CompletedTask;
            yield break;
        }
    }
}

