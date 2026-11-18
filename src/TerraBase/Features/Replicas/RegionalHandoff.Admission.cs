using NATS.Client.KeyValueStore;
using TerraBase.Errors;
using TerraBase.Features.Memory;

namespace TerraBase.Features.Replicas;

internal sealed partial class RegionalHandoff
{
    private const string Admitted = "admitted";
    private const long DrainMemory = 16L * 1024 * 1024;

    private sealed record PrimeMark(string Source, long Epoch, long Since);

    private sealed record DrainAdmission(string Transfer, string Target, string Spec, IMemoryReservation Memory, ITimer Expiry);

    private readonly Lock _admissionGate = new();
    private DrainAdmission? _admission;

    private bool Replaceable(RegionalTransfer admitted) =>
        admitted.Deadline <= DateTimeOffset.UtcNow || admitted.Source == options.MachineId && admitted.SourceSession == _session;

    private async Task<string?> HeldAdmissionAsync(INatsKVStore kv, string target, string spec, CancellationToken cancellationToken)
    {
        DrainAdmission? held;
        lock (_admissionGate)
        {
            held = _admission;
        }
        if (held is null || held.Target != target || held.Spec != spec)
        {
            return null;
        }

        return await ReadAsync(kv, options.Region, cancellationToken).ConfigureAwait(false) is { Value: { } value }
            && Decode(value).Transfer is { Phase: Admitted or Retiring } transfer && transfer.Id == held.Transfer
            && transfer.Source == options.MachineId && transfer.SourceSession == _session && transfer.Target == target
                ? held.Transfer : null;
    }

    private void HoldAdmission(string transfer, string target, string spec, IMemoryReservation memory)
    {
        var expiry = services.GetRequiredService<TimeProvider>().CreateTimer(_ => ReleaseAdmission(transfer), null, Timeout.InfiniteTimeSpan, Timeout.InfiniteTimeSpan);
        lock (_admissionGate)
        {
            _admission = new DrainAdmission(transfer, target, spec, memory, expiry);
        }
        _ = expiry.Change(DrainBudget, Timeout.InfiniteTimeSpan);
    }

    private IMemoryReservation? TakeAdmission(string transfer, string target)
    {
        DrainAdmission? taken;
        lock (_admissionGate)
        {
            taken = _admission is { } held && held.Transfer == transfer && held.Target == target ? held : null;
            if (taken is not null)
            {
                _admission = null;
            }
        }
        taken?.Expiry.Dispose();
        return taken?.Memory;
    }

    private void ReleaseAdmission(string? transfer)
    {
        DrainAdmission? released;
        lock (_admissionGate)
        {
            released = _admission;
            if (released is null || transfer is not null && released.Transfer != transfer)
            {
                return;
            }
            _admission = null;
        }
        released.Expiry.Dispose();
        released.Memory.Dispose();
    }

    private async Task<DrainReservation> ClaimAdmittedAsync(INatsKVStore kv, string target, string id, DrainProgress progress, CancellationToken cancellationToken)
    {
        var entry = await ReadAsync(kv, options.Region, cancellationToken).ConfigureAwait(false);
        if (entry is null)
        {
            return DrainRefused(progress, "regional owner unavailable", "owner_unavailable");
        }

        var owner = Decode(entry.Value.Value!);
        if (owner.Transfer is not { } transfer || transfer.Id != id || transfer.Source != options.MachineId
            || transfer.SourceSession != _session || transfer.Target != target)
        {
            return DrainRefused(progress, "the admitted transfer is unknown or expired", "admission_unknown");
        }
        if (transfer.Phase == Retiring)
        {
            return entry.Value;
        }
        if (transfer.Phase != Admitted)
        {
            return DrainRefused(progress, "the admitted transfer is unknown or expired", "admission_unknown");
        }

        var preparing = owner with { Transfer = transfer with { Phase = Preparing, Deadline = DateTimeOffset.UtcNow + DrainBudget } };
        var revision = await kv.UpdateAsync(options.Region, Encode(preparing), entry.Value.Revision, cancellationToken: cancellationToken).ConfigureAwait(false);
        entry = await ReadAsync(kv, options.Region, cancellationToken).ConfigureAwait(false);
        return entry?.Revision == revision ? entry.Value : DrainRefused(progress, "the stop reservation changed", "owner_changed");
    }
}
