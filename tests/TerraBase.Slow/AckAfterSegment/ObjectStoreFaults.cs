using System.Net;
using System.Text;

namespace TerraBase.Slow.AckAfterSegment;

/// <summary>
/// Faults on object PUTs between the real S3 client and the fake bucket: added latency, an outage answered 503 SlowDown
/// without storing, and lost responses, where the bucket stores the object and the client sees the connection drop.
/// Shared by every handler the client factory builds (<see cref="Handler"/>).
/// </summary>
internal sealed class ObjectStoreFaults(int seed)
{
    private readonly Random _random = new(seed);
    private long _puts;
    private long _lostResponses;
    private long _refused;
    private volatile bool _outage;

    /// <summary>Added to every PUT before it reaches the bucket: the request's round trip.</summary>
    public TimeSpan PutLatency { get; set; }

    /// <summary>Share of stored PUTs whose response is lost.</summary>
    public double LostResponseChance { get; set; }

    /// <summary>While set, every PUT is refused with 503 SlowDown and nothing is stored.</summary>
    public bool Outage
    {
        get => _outage;
        set => _outage = value;
    }

    /// <summary>PUT requests that reached this handler, client retries included.</summary>
    public long Puts => Interlocked.Read(ref _puts);

    /// <summary>PUTs the bucket stored whose response the client never got.</summary>
    public long LostResponses => Interlocked.Read(ref _lostResponses);

    /// <summary>PUTs refused during an outage.</summary>
    public long Refused => Interlocked.Read(ref _refused);

    private void CountPut() => Interlocked.Increment(ref _puts);

    private void CountLost() => Interlocked.Increment(ref _lostResponses);

    private void CountRefused() => Interlocked.Increment(ref _refused);

    private bool Lose()
    {
        lock (_random)
        {
            return _random.NextDouble() < LostResponseChance;
        }
    }

    /// <summary>One handler of the client's chain; the client factory builds a new one per chain.</summary>
    public sealed class Handler(ObjectStoreFaults faults) : DelegatingHandler
    {
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            if (request.Method != HttpMethod.Put)
            {
                return await base.SendAsync(request, cancellationToken).ConfigureAwait(false);
            }

            faults.CountPut();
            if (faults.PutLatency > TimeSpan.Zero)
            {
                await Task.Delay(faults.PutLatency, cancellationToken).ConfigureAwait(false);
            }

            if (faults.Outage)
            {
                faults.CountRefused();
                return new HttpResponseMessage(HttpStatusCode.ServiceUnavailable)
                {
                    Content = new StringContent("<Error><Code>SlowDown</Code><Message>Please reduce your request rate.</Message></Error>", Encoding.UTF8, "application/xml"),
                };
            }

            var response = await base.SendAsync(request, cancellationToken).ConfigureAwait(false);
            if (response.IsSuccessStatusCode && faults.Lose())
            {
                // Stored, then the connection drops before the client reads the answer.
                response.Dispose();
                faults.CountLost();
                throw new HttpRequestException("the connection closed before the response arrived", new IOException("Connection reset by peer"));
            }

            return response;
        }
    }
}
