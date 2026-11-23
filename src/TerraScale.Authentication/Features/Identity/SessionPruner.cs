using ZLogger;
using TerraScale.Authentication.Features.AgentId;
using TerraScale.Authentication.Features.Agents;
using TerraScale.Authentication.Features.AttackProtection;
using TerraScale.Authentication.Features.Oidc;
using TerraScale.Authentication.Features.Platform;
using TerraScale.Authentication.Storage;
using TerraScale.DotNet.TerraBase;

namespace TerraScale.Authentication.Features.Identity;

public sealed class SessionPruner(AuthSchema schema, Sessions sessions, Passcodes passcodes, Challenges challenges, StepUpProofs proofs, AgentEnrollments enrollments, OidcTokenStore oidcTokens, OidcAuthorizationStore oidcAuthorizations, ILogger<SessionPruner> logger, AccountProtection protection, AgentAssertions agents, AgentIdTransactions agentSignIns) : BackgroundService
{
    internal const int BatchSize = 500;

    private static readonly TimeSpan Interval = TimeSpan.FromMinutes(10);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        await schema.Ready.WaitAsync(stoppingToken);
        using var timer = new PeriodicTimer(Interval);
        while (await timer.WaitForNextTickAsync(stoppingToken))
        {
            await this.PruneAsync("attack protection", protection.PruneAsync, stoppingToken);
            await this.PruneAsync("sessions", sessions.PruneAsync, stoppingToken);
            await this.PruneAsync("passcodes", passcodes.PruneAsync, stoppingToken);
            await this.PruneAsync("challenges", challenges.PruneAsync, stoppingToken);
            await this.PruneAsync("step-up proofs", proofs.PruneAsync, stoppingToken);
            await this.PruneAsync("agent enrollments", enrollments.PruneAsync, stoppingToken);
            await this.PruneAsync("OIDC tokens", oidcTokens.PruneAsync, stoppingToken);
            await this.PruneAsync("OIDC authorizations", oidcAuthorizations.PruneAsync, stoppingToken);
            await this.PruneAsync("agent assertions", agents.PruneAsync, stoppingToken);
            await this.PruneAsync("agent sign-in transactions", agentSignIns.PruneAsync, stoppingToken);
        }
    }

    private async Task PruneAsync(string store, Func<int, CancellationToken, Task<int>> prune, CancellationToken stoppingToken)
    {
        try
        {
            await prune(BatchSize, stoppingToken);
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
