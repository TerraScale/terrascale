using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using TerraScale.Auth.Configuration;
using TerraScale.DotNet.TerraBase;

namespace TerraScale.Auth.Hosting;

/// <summary>
/// Sends storage to the first ready member of <see cref="AuthOptions.TerraScaleApiEndpoints"/>, over TLS that trusts only
/// <see cref="AuthOptions.TerraScaleApiCa"/>. With one member, storage is pinned to it and fails closed while it is down.
/// Several members need <see cref="AuthOptions.TerraScaleApiFailover"/>, the documented exception to the single storage
/// authority: a member that takes over may not have replicated the latest writes yet. Every replica has the same order, so
/// all of them use the same member while it is ready and read their own writes. Each member's unauthenticated <c>/readyz</c> is probed every two seconds: a member
/// is ready while it answers below 500 (a release without the client-listener probe answers 404), and down while it does
/// not answer or answers 503 (shutting down, or a catalog follower that has not caught up). A request that cannot connect
/// marks its member down at once, as does a failed name lookup or TLS handshake. A failed request is never resent: TerraBase documents no retryable
/// failure. When no member is ready, storage tries the first one.
/// </summary>
public sealed partial class TerraBaseMembers : BackgroundService, ITerraBaseEndpointResolver
{
    private static readonly TimeSpan ProbeInterval = TimeSpan.FromSeconds(2);
    private static readonly TimeSpan ProbeTimeout = TimeSpan.FromMilliseconds(1500);
    private static readonly TimeSpan ConnectTimeout = TimeSpan.FromSeconds(2);

    private readonly AuthOptions options;
    private readonly Uri[] members;
    private readonly string[] authorities;
    // 1 while ready. Optimistic until probed, so storage starts on the first member.
    private readonly int[] ready;
    // Request failures per member. A probe that began before the latest one cannot make the member ready again.
    private readonly long[] failures;
    private readonly Lock gate = new();
    private readonly HttpClient probes;
    private readonly ILogger<TerraBaseMembers> logger;

    /// <summary>Creates the member set for configured endpoints.</summary>
    public TerraBaseMembers(AuthOptions options, ILogger<TerraBaseMembers> logger)
    {
        ArgumentNullException.ThrowIfNull(options);
        this.options = options;
        this.members = [.. options.TerraScaleApiEndpoints];
        this.authorities = [.. this.members.Select(member => member.GetLeftPart(UriPartial.Authority))];
        this.ready = [.. this.members.Select(_ => 1)];
        this.failures = new long[this.members.Length];
        this.probes = new HttpClient(CreateTransport(options));
        this.logger = logger;
    }

    /// <summary>The storage client's handler: trusts only the configured roots and marks a member it cannot reach down.</summary>
    public HttpMessageHandler CreateHandler() => new Tracker(this, CreateTransport(this.options));

    /// <inheritdoc />
    public ValueTask<Uri> ResolveAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        for (var index = 0; index < this.members.Length; index++)
        {
            if (Volatile.Read(ref this.ready[index]) == 1)
            {
                return ValueTask.FromResult(this.members[index]);
            }
        }

        return ValueTask.FromResult(this.members[0]);
    }

    /// <inheritdoc />
    public override void Dispose()
    {
        this.probes.Dispose();
        base.Dispose();
    }

    /// <inheritdoc />
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(ProbeInterval);
        do
        {
            await Task.WhenAll(this.members.Select((_, index) => this.ProbeAsync(index, stoppingToken)));
        }
        while (await timer.WaitForNextTickAsync(stoppingToken));
    }

    /// <summary>How many requests to member <paramref name="index"/> have failed; a probe reads it before it starts.</summary>
    internal long Failures(int index) => Volatile.Read(ref this.failures[index]);

    /// <summary>Applies a probe result, unless the probe began before a request failure that is newer than it.</summary>
    internal void ApplyProbe(int index, bool isReady, long failuresAtStart)
    {
        lock (this.gate)
        {
            if (!isReady || this.failures[index] == failuresAtStart)
            {
                this.Set(index, isReady);
            }
        }
    }

    private async Task ProbeAsync(int index, CancellationToken stoppingToken)
    {
        var failuresAtStart = this.Failures(index);
        bool isReady;
        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken);
            timeout.CancelAfter(ProbeTimeout);
            using var response = await this.probes.GetAsync(new Uri(this.members[index], "readyz"), timeout.Token);
            isReady = (int)response.StatusCode < 500;
        }
        catch (Exception error) when (error is HttpRequestException or OperationCanceledException && !stoppingToken.IsCancellationRequested)
        {
            isReady = false;
        }

        this.ApplyProbe(index, isReady, failuresAtStart);
    }

    private void MarkDown(Uri request)
    {
        var index = Array.IndexOf(this.authorities, request.GetLeftPart(UriPartial.Authority));
        if (index >= 0)
        {
            lock (this.gate)
            {
                this.failures[index]++;
                this.Set(index, false);
            }
        }
    }

    private void Set(int index, bool isReady)
    {
        if (Interlocked.Exchange(ref this.ready[index], isReady ? 1 : 0) != (isReady ? 1 : 0))
        {
            if (isReady)
            {
                LogReady(this.logger, this.authorities[index]);
            }
            else
            {
                LogUnavailable(this.logger, this.authorities[index]);
            }
        }
    }

    private static SocketsHttpHandler CreateTransport(AuthOptions options)
    {
        var handler = TerraBaseHttp.CreateHandler();
        // A member whose host is gone fails fast instead of holding the request until its deadline.
        handler.ConnectTimeout = ConnectTimeout;
        var policy = new X509ChainPolicy
        {
            TrustMode = X509ChainTrustMode.CustomRootTrust,
            // Member certificates name no revocation endpoint; the roots are pinned by the signed configuration.
            RevocationMode = X509RevocationMode.NoCheck,
            DisableCertificateDownloads = true,
        };
        policy.CustomTrustStore.AddRange(options.TerraScaleApiCa.ToArray());
        policy.ApplicationPolicy.Add(new Oid("1.3.6.1.5.5.7.3.1"));
        handler.SslOptions.CertificateChainPolicy = policy;
        return handler;
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "TerraBase member {Member} is ready")]
    private static partial void LogReady(ILogger logger, string member);

    [LoggerMessage(Level = LogLevel.Warning, Message = "TerraBase member {Member} is unavailable; storage uses the next ready member")]
    private static partial void LogUnavailable(ILogger logger, string member);

    private sealed class Tracker(TerraBaseMembers members, HttpMessageHandler inner) : DelegatingHandler(inner)
    {
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            try
            {
                return await base.SendAsync(request, cancellationToken);
            }
            catch (Exception error) when (error is HttpRequestException { HttpRequestError: HttpRequestError.NameResolutionError or HttpRequestError.ConnectionError or HttpRequestError.SecureConnectionError }
                // ConnectTimeout surfaces as a cancellation wrapping a TimeoutException while the request's own token is live.
                || (error is OperationCanceledException { InnerException: TimeoutException } && !cancellationToken.IsCancellationRequested))
            {
                members.MarkDown(request.RequestUri!);
                throw;
            }
        }
    }
}
