using System.Globalization;
using System.Numerics;
using System.Text.Json;
using TerraScale.Dashboard.Features.Auth;
using TerraScale.Dashboard.Features.Billing.Internal;
using TerraScale.Dashboard.Platform.Persistence;
using TerraScale.Dashboard.Shared.Formatting;
using TerraScale.DotNet.TerraBase;
using TerraScale.DotNet;

namespace TerraScale.Dashboard.Features.Billing;

public sealed class InternalBillingService(IDashboardState state, AccountService accounts, IPlatformRoleGateway roles,
    IUsageMeasurements measurements, TimeProvider time, ITsAuthPlatform platform)
{
    public const string Collection = "dashboard_internal_billing";
    public const string SingletonId = "organization";
    private readonly TerraScaleIdGenerator ids = new();
    private const string MissingUsage = "Every project's monthly usage is required to compute the internal bill and evaluate its cap.";
    private const string InvalidAmount = "The internal bill exceeds the supported amount or has invalid reporting rates.";

    public async Task ConfigureAsync(AccountContext principal, InternalBillingConfiguration config,
        Func<CancellationToken, Task<AccountContext>> refreshAuthority, CancellationToken cancellationToken)
    {
        await AdministratorAsync(principal, cancellationToken);
        if (config.OrganizationId.Length == 0 || config.OrganizationId.Trim() != config.OrganizationId ||
            config.BasePlanKey.Trim() != config.BasePlanKey || config.SpendCapCents <= 0 ||
            config.WriteCentsPerMillion < 0 || config.StorageCentsPerGBMonth < 0)
            throw new AccountAccessException("An exact organization ID, paid base plan and positive cap with non-negative rates are required.", 422);
        var basePlan = PlanCatalog.For(config.BasePlanKey);
        if (basePlan.PriceCents <= 0) throw new AccountAccessException("Select an existing paid base plan key.", 422);
        var target = await AuthorizedOrganizationAsync(principal, config.OrganizationId, cancellationToken);
        Target(target.Organization, target.Authority);
        var reservation = await ReserveAsync(target.Organization, config, cancellationToken);
        try { await ClaimSingletonAsync(reservation, cancellationToken); }
        catch (AccountAccessException)
        {
            // Only a proven uncommitted loser may release its own fence. An
            // unknown commit or failed read remains closed until a safe retry.
            await ReleaseUncommittedAsync(reservation, cancellationToken);
            throw;
        }
        for (var attempt = 0; attempt < 8; attempt++)
        {
            var currentFence = await FenceAsync(config.OrganizationId, cancellationToken);
            if (currentFence.RequestId != reservation.RequestId || currentFence.Kind != "internal")
                throw new AccountAccessException("Another internal billing configuration is in progress.", 409);
            await ClaimSingletonAsync(reservation, cancellationToken);
            principal = await RefreshAdministratorAsync(principal, refreshAuthority, cancellationToken);
            target = await AuthorizedOrganizationAsync(principal, config.OrganizationId, cancellationToken);
            Target(target.Organization, target.Authority);
            var proposal = reservation.InternalReservation!;
            if (target.Organization.InternalBilling?.ConfigurationId == proposal.Policy.ConfigurationId)
            {
                proposal.Policy.Validate(target.Organization);
                await ActivateAsync(reservation, cancellationToken);
                return;
            }
            target.Organization.InternalBilling = proposal.Policy;
            target.Organization.PlanId = proposal.Policy.PlanId;
            target.Organization.BillingStatus = "active";
            target.Organization.SpendCapCents = proposal.SpendCapCents;
            target.Organization.PaidExcess = true;
            try
            {
                await state.PutAsync(AccountService.OrganizationsCollection, config.OrganizationId,
                    JsonSerializer.SerializeToElement(target.Organization), TerraBasePrecondition.Matches(target.Document.Version), cancellationToken);
                await ActivateAsync(reservation, cancellationToken);
                return;
            }
            catch (TerraBasePreconditionFailedException) when (attempt < 7) { }
        }
        throw new IOException("Internal billing changed concurrently. Retry configuration.");
    }

    public async Task<InternalBill> ReportAsync(AccountContext principal, string organizationId,
        Func<CancellationToken, Task<AccountContext>> refreshAuthority, CancellationToken cancellationToken)
    {
        await AdministratorAsync(principal, cancellationToken);
        // The operator must also have a genuine fresh producer-authorized
        // membership. No owner impersonation or management key is borrowed.
        var context = await AccountScope.RefreshAsync(accounts, principal with { Organization = new() { Id = organizationId }, Project = null }, cancellationToken);
        var (organization, bill) = await ReadDetailsAsync(context, cancellationToken);
        if (bill is { Available: true, CapReached: true })
            try { await RecordAlertAsync(principal, organization, bill, refreshAuthority, cancellationToken); }
            catch (Exception error) when (error is AccountAccessException or IOException or HttpRequestException or JsonException or TimeoutException ||
                                          error is OperationCanceledException && !cancellationToken.IsCancellationRequested)
            { throw new InternalBillingReportException(bill, error); }
        return bill;
    }

    public async Task<InternalBill> ReadAsync(AccountContext context, CancellationToken cancellationToken)
        => (await ReadDetailsAsync(context, cancellationToken)).Bill;

    private async Task<(OrganizationRecord Organization, InternalBill Bill)> ReadDetailsAsync(AccountContext context, CancellationToken cancellationToken)
    {
        context = await AccountScope.RefreshAsync(accounts, context, cancellationToken);
        var target = await AuthorizedOrganizationAsync(context, context.Organization!.Id, cancellationToken);
        var organization = target.Organization;
        if (organization.InternalBilling is null) throw new AccountAccessException("Organization has no internal billing policy.", 422);
        await ValidateClaimsAsync(state, organization, cancellationToken);
        var selection = new UsageSelection(context with { Organization = organization }, "7d", "", false);
        return (organization, Estimate(organization, await measurements.ReadAsync(selection, cancellationToken), time.GetUtcNow()));
    }

    public static InternalBill Estimate(OrganizationRecord organization, UsageMeasurements measurements, DateTimeOffset now)
    {
        var policy = organization.InternalBilling ?? throw new IOException("Internal billing policy is unavailable.");
        policy.Validate(organization);
        var utc = now.UtcDateTime;
        var from = new DateTimeOffset(utc.Year, utc.Month, 1, 0, 0, 0, TimeSpan.Zero);
        var to = now;
        var initial = new InternalBill(false, true, default, default, 0, 0, 0, policy.Plan.PriceCents, 0, 0, 0,
            organization.SpendCapCents, false, policy.WriteCentsPerMillion, policy.StorageCentsPerGBMonth);
        var projects = organization.Projects.Values.ToArray();
        if (projects.Length == 0 || measurements.Scope.State == UsageSourceState.NoProducer ||
            projects.Any(project => measurements.Monthly.GetValueOrDefault(project.Id) is not { State: UsageSourceState.Measured, Reading: not null }))
            return initial with { Error = MissingUsage };
        var coherent = false;
        try
        {
            // Use the month captured by the actual producer request, even if
            // the last response arrives just after a UTC month rollover.
            if (measurements.MonthStart is not { } capturedMonth) return initial with { Error = MissingUsage };
            from = capturedMonth;
            if (from.Offset != TimeSpan.Zero || from.Day != 1 || from.TimeOfDay != TimeSpan.Zero || from > now)
                return initial with { Error = MissingUsage };
            // TerraBase includes the current UTC day bucket; its upper boundary
            // is the next midnight unless the request was made exactly at midnight.
            var ticks = TimeSpan.TicksPerDay;
            var roundedEnd = new DateTimeOffset(checked((now.UtcTicks + ticks - 1) / ticks * ticks), TimeSpan.Zero);
            to = from;
            long reads = 0, writes = 0, bytes = 0;
            foreach (var project in projects)
            {
                var reading = measurements.Monthly[project.Id].Reading!;
                var period = reading.Period;
                if (organization.Projects.GetValueOrDefault(project.Id) != project || project.OrganizationId != organization.Id ||
                    reading.ProjectId != project.Id || reading.DatabaseId.Length != 0 || period.From != from || period.Granularity != "day" ||
                    period.To.Offset != TimeSpan.Zero || period.To.TimeOfDay != TimeSpan.Zero ||
                    period.To <= from || period.To > from.AddMonths(1) || period.To > roundedEnd ||
                    period.Reads < 0 || period.Writes < 0 || reading.LogicalBytes < 0)
                    return initial with { Error = MissingUsage };
                reads = checked(reads + period.Reads); writes = checked(writes + period.Writes); bytes = checked(bytes + reading.LogicalBytes);
                if (period.To > to) to = period.To;
            }
            // Metering facts remain genuine when only the price exceeds the
            // supported amount. Publish them only after every total is valid.
            initial = initial with { PeriodStart = from, PeriodEnd = to, Reads = reads, Writes = writes, LogicalBytes = bytes };
            coherent = true;
            var writeCharge = Charge(BigInteger.Max(0, (BigInteger)writes - policy.Allowances.MonthlyOperations), policy.WriteCentsPerMillion, 1_000_000);
            var storageCharge = Charge(BigInteger.Max(0, (BigInteger)bytes - policy.Allowances.StorageBytes), policy.StorageCentsPerGBMonth, 1_000_000_000);
            var total = checked(policy.Plan.PriceCents + checked(writeCharge + storageCharge));
            var reached = organization.SpendCapCents > 0 && total >= organization.SpendCapCents;
            return initial with
            {
                Available = true,
                PeriodStart = from,
                PeriodEnd = to,
                Reads = reads,
                Writes = writes,
                LogicalBytes = bytes,
                WriteCents = writeCharge,
                StorageCents = storageCharge,
                TotalCents = total,
                CapReached = reached,
                Alert = reached ? $"Internal spending cap reached: estimated bill {MoneyFormat.Money(total)}, cap {MoneyFormat.Money(organization.SpendCapCents)}. Report only; services remain active." : null
            };
        }
        catch (OverflowException) { return initial with { Error = coherent ? InvalidAmount : MissingUsage }; }
    }

    private static long Charge(BigInteger units, long rate, long scale)
    {
        if (units < 0 || rate < 0 || scale <= 0) throw new OverflowException();
        return checked((long)((units * rate + scale - 1) / scale));
    }

    private async Task<BillingBinding> ReserveAsync(OrganizationRecord organization, InternalBillingConfiguration config, CancellationToken cancellationToken)
    {
        for (var attempt = 0; attempt < 8; attempt++)
        {
            var document = await state.GetAsync(StripeBilling.Collection, organization.Id, cancellationToken);
            var current = document is null ? null : StripeBilling.ReadBinding(document, organization.Id);
            if (current is { Kind: not "internal" })
                throw new AccountAccessException("Internal billing cannot replace Stripe billing or checkout state.", 409);
            if (current is not null)
            {
                if (Same(current.InternalReservation!, config) && (current.State == "reserved" ||
                    organization.InternalBilling?.ConfigurationId == current.RequestId && organization.SpendCapCents == config.SpendCapCents)) return current;
                if (current.State != "active") throw new AccountAccessException("Finish the reserved internal billing configuration before changing its rates.", 409);
                if (organization.InternalBilling?.ConfigurationId != current.RequestId) throw new IOException("Internal billing activation does not match its policy.");
            }
            else if (organization.InternalBilling is not null) throw new IOException("The internal billing fence is unavailable.");
            var planId = current?.InternalReservation!.Policy.PlanId ?? ids.NewId();
            var requestId = ids.NewId();
            var plan = PlanCatalog.For(config.BasePlanKey) with { Id = planId, Key = "internal", Name = "TerraScale Internal" };
            var policy = new InternalBillingPolicy(organization.Id, planId, config.BasePlanKey, requestId, plan,
                PlanCatalog.Limits(config.BasePlanKey), PlanCatalog.ResourceLimits(config.BasePlanKey), config.WriteCentsPerMillion, config.StorageCentsPerGBMonth);
            var next = new BillingBinding
            {
                OrganizationId = organization.Id,
                Kind = "internal",
                State = "reserved",
                RequestId = requestId,
                StartedAt = time.GetUtcNow(),
                Plan = "internal",
                Interval = "monthly",
                Status = "pending",
                Revision = current is null ? 0 : checked(current.Revision + 1),
                InternalReservation = new(policy, config.SpendCapCents, organization.InternalBilling?.ConfigurationId ?? "")
            };
            try
            {
                await state.PutAsync(StripeBilling.Collection, organization.Id, JsonSerializer.SerializeToElement(next),
                    document is null ? TerraBasePrecondition.Absent : TerraBasePrecondition.Matches(document.Version), cancellationToken);
                return next;
            }
            catch (TerraBasePreconditionFailedException) when (attempt < 7) { }
        }
        throw new IOException("Internal billing changed concurrently.");
    }

    private static bool Same(InternalBillingReservation reservation, InternalBillingConfiguration config) =>
        reservation.Policy.OrganizationId == config.OrganizationId && reservation.Policy.BasePlanKey == config.BasePlanKey &&
        reservation.SpendCapCents == config.SpendCapCents && reservation.Policy.WriteCentsPerMillion == config.WriteCentsPerMillion &&
        reservation.Policy.StorageCentsPerGBMonth == config.StorageCentsPerGBMonth;

    private async Task ClaimSingletonAsync(BillingBinding reservation, CancellationToken cancellationToken)
    {
        var singleton = new InternalBillingSingleton(reservation.OrganizationId, reservation.InternalReservation!.Policy.PlanId);
        var document = await state.GetAsync(Collection, SingletonId, cancellationToken);
        if (document is null)
            try
            {
                await state.PutAsync(Collection, SingletonId, JsonSerializer.SerializeToElement(singleton), TerraBasePrecondition.Absent, cancellationToken);
                return;
            }
            catch (TerraBasePreconditionFailedException) { document = await state.GetAsync(Collection, SingletonId, cancellationToken); }
        if (document is null || document.Id != SingletonId) throw new IOException("Internal organization ownership is unavailable.");
        var existing = AccountService.Read<InternalBillingSingleton>(document);
        if (string.IsNullOrEmpty(existing.OrganizationId) || !TerraScaleIds.IsValid(existing.PlanId)) throw new IOException("Internal organization ownership is invalid.");
        if (existing.OrganizationId != singleton.OrganizationId) throw new AccountAccessException("Internal billing already belongs to another organization.", 409);
        if (existing.PlanId != singleton.PlanId) throw new IOException("The internal plan identity changed.");
    }

    private async Task ActivateAsync(BillingBinding reservation, CancellationToken cancellationToken)
    {
        for (var attempt = 0; attempt < 8; attempt++)
        {
            var document = await state.GetAsync(StripeBilling.Collection, reservation.OrganizationId, cancellationToken) ?? throw new IOException("Internal billing fence is unavailable.");
            var current = StripeBilling.ReadBinding(document, reservation.OrganizationId);
            if (current.Kind != "internal" || current.RequestId != reservation.RequestId) throw new AccountAccessException("Internal billing changed concurrently.", 409);
            if (current.State == "active") return;
            current.State = "active"; current.Status = "active"; current.EffectivePlan = "internal"; current.EffectiveInterval = "monthly";
            current.Revision = checked(current.Revision + 1);
            try { await state.PutAsync(StripeBilling.Collection, current.OrganizationId, JsonSerializer.SerializeToElement(current), TerraBasePrecondition.Matches(document.Version), cancellationToken); return; }
            catch (TerraBasePreconditionFailedException) when (attempt < 7) { }
        }
        throw new IOException("Internal billing activation changed concurrently.");
    }

    private async Task ReleaseUncommittedAsync(BillingBinding reservation, CancellationToken cancellationToken)
    {
        var target = await OrganizationAsync(reservation.OrganizationId, cancellationToken);
        if (target.Organization.InternalBilling is not null) return;
        var document = await state.GetAsync(StripeBilling.Collection, reservation.OrganizationId, cancellationToken);
        if (document is null) return;
        var current = StripeBilling.ReadBinding(document, reservation.OrganizationId);
        if (current.Kind == "internal" && current.State == "reserved" && current.RequestId == reservation.RequestId)
            try { await state.DeleteAsync(StripeBilling.Collection, reservation.OrganizationId, document.Version, cancellationToken); }
            catch (TerraBasePreconditionFailedException) { }
    }

    private async Task RecordAlertAsync(AccountContext principal, OrganizationRecord observed, InternalBill bill,
        Func<CancellationToken, Task<AccountContext>> refreshAuthority, CancellationToken cancellationToken)
    {
        var month = bill.PeriodStart.UtcDateTime.ToString("yyyy-MM", CultureInfo.InvariantCulture);
        var alertKey = month + "/" + AccountService.AccountStorageKey(observed.OwnerUserId);
        InternalBillingAlert? alert = null;
        for (var attempt = 0; attempt < 8; attempt++)
        {
            principal = await RefreshAdministratorAsync(principal, refreshAuthority, cancellationToken);
            var current = await AuthorizedOrganizationAsync(principal, observed.Id, cancellationToken);
            if (current.Organization.InternalBilling?.ConfigurationId != observed.InternalBilling!.ConfigurationId ||
                current.Organization.OwnerUserId != observed.OwnerUserId || current.Organization.SpendCapCents != bill.SpendCapCents)
                throw new AccountAccessException("Internal billing changed while recording its cap alert.", 409);
            if (current.Organization.InternalBillingAlerts.TryGetValue(alertKey, out alert) &&
                alert.ConfigurationId == observed.InternalBilling.ConfigurationId && alert.SpendCapCents == bill.SpendCapCents) break;
            alert = new(current.Organization.OwnerUserId, observed.InternalBilling.ConfigurationId, bill.SpendCapCents,
                alert?.Id ?? ids.NewId(), month, bill.Alert!, time.GetUtcNow());
            current.Organization.InternalBillingAlerts[alertKey] = alert;
            try { await state.PutAsync(AccountService.OrganizationsCollection, observed.Id, JsonSerializer.SerializeToElement(current.Organization), TerraBasePrecondition.Matches(current.Document.Version), cancellationToken); break; }
            catch (TerraBasePreconditionFailedException) when (attempt < 7) { alert = null; }
        }
        if (alert is null || alert.Month != month || alert.Id.Length == 0 || alert.OwnerUserId.Length == 0)
            throw new IOException("Internal billing cap alert is unavailable.");
        var key = AccountService.AccountStorageKey(alert.OwnerUserId);
        for (var attempt = 0; attempt < 8; attempt++)
        {
            var document = await state.GetAsync(AccountService.AccountsCollection, key, cancellationToken) ?? throw new IOException("Internal billing owner account is unavailable.");
            var account = AccountService.Read<AccountRecord>(document);
            var identityDocument = await state.GetAsync(AccountService.IdentitiesCollection, key, cancellationToken) ?? throw new IOException("Internal billing owner identity is unavailable.");
            var identity = AccountService.Read<AccountIdentityBinding>(identityDocument);
            if (document.Id != key || identityDocument.Id != key || account.UserId != alert.OwnerUserId || identity.UserId != account.UserId ||
                identity.AccountId != account.Id ||
                !(TerraScaleIds.IsValid(account.Id) || account.Id == key)) throw new IOException("Internal billing owner account identity is invalid.");
            principal = await RefreshAdministratorAsync(principal, refreshAuthority, cancellationToken);
            // The CLI's explicit target need not be the browser selection.
            // Re-resolve its provider membership after credential rotation,
            // then read its canonical owner/policy/cap after that awaited work.
            var latest = await AuthorizedOrganizationAsync(principal, observed.Id, cancellationToken);
            if (latest.Organization.OwnerUserId != alert.OwnerUserId || latest.Organization.InternalBilling?.ConfigurationId != alert.ConfigurationId ||
                latest.Organization.SpendCapCents != alert.SpendCapCents)
                throw new AccountAccessException("Internal billing changed while recording its cap alert.", 409);
            if (account.Notifications.Any(notification => notification.OrganizationId == observed.Id && notification.Type == "internal-cap" && notification.BillingMonth == month)) return;
            account.Notifications.Add(new()
            {
                Id = alert.Id,
                OrganizationId = observed.Id,
                Type = "internal-cap",
                Title = "Internal spending cap reached",
                Body = alert.Body,
                Severity = "warning",
                Status = "Unread",
                Url = "/organization?tab=billing",
                CreatedAt = alert.CreatedAt,
                BillingMonth = month
            });
            // The account CAS deduplicates inbox writes; it cannot atomically
            // guard a later change to the separate organization document.
            try { await state.PutAsync(AccountService.AccountsCollection, key, JsonSerializer.SerializeToElement(account), TerraBasePrecondition.Matches(document.Version), cancellationToken); return; }
            catch (TerraBasePreconditionFailedException) when (attempt < 7) { }
        }
        throw new IOException("Internal billing owner inbox changed concurrently.");
    }

    private async Task AdministratorAsync(AccountContext principal, CancellationToken cancellationToken)
    {
        if (principal.UserId.Length == 0 || principal.Session.UserId != principal.UserId ||
            await roles.PlatformRoleAsync(principal.Session, cancellationToken) != "admin")
            throw new AccountAccessException("Platform administrator authorization is required.");
    }

    private async Task<AccountContext> RefreshAdministratorAsync(AccountContext previous,
        Func<CancellationToken, Task<AccountContext>> refreshAuthority, CancellationToken cancellationToken)
    {
        var fresh = await refreshAuthority(cancellationToken);
        if (fresh.UserId != previous.UserId || !fresh.Session.Email.Equals(previous.Session.Email, StringComparison.OrdinalIgnoreCase))
            throw new AccountAccessException("Administrator identity changed.");
        await AdministratorAsync(fresh, cancellationToken);
        return fresh;
    }

    private async Task<(TerraBaseDocument Document, OrganizationRecord Organization)> OrganizationAsync(string id, CancellationToken cancellationToken)
    {
        var document = await state.GetAsync(AccountService.OrganizationsCollection, id, cancellationToken) ?? throw new AccountAccessException("Organization was not found.", 404);
        var organization = AccountService.Read<OrganizationRecord>(document);
        if (document.Id != id || organization.Id != id || organization.InternalBillingAlerts is null) throw new IOException("Internal billing organization identity is invalid.");
        return (document, organization);
    }

    private async Task<(TerraBaseDocument Document, OrganizationRecord Organization, OrganizationRecord Authority)> AuthorizedOrganizationAsync(AccountContext principal, string id, CancellationToken cancellationToken)
    {
        // Capture the CAS version before awaited canonical reads so a newer dashboard projection cannot be overwritten.
        var target = await OrganizationAsync(id, cancellationToken);
        var current = await AccountScope.RefreshAsync(accounts, principal with { Organization = new() { Id = id }, Project = null }, cancellationToken);
        var members = await platform.ListMembersAsync(current.Session, id, cancellationToken);
        var owners = members.Where(member => member.Role == "owner").ToArray();
        if (members.Any(member => string.IsNullOrEmpty(member.UserId)) || members.Select(member => member.UserId).Distinct(StringComparer.Ordinal).Count() != members.Count ||
            !members.Any(member => member.UserId == current.UserId) || owners.Length != 1)
            throw new IOException("Internal organization owner identity is invalid.");
        // I3 keeps OwnerUserId transient; each persisted billing alert owns its owner reference.
        target.Organization.OwnerUserId = owners[0].UserId;
        // I3's hydrated map preserves settings for current projects; canonical project fields are JsonIgnore.
        target.Organization.Projects = current.Organization!.Projects;
        return (target.Document, target.Organization, current.Organization!);
    }

    private async Task<BillingBinding> FenceAsync(string id, CancellationToken cancellationToken) => StripeBilling.ReadBinding(
        await state.GetAsync(StripeBilling.Collection, id, cancellationToken) ?? throw new IOException("Internal billing fence is unavailable."), id);

    public static async Task ValidateClaimsAsync(IDashboardState state, OrganizationRecord organization, CancellationToken cancellationToken)
    {
        if (organization.InternalBilling is not { } policy) return;
        policy.Validate(organization);
        var singletonDocument = await state.GetAsync(Collection, SingletonId, cancellationToken) ?? throw new IOException("Internal organization ownership is unavailable.");
        if (singletonDocument.Id != SingletonId) throw new IOException("Internal organization ownership identity is invalid.");
        var singleton = AccountService.Read<InternalBillingSingleton>(singletonDocument);
        if (singleton.OrganizationId != organization.Id || singleton.PlanId != policy.PlanId) throw new IOException("Internal organization ownership changed.");
        var fenceDocument = await state.GetAsync(StripeBilling.Collection, organization.Id, cancellationToken) ?? throw new IOException("Internal billing fence is unavailable.");
        var fence = StripeBilling.ReadBinding(fenceDocument, organization.Id);
        if (fence.Kind != "internal" || fence.InternalReservation!.Policy.PlanId != policy.PlanId ||
            (fence.State == "active" ? fence.RequestId != policy.ConfigurationId :
                fence.RequestId != policy.ConfigurationId && fence.InternalReservation.PreviousConfigurationId != policy.ConfigurationId))
            throw new IOException("Internal billing fence does not match its policy.");
    }

    private static void Target(OrganizationRecord organization, OrganizationRecord authority)
    {
        if (authority.Id != organization.Id) throw new IOException("Internal organization identity is invalid.");
        if (authority.Name != "terrascale") throw new AccountAccessException("Internal billing requires the organization named exactly terrascale.");
        if (authority.Status != "active" || organization.Archived || organization.DeletionRequested) throw new AccountAccessException("The internal organization must be active.", 409);
        foreach (var name in new[] { "ts-dashboard", "ts-auth", "ts-router", "ts-status", "ts-orchestrator", "ku", "ts-reviewer" })
            if (!authority.Projects.Any(pair => pair.Key == pair.Value.Id && pair.Value.OrganizationId == organization.Id && pair.Value.Status == "active" && pair.Value.Name == name))
                throw new AccountAccessException("Internal service project " + name + " is missing or inactive.", 409);
        organization.InternalBilling?.Validate(organization);
    }
}
