using System.Collections.Concurrent;
using System.Net;
using TerraBase.Hosting;
using TerraBase.Features.Replicas;

namespace TerraBase.Features.Regions;

public sealed record HomeNode(string Region, Uri Address);

public interface IHomeLocator
{
    ValueTask<HomeNode?> LocateAsync(string project, string? database, CancellationToken cancellationToken);

    HomeNode? Cached(string project, string? database);

    ValueTask<HomeNode?> RefreshAsync(string project, string? database, CancellationToken cancellationToken);
}

internal sealed class HomeLocator(IRegionPeers peers, IHttpClientFactory http, InternalCredential credential, TimeProvider time, RegionalOwners owners) : IHomeLocator
{
    internal const string HttpClientName = "terrabase-peers";
    internal const string Path = "/v1/internal/home";
    private const int MaxEntries = 4096;
    internal static readonly TimeSpan ForcedRefreshInterval = TimeSpan.FromSeconds(1);
    private readonly Lock _refreshGate = new();
    private readonly Dictionary<(string Project, string? Database), DateTimeOffset> _refreshed = [];

    private static readonly TimeSpan Lifetime = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan AskTimeout = TimeSpan.FromSeconds(3);
    private readonly ConcurrentDictionary<(string Project, string? Database), (HomeNode Home, string? Owner, DateTimeOffset Until)> _found = new();

    public HomeNode? Cached(string project, string? database) =>
        _found.TryGetValue((project, database), out var cached) && time.GetUtcNow() < cached.Until ? cached.Home : null;

    public async ValueTask<HomeNode?> LocateAsync(string project, string? database, CancellationToken cancellationToken)
    {
        var key = (project, database);
        if (_found.TryGetValue(key, out var cached) && time.GetUtcNow() < cached.Until
            && (owners.Confirmed(cached.Home.Region) is not { } owner || owner.Node == cached.Owner))
        {
            return cached.Home;
        }

        var others = await peers.OtherRegionsAsync(cancellationToken).ConfigureAwait(false);
        if (others.Count == 0)
        {
            return null;
        }

        using var ask = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        ask.CancelAfter(AskTimeout);
        var asks = others.Select(peer => AskAsync(peer, project, database, ask.Token)).ToList();
        await foreach (var done in Task.WhenEach(asks).ConfigureAwait(false))
        {
            if (await done.ConfigureAwait(false) is { } home)
            {
                await ask.CancelAsync().ConfigureAwait(false);
                if (_found.Count >= MaxEntries)
                {
                    _found.Clear();
                }

                _found[key] = (home, owners.Confirmed(home.Region)?.Node, time.GetUtcNow() + Lifetime);
                return home;
            }
        }

        _found.TryRemove(key, out _);
        return null;
    }

    public ValueTask<HomeNode?> RefreshAsync(string project, string? database, CancellationToken cancellationToken)
    {
        var key = (project, database);
        lock (_refreshGate)
        {
            var now = time.GetUtcNow();
            if (_refreshed.TryGetValue(key, out var until) && now < until)
            {
                return ValueTask.FromResult<HomeNode?>(null);
            }

            foreach (var expired in _refreshed.Where(entry => entry.Value <= now).Select(entry => entry.Key).ToArray())
            {
                _refreshed.Remove(expired);
            }

            if (_refreshed.Count >= MaxEntries)
            {
                return ValueTask.FromResult<HomeNode?>(null);
            }

            _refreshed[key] = now + ForcedRefreshInterval;
            _found.TryRemove(key, out _);
        }

        return LocateAsync(project, database, cancellationToken);
    }

    private async Task<HomeNode?> AskAsync(RegionPeer peer, string project, string? database, CancellationToken cancellationToken)
    {
        var query = "?project=" + Uri.EscapeDataString(project) + (database is null ? "" : "&database=" + Uri.EscapeDataString(database));
        using var request = new HttpRequestMessage(HttpMethod.Get, new Uri(peer.Address, Path + query))
        {
            Version = HttpVersion.Version20,
            VersionPolicy = HttpVersionPolicy.RequestVersionExact,
        };
        credential.Sign(request.Headers);
        try
        {
            using var client = http.CreateClient(HttpClientName);
            using var response = await client.SendAsync(request, cancellationToken).ConfigureAwait(false);
            return response.StatusCode == HttpStatusCode.OK ? new HomeNode(peer.Region, peer.Address) : null;
        }
        catch (Exception error) when (error is HttpRequestException or OperationCanceledException)
        {
            return null;
        }
    }
}
