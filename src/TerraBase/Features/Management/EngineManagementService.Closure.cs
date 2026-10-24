using TerraBase.Engine;
using TerraBase.Errors;
using TerraBase.Features.ApiKeys;
using TerraBase.Features.Management.Backups;
using TerraBase.Features.Replication;
using TerraBase.Hosting;
using TerraBase.Primitives;
using ZLogger;

namespace TerraBase.Features.Management;

// docs/terrabase/project-closure.md: a permanent per-project fence, accepted only once every catalog holder confirmed the
// project empty under it.
internal sealed partial class EngineManagementService
{
    private string? _closuresAfter;

    public async ValueTask<CloseProjectResult> CloseProjectAsync(string projectId, string closureId, string authorityOrganizationId, CancellationToken cancellationToken) =>
        await WriteAsync<CloseProjectResult>(async transaction =>
        {
            if (await GetAsync(transaction, ProjectClosure.Collection, projectId, TerraBaseJsonContext.Default.ProjectClosureRecord, cancellationToken).ConfigureAwait(false) is { } existing)
            {
                if (existing.FormatVersion != 1 || existing.ProjectId != projectId || existing.ClosureId != closureId || existing.AuthorityOrganizationId != authorityOrganizationId)
                {
                    return ProjectClosure.ClosureConflict;
                }

                await RefreshClosureReceiptAsync(transaction, existing, cancellationToken).ConfigureAwait(false);
                return await ClosureStatusAsync(transaction, existing, cancellationToken).ConfigureAwait(false);
            }

            // TerraBase's own mapping decides; the request's organization is only checked against a binding that names one.
            // An enrolled binding names none (TerraBase never learns it); a never-enrolled project has no mapping at all.
            if (await GetAsync(transaction, ProjectOrganizations, projectId, Id, cancellationToken).ConfigureAwait(false) is { } namespaceId
                && (!await IsProjectBoundAsync(transaction, projectId, namespaceId, cancellationToken).ConfigureAwait(false)
                    || await GetAsync(transaction, Bindings(projectId), "ts_auth", TerraBaseJsonContext.Default.ProjectCatalogBinding, cancellationToken).ConfigureAwait(false)
                        is { AuthorityOrganizationId: { } authority } && authority != authorityOrganizationId))
            {
                return ProjectClosure.BindingConflict;
            }

            if (!await IsProjectEmptyAsync(transaction, projectId, cancellationToken).ConfigureAwait(false))
            {
                return ManagementErrors.NotEmpty;
            }

            var record = new ProjectClosureRecord(projectId, closureId, authorityOrganizationId, time.GetUtcNow(),
                [.. (await ClosureHoldersAsync(transaction, cancellationToken).ConfigureAwait(false)).Order(StringComparer.Ordinal)]);
            await PutAsync(transaction, ProjectClosure.Collection, projectId, record, TerraBaseJsonContext.Default.ProjectClosureRecord, cancellationToken).ConfigureAwait(false);
            await RefreshClosureReceiptAsync(transaction, record, cancellationToken).ConfigureAwait(false);
            logger.ZLogInformation($"Project {projectId:@Project} is fenced for closure {closureId:@Closure}");
            return await ClosureStatusAsync(transaction, record, cancellationToken).ConfigureAwait(false);
        }, static error => error, cancellationToken).ConfigureAwait(false);

    // Read by every registration and enrollment in its committing transaction.
    private static ValueTask<bool> IsProjectClosedAsync(IStorageTransaction transaction, string projectId, CancellationToken cancellationToken) =>
        ProjectClosure.IsClosedAsync(transaction, projectId, cancellationToken);

    // The same contents DeleteProjectAsync refuses to delete.
    private static async ValueTask<bool> IsProjectEmptyAsync(IStorageTransaction transaction, string projectId, CancellationToken cancellationToken) =>
        !await AnyAsync(transaction, Databases(projectId), cancellationToken).ConfigureAwait(false)
        && !await AnyAsync(transaction, EngineApiKeyStore.ProjectKeys(projectId), cancellationToken).ConfigureAwait(false)
        && !await AnyAsync(transaction, ProjectBackupDestinations.Collection(projectId), cancellationToken).ConfigureAwait(false)
        && !await AnyAsync(transaction, Features.FileStorage.FileStorageUsage.Buckets(projectId), cancellationToken).ConfigureAwait(false);

    // Every machine that ever ran this catalog (retired ones included) and this one; no holder is silently dropped.
    private ValueTask<HashSet<string>> ClosureHoldersAsync(IStorageTransaction transaction, CancellationToken cancellationToken) =>
        CatalogHoldersAsync(transaction, options.MachineId, cancellationToken);

    /// <summary>
    /// Every machine that ever ran this catalog (retired ones included) and <paramref name="machineId"/>: the holders that
    /// project closure and shared-history erasure (<see cref="HistoryErasures"/>) wait for. Bounded to 1024.
    /// </summary>
    internal static async ValueTask<HashSet<string>> CatalogHoldersAsync(IStorageTransaction transaction, string machineId, CancellationToken cancellationToken)
    {
        var holders = new HashSet<string>(StringComparer.Ordinal) { machineId };
        string? after = null;
        DocumentPage page;
        do
        {
            page = await transaction.ListAsync(CatalogGate.Members, after, MemberPage, MaxPageBytes, cancellationToken).ConfigureAwait(false);
            holders.UnionWith(page.Documents.Select(member => member.Id));
            if (holders.Count > 1024)
            {
                throw new InvalidOperationException("catalog membership exceeds its bound");
            }

            after = page.Documents.Count == 0 ? after : page.Documents[^1].Id;
        }
        while (page.HasMore);

        return holders;
    }

