using System.Globalization;
using TerraBase.Engine.Revisions;
using TerraBase.Engine;
using TerraBase.Features.Replicas;
using TerraBase.Hosting;
using TerraBase.Tenancy;

namespace TerraBase.Features.Replicas;

internal sealed record VerificationRound(ReplicaVerdict Verdict, ChecksumCursor? Next, string? Where);

internal sealed class ReplicaVerifier(IDatabaseReplicas replicas, HttpClient http)
{
    private const int Attempts = 3;

    public async Task<VerificationRound> RoundAsync(TenantContext database, Uri source, ChecksumCursor? cursor, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(source);
        for (var attempt = 0; attempt < Attempts; attempt++)
        {
            if (attempt > 0 || replicas.HeldRevision(database) is null)
            {
                _ = await replicas.PullAsync(database, fromHome: true, cancellationToken).ConfigureAwait(false);
            }

            var round = await ObserveAsync(database, source, cursor, cancellationToken).ConfigureAwait(false);
            if (round.Verdict is not Moved)
            {
                return round;
            }
        }

        return new VerificationRound(new Moved(), cursor, null);
    }

    internal async Task<VerificationRound> ObserveAsync(TenantContext database, Uri source, ChecksumCursor? cursor, CancellationToken cancellationToken)
    {
        if (replicas.HeldRevision(database) is not { } held)
        {
            return new VerificationRound(new Moved(), cursor, null);
        }

        var page = await http.GetFromJsonAsync(Address(source, held, cursor), ReplicasJsonContext.Default.ReplicaChecksumPage, cancellationToken).ConfigureAwait(false)
            ?? throw new InvalidDataException("the home answered no checksums");
        if (page.Moved)
        {
            return new VerificationRound(new Moved(), cursor, null);
        }

        var comparison = await replicas.CompareAsync(database, held, page.Ranges, cancellationToken).ConfigureAwait(false);
        return comparison switch
        {
            { Verdict: Equal } => new VerificationRound(new Equal(), page.Next, null),
            { Verdict: Diverged, Where: var where } => new VerificationRound(new Diverged(), null, where),
            null or { Verdict: Moved } => new VerificationRound(new Moved(), cursor, null),
        };
    }

    public async Task HealAsync(TenantContext database, Uri source, CancellationToken cancellationToken)
    {
        _ = await replicas.DeleteAsync(database, cancellationToken).ConfigureAwait(false);
        await replicas.OpenAsync(database, source, http, cancellationToken).ConfigureAwait(false);
    }

    private static Uri Address(Uri source, DatabaseRevision revision, ChecksumCursor? cursor) => new(
        source + "/checksums?revision=" + Uri.EscapeDataString(revision.ToString())
        + (cursor is null ? "" : "&table=" + Uri.EscapeDataString(cursor.Table) + "&after=" + cursor.After.ToString(CultureInfo.InvariantCulture)));
}
