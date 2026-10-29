using System.Text.Json;
using TerraScale.Dashboard.Features.Billing;
using TerraScale.Dashboard.Platform.Persistence;
using TerraScale.DotNet;
using TerraScale.DotNet.TerraBase;

namespace TerraScale.Dashboard.Features.Profile;

// Immutable authorization and opaque owned references survive ACK indefinitely.
// Local CAS fences are not a finite barrier against later regional LWW joins.
public sealed class DeletionCleanupJournal(IDashboardState state)
{
    public const string Collection = "dashboard_deletion_cleanup";
    public sealed record Entry(TerraScaleAuthDeletionProof Proof, string[] ProjectIds, string[] CustomerIds);

    public async Task<Entry> RememberAsync(TerraScaleAuthDeletionProof proof, CancellationToken cancellationToken)
    {
        if (await state.GetAsync(Collection, proof.Id, cancellationToken) is { } existing) return Match(existing, proof);
        HashSet<string> projects = [];
        HashSet<string> customers = [];
        if (proof.Scope == "organization")
        {
            if (await state.GetAsync(AccountService.OrganizationsCollection, proof.SubjectId, cancellationToken) is { } org && !AccountService.IsDeletionFence(org))
            {
                var value = AccountService.Read<OrganizationRecord>(org);
                if (org.Id != proof.SubjectId || value.Id != proof.SubjectId) throw new IOException("Cleanup journal organization changed.");
                projects.UnionWith(value.Projects.Keys);
                projects.UnionWith(value.DeletedProjectIds);
            }
            if (await state.GetAsync(StripeBilling.Collection, proof.SubjectId, cancellationToken) is { } billing && !AccountService.IsDeletionFence(billing))
            {
                var binding = StripeBilling.ReadBinding(billing, proof.SubjectId);
                if (binding.CustomerId.Length > 0) customers.Add(binding.CustomerId);
            }
            foreach (var document in await state.ListAsync(StripeBilling.CheckoutsCollection, cancellationToken))
            {
                var checkout = AccountService.Read<CheckoutBinding>(document);
                if (checkout.OrganizationId != proof.SubjectId) continue;
                if (checkout.CustomerId.Length > 0) customers.Add(checkout.CustomerId);
                if (TerraScaleIds.IsValid(checkout.ProjectId)) projects.Add(checkout.ProjectId);
            }
        }
        var entry = new Entry(proof, [.. projects.Order(StringComparer.Ordinal)], [.. customers.Order(StringComparer.Ordinal)]);
        try { await state.PutAsync(Collection, proof.Id, JsonSerializer.SerializeToElement(entry), TerraBasePrecondition.Absent, cancellationToken); }
        catch (TerraBasePreconditionFailedException)
        {
            return Match(await state.GetAsync(Collection, proof.Id, cancellationToken) ?? throw new IOException("Cleanup journal disappeared."), proof);
        }
        return entry;
    }

    public async Task<Entry> ReadAsync(TerraBaseDocument document, CancellationToken cancellationToken)
    {
        var entry = AccountService.Read<Entry>(document);
        if (entry.Proof is null || !TerraScaleIds.IsValid(entry.Proof.Id) || entry.ProjectIds is null || entry.CustomerIds is null ||
            entry.ProjectIds.Any(id => !TerraScaleIds.IsValid(id)) || entry.CustomerIds.Any(string.IsNullOrEmpty))
            throw new IOException("Cleanup journal is invalid.");
        var stored = await state.GetAsync(Collection, entry.Proof.Id, cancellationToken) ?? throw new IOException("Cleanup journal disappeared.");
        return Match(stored, entry.Proof);
    }

    private static Entry Match(TerraBaseDocument document, TerraScaleAuthDeletionProof proof)
    {
        var entry = AccountService.Read<Entry>(document);
        if (document.Id != proof.Id || entry.Proof is null || entry.Proof.Id != proof.Id || entry.Proof.Scope != proof.Scope || entry.Proof.SubjectId != proof.SubjectId || entry.Proof.OrganizationId != proof.OrganizationId || entry.ProjectIds is null || entry.CustomerIds is null)
            throw new IOException("Cleanup journal proof changed.");
        return entry;
    }
}