    // This machine's receipt, in a transaction that sees the fence: once empty, nothing registers here again.
    private async ValueTask RefreshClosureReceiptAsync(IStorageTransaction transaction, ProjectClosureRecord record, CancellationToken cancellationToken)
    {
        var current = await OwnClosureReceiptAsync(transaction, record, cancellationToken).ConfigureAwait(false);
        if (IsFinal(current, record))
        {
            return;
        }

        var empty = await IsProjectEmptyAsync(transaction, record.ProjectId, cancellationToken).ConfigureAwait(false);
        if (current is not null && current.Empty == empty && current.ClosureId == record.ClosureId)
        {
            return;
        }

        await PutAsync(transaction, ProjectClosure.Receipts(record.ProjectId), options.MachineId,
            new ProjectClosureReceipt(record.ProjectId, record.ClosureId, options.MachineId, empty, time.GetUtcNow()), TerraBaseJsonContext.Default.ProjectClosureReceipt, cancellationToken)
            .ConfigureAwait(false);
    }

    private ValueTask<ProjectClosureReceipt?> OwnClosureReceiptAsync(IStorageTransaction transaction, ProjectClosureRecord record, CancellationToken cancellationToken) =>
        GetAsync(transaction, ProjectClosure.Receipts(record.ProjectId), options.MachineId, TerraBaseJsonContext.Default.ProjectClosureReceipt, cancellationToken);

    // Causal, not wall-clock: a receipt is written only in a transaction that sees the permanent fence it names, so its
    // closure id proves it came after the fence whatever the two machines' clocks say.
    private bool IsFinal(ProjectClosureReceipt? receipt, ProjectClosureRecord record) =>
        receipt is { FormatVersion: 1, Empty: true } && receipt.ProjectId == record.ProjectId && receipt.ClosureId == record.ClosureId && receipt.MachineId == options.MachineId;

    // Accepted only when the recorded holders and every machine listed now confirmed the project empty under this fence
    // (bound by closure id: see IsFinal).
    private async ValueTask<ProjectClosureStatus> ClosureStatusAsync(IStorageTransaction transaction, ProjectClosureRecord record, CancellationToken cancellationToken)
    {
        var holders = await ClosureHoldersAsync(transaction, cancellationToken).ConfigureAwait(false);
        holders.UnionWith(record.ExpectedHolders);
        var pending = 0;
        foreach (var holder in holders)
        {
            var receipt = await GetAsync(transaction, ProjectClosure.Receipts(record.ProjectId), holder, TerraBaseJsonContext.Default.ProjectClosureReceipt, cancellationToken)
                .ConfigureAwait(false);
            if (receipt is not { FormatVersion: 1, Empty: true } || receipt.MachineId != holder || receipt.ProjectId != record.ProjectId
                || receipt.ClosureId != record.ClosureId)
            {
                pending++;
            }
        }

        return new ProjectClosureStatus(pending == 0 ? ProjectClosure.Accepted : ProjectClosure.Pending, record.ClosureId, pending);
    }

    // One bounded page per pass, as permanent purges are; only this machine's receipt is written.
    private async ValueTask ConvergeClosuresAsync(CancellationToken cancellationToken)
    {
        var page = await ReadAsync<DocumentPage?>(async transaction => await transaction.ListAsync(ProjectClosure.Collection, _closuresAfter,
            MemberPage, MaxPageBytes, cancellationToken).ConfigureAwait(false), static _ => null, cancellationToken).ConfigureAwait(false);
        if (page is null)
        {
            return;
        }

        foreach (var document in page.Documents)
        {
            try
            {
                var record = EntityPayload.Read(document, TerraBaseJsonContext.Default.ProjectClosureRecord);
                if (record is not { FormatVersion: 1 } || record.ProjectId != document.Id)
                {
                    throw new InvalidOperationException("a project closure record has unsupported identity or format");
                }

                if (await ReadAsync<bool>(async transaction => IsFinal(await OwnClosureReceiptAsync(transaction, record, cancellationToken).ConfigureAwait(false), record),
                    static _ => false, cancellationToken).ConfigureAwait(false))
                {
                    continue;
                }

                _ = await WriteAsync<Done>(async transaction =>
                {
                    await RefreshClosureReceiptAsync(transaction, record, cancellationToken).ConfigureAwait(false);
                    return new Done();
                }, static _ => new Done(), cancellationToken).ConfigureAwait(false);
            }
            catch (Exception error) when (!cancellationToken.IsCancellationRequested)
            {
                logger.ZLogWarning(error, $"Confirming project closure {document.Id:@Project} failed; the next pass tries again");
            }
        }

        _closuresAfter = page.HasMore ? page.Documents[^1].Id : null;
    }
}
