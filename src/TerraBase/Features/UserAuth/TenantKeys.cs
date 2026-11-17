using System.Net;
using System.Text.Json;
using Microsoft.IdentityModel.Tokens;
using TerraBase.Configuration;
using TerraBase.Errors;
using TerraBase.Features.Management;
using TerraBase.Primitives;

namespace TerraBase.Features.UserAuth;

internal readonly union TenantKeysResult(IReadOnlyList<SecurityKey>, NotFound, Unavailable);

internal sealed class TenantKeys(TerraBaseOptions options, IHttpClientFactory http, IProjectCatalog catalog, TimeProvider time, IHostApplicationLifetime lifetime)
{
    private const int Capacity = 4096;

    private static readonly NotFound UnknownProject = new("the token's project is unknown", "unknown_project");

    private readonly Lock _gate = new();
    private readonly Dictionary<string, Loaded> _loaded = new(StringComparer.Ordinal);
    private readonly Dictionary<string, Task<TenantKeysResult>> _fetching = new(StringComparer.Ordinal);

    private sealed record Loaded(IReadOnlyList<SecurityKey> Keys, long FetchedAt);

    public static string Issuer(TerraBaseOptions options, string project) =>
        new Uri(options.AuthIssuer ?? throw new InvalidOperationException("auth-issuer is not configured")).GetLeftPart(UriPartial.Authority) + "/" + project;

    public static string Audience(string project) => TerraBaseOptions.PlatformAuthAudience + "/v1/projects/" + project;

    public static string? ProjectOf(TerraBaseOptions options, string? issuer)
    {
        if (options.AuthIssuer is null || issuer is null)
        {
            return null;
        }

        var origin = new Uri(options.AuthIssuer).GetLeftPart(UriPartial.Authority) + "/";
        return issuer.StartsWith(origin, StringComparison.Ordinal) && issuer[origin.Length..] is var project && EntityIds.IsValid(project)
            ? project
            : null;
    }

    public async ValueTask<TenantKeysResult> KeysAsync(string project, string subject, CancellationToken cancellationToken)
    {
        lock (_gate)
        {
            if (_loaded.TryGetValue(project, out var loaded) && time.GetElapsedTime(loaded.FetchedAt) < TsAuthKeys.RefreshInterval)
            {
                return new TenantKeysResult(loaded.Keys);
            }
        }

        return await FetchAsync(project, subject, cancellationToken).ConfigureAwait(false);
    }

    public async ValueTask<TenantKeysResult> RefreshAsync(string project, string subject, CancellationToken cancellationToken)
    {
        lock (_gate)
        {
            if (_loaded.TryGetValue(project, out var loaded) && time.GetElapsedTime(loaded.FetchedAt) < TsAuthKeys.UnknownKeyInterval)
            {
                return new TenantKeysResult(loaded.Keys);
            }
        }

        return await FetchAsync(project, subject, cancellationToken).ConfigureAwait(false);
    }

    private async ValueTask<TenantKeysResult> FetchAsync(string project, string subject, CancellationToken cancellationToken)
    {
        switch (await catalog.ResolveProjectAsync(project, subject, createIn: null, cancellationToken).ConfigureAwait(false))
        {
            case NotFound:
                Drop(project);
                return UnknownProject;
            case Unavailable unavailable:
                return unavailable;
            case string:
                break;
        }

        Task<TenantKeysResult> fetch;
        lock (_gate)
        {
            if (!_fetching.TryGetValue(project, out fetch!))
            {
                fetch = DownloadAsync(project);
                _fetching[project] = fetch;
            }
        }

        return await fetch.WaitAsync(cancellationToken).ConfigureAwait(false);
    }

    private async Task<TenantKeysResult> DownloadAsync(string project)
    {
        await Task.Yield();
        try
        {
            using var timeout = new CancellationTokenSource(TsAuthKeys.FetchTimeout, time);
            using var deadline = CancellationTokenSource.CreateLinkedTokenSource(timeout.Token, lifetime.ApplicationStopping);
            using var response = await http.CreateClient(TsAuthKeys.HttpClientName)
                .GetAsync(new Uri(Issuer(options, project) + "/.well-known/jwks.json"), deadline.Token).ConfigureAwait(false);
            if (response.StatusCode == HttpStatusCode.NotFound)
            {
                Drop(project);
                return UnknownProject;
            }

            response.EnsureSuccessStatusCode();
            var keys = TsAuthKeys.SigningKeys(TsAuthKeys.ReadKeySet(await response.Content.ReadAsStringAsync(deadline.Token).ConfigureAwait(false)));
            lock (_gate)
            {
                if (_loaded.Count >= Capacity && !_loaded.ContainsKey(project))
                {
                    _loaded.Clear();
                }

                _loaded[project] = new Loaded(keys, time.GetTimestamp());
            }

            return keys;
        }
        catch (Exception error) when (error is HttpRequestException or OperationCanceledException or InvalidDataException or JsonException or ArgumentException)
        {
            lock (_gate)
            {
                return _loaded.TryGetValue(project, out var loaded) && time.GetElapsedTime(loaded.FetchedAt) < TsAuthKeys.MaxKeyAge
                    ? new TenantKeysResult(loaded.Keys)
                    : new Unavailable(TokenKeysPending.Message);
            }
        }
        finally
        {
            lock (_gate)
            {
                _fetching.Remove(project);
            }
        }
    }

    private void Drop(string project)
    {
        lock (_gate)
        {
            _loaded.Remove(project);
        }
    }
}
