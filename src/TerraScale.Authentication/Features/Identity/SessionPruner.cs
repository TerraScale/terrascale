using ZLogger;
using TerraScale.Authentication.Features.Oidc;
using TerraScale.Authentication.Features.Platform;
using TerraScale.Authentication.Storage;
using TerraScale.DotNet.TerraBase;

namespace TerraScale.Authentication.Features.Identity;

/// <summary>
/// Deletes dead sessions and spent code records every ten minutes on every replica. Deletes are version-conditional,
/// so concurrent passes are safe; a failed pass is retried at the next tick, and dead records grant nothing while they wait.
/// </summary>
public sealed class SessionPruner(AuthSchema schema, Sessions sessions, Passcodes passcodes, Challenges challenges, StepUpProofs proofs, OidcTokenStore oidcTokens, OidcAuthorizationStore oidcAuthorizations, ILogger<SessionPruner> logger) : BackgroundService
{
    internal const int BatchSize = 500;

    private static readonly TimeSpan Interval = TimeSpan.FromMinutes(10);

    /// <inheritdoc />
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        await schema.Ready.WaitAsync(stoppingToken).ConfigureAwait(false);
        using var timer = new PeriodicTimer(Interval);
        while (await timer.WaitForNextTickAsync(stoppingToken).ConfigureAwait(false))
        {
            await this.PruneAsync("sessions", sessions.PruneAsync, stoppingToken).ConfigureAwait(false);
            await this.PruneAsync("passcodes", passcodes.PruneAsync, stoppingToken).ConfigureAwait(false);
            await this.PruneAsync("challenges", challenges.PruneAsync, stoppingToken).ConfigureAwait(false);
            await this.PruneAsync("step-up proofs", proofs.PruneAsync, stoppingToken).ConfigureAwait(false);
            await this.PruneAsync("OIDC tokens", oidcTokens.PruneAsync, stoppingToken).ConfigureAwait(false);
            await this.PruneAsync("OIDC authorizations", oidcAuthorizations.PruneAsync, stoppingToken).ConfigureAwait(false);
        }
    }

    private async Task PruneAsync(string store, Func<int, CancellationToken, Task<int>> prune, CancellationToken stoppingToken)
    {
        try
        {
            await prune(BatchSize, stoppingToken).ConfigureAwait(false);
        }
        catch (Exception error) when (!stoppingToken.IsCancellationRequested)
        {
            logger.ZLogWarning(error, $"Pruning {store} failed (TerraBase code {Code(error)}); retrying at the next interval");
        }
    }

    private static string Code(Exception error) => error switch
    {
        TerraBaseException { ErrorCode: { } code } => code,
        { InnerException: { } inner } => Code(inner),
        _ => "none",
    };
}
