using Microsoft.EntityFrameworkCore;
using TerraScale.Authentication.Configuration;

namespace TerraScale.Authentication.Storage;

public sealed record StorageHealthResult(bool Healthy, string? Region);

public interface IStorageHealth
{
    Task<StorageHealthResult> CheckAsync(CancellationToken cancellationToken);
}

public sealed record StorageProbe(string Id, DateTimeOffset CheckedAt);

public sealed partial class TerraBaseStorageHealth(
    IDbContextFactory<AuthDbContext> contexts, AuthData data, TimeProvider clock, AuthOptions options, IConfiguration configuration, ILogger<TerraBaseStorageHealth> logger)
{
    private static readonly AsyncLocal<RegionCheck?> Scope = new();

    private readonly string servingRegion = configuration["TS_REGION"]
        ?? (options.Development ? "gru" : throw new AuthConfigurationException("TS_REGION is required outside Development"));

    private readonly string probeId = AuthIds.New();

    public static HttpMessageHandler Wrap(HttpMessageHandler inner) => new RegionHandler { InnerHandler = inner };

    public async Task<StorageHealthResult> CheckAsync(CancellationToken cancellationToken)
    {
        var region = options.Development || this.servingRegion == AuthOptions.AuthHome ? null : new RegionCheck(logger);
        Scope.Value = region;
        await using (var db = await contexts.CreateDbContextAsync(cancellationToken).ConfigureAwait(false))
        {
            if ((await db.Database.GetPendingMigrationsAsync(cancellationToken).ConfigureAwait(false)).Any())
            {
                return new(false, null);
            }
        }

        if (await this.ReadAsync(cancellationToken).ConfigureAwait(false) is { } previous)
        {
            _ = await data.SaveAsync(Change.Delete(previous), cancellationToken).ConfigureAwait(false);
        }

        var probe = new StorageProbe(this.probeId, clock.GetUtcNow());
        var created = Change.Insert(probe);
        var healthy = await data.SaveAsync(created, cancellationToken).ConfigureAwait(false)
            && await this.ReadAsync(cancellationToken).ConfigureAwait(false) is { } read && read.Version == created.Version
            && await data.SaveAsync(Change.Delete(read), cancellationToken).ConfigureAwait(false);
        return new(healthy && (region is null || region.Observed && region.MatchesHome), region?.Region);
    }

    private Task<Versioned<StorageProbe>?> ReadAsync(CancellationToken cancellationToken) =>
        data.ReadAsync((db, token) => db.StorageProbes.Where(probe => probe.Id == this.probeId).WithVersion().SingleOrDefaultAsync(token), cancellationToken);

    [LoggerMessage(Level = LogLevel.Error, Message = "ts-auth storage answered from {Region}, expected {Expected}")]
    private static partial void WrongRegion(ILogger logger, string region, string expected);

    private sealed class RegionCheck(ILogger logger)
    {
        public bool Observed { get; private set; }

        public bool MatchesHome { get; private set; } = true;

        public string? Region { get; private set; }

        public void Observe(HttpResponseMessage response)
        {
            this.Observed = true;
            var region = response.Headers.TryGetValues("X-TerraBase-Region", out var values)
                ? string.Join(", ", values) : "<missing>";
            if (this.MatchesHome)
            {
                this.Region = region;
            }

            if (!string.Equals(region, AuthOptions.AuthHome, StringComparison.Ordinal))
            {
                this.MatchesHome = false;
                WrongRegion(logger, region, AuthOptions.AuthHome);
            }
        }
    }

    private sealed class RegionHandler : DelegatingHandler
    {
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var response = await base.SendAsync(request, cancellationToken).ConfigureAwait(false);
            Scope.Value?.Observe(response);
            return response;
        }
    }
}
