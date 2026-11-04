using System.Globalization;
using TerraBase.Engine;
using TerraBase.Engine.Revisions;
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
                _ = await replicas.PullAsync(database, cancellationToken).ConfigureAwait(false);
            }

            if (replicas.HeldRevision(database) is not { } held)
            {
                continue;
            }

            var page = await http.GetFromJsonAsync(Address(source, held, cursor), TerraBaseJsonContext.Default.ReplicaChecksumPage, cancellationToken).ConfigureAwait(false)
                ?? throw new InvalidDataException("the home answered no checksums");
            if (page.Moved)
            {
                continue;
            }

            var comparison = await replicas.CompareAsync(database, held, page.Ranges, cancellationToken).ConfigureAwait(false);
            switch (comparison?.Verdict)
            {
                case ReplicaVerdict.Equal:
                    return new VerificationRound(ReplicaVerdict.Equal, page.Next, null);
                case ReplicaVerdict.Diverged:
                    return new VerificationRound(ReplicaVerdict.Diverged, null, comparison.Where);
            }
        }

        return new VerificationRound(ReplicaVerdict.Moved, cursor, null);
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
