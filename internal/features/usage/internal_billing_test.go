package usage

import (
	"context"
	"errors"
	"math"
	"reflect"
	"regexp"
	"sync"
	"testing"
	"time"

	"terrascale.tech/dashboard/internal/adapters/terrabase"
	"terrascale.tech/dashboard/internal/core"
	"terrascale.tech/dashboard/internal/core/memstore"
	"terrascale.tech/dashboard/internal/features/payments"
	"terrascale.tech/dashboard/internal/features/subscriptions"
	"terrascale.tech/dashboard/internal/platform/terrabasetest"
)

type internalMeter struct {
	mu     sync.Mutex
	values map[string]core.Usage
	fail   string
	scopes []core.UsageScope
}

func (m *internalMeter) Usage(_ context.Context, token string, scope core.UsageScope) (core.Usage, error) {
	m.mu.Lock()
	defer m.mu.Unlock()
	m.scopes = append(m.scopes, scope)
	if token != "user-token" {
		return core.Usage{}, core.ErrUnauthorized
	}
	if scope.ProjectID == "" {
		return core.Usage{}, errors.New("TerraBase has no organization usage route")
	}
	if scope.ProjectID == m.fail && scope.Granularity == "day" {
		return core.Usage{}, core.ErrUnavailable
	}
	value := m.values[scope.ProjectID]
	if value.Period.From.IsZero() && value.Period.To.IsZero() {
		value.Period.From, value.Period.To, value.Period.Granularity = scope.From, time.Now().UTC(), scope.Granularity
	}
	return value, nil
}

func internalUsageFixture(t *testing.T, store core.Store) (Service, core.Principal, *internalMeter) {
	t.Helper()
	a, err := store.CreateAccount(t.Context(), core.User{Name: "Internal owner", Email: "internal@example.test", PasswordHash: "unused", PlatformAdmin: true})
	if err != nil {
		t.Fatal(err)
	}
	p := a.Principal()
	plan, err := store.Plan(t.Context(), "business")
	if err != nil {
		t.Fatal(err)
	}
	plan.PriceCents, plan.OperationsPerMonth, plan.StorageMiB = 500, 100, 1024
	if err := store.UpsertPlanAs(t.Context(), p, plan); err != nil {
		t.Fatal(err)
	}
	meter := &internalMeter{values: map[string]core.Usage{}}
	if err := store.UpdateAs(t.Context(), p, func(a *core.Account) error {
		org := a.Organizations[p.OrganizationID]
		org.Name, org.Kind = "terrascale", "business"
		org.Projects = map[string]core.Project{}
		for i, name := range []string{"ts-dashboard", "ts-auth", "ts-router", "ts-status", "ts-orchestrator", "ku", "ts-reviewer"} {
			id := core.NewULID()
			if i == 0 {
				id = p.ProjectID
			}
			project := core.Project{ID: id, OrganizationID: org.ID, Name: name, Status: "active", Databases: map[string]core.Database{}}
			org.Projects[id] = project
			_, meterID := project.TerraBaseIDs(org.ID)
			value := core.Usage{ProjectID: meterID, StorageBytes: 1 << 40, LogicalBytes: 2 << 30, Period: core.UsagePeriod{Reads: 7, Writes: 200_001}}
			if name == "ts-status" {
				value.LogicalBytes, value.Period.Reads, value.Period.Writes = 0, 0, 0
			} // D1 is not TerraBase usage.
			meter.values[meterID] = value
		}
		a.Organizations[org.ID] = org
		return nil
	}); err != nil {
		t.Fatal(err)
	}
	if err := subscriptions.ConfigureInternalBilling(t.Context(), store, p, subscriptions.InternalBillingConfig{
		OrganizationID: p.OrganizationID, BasePlanKey: "business", SpendCapCents: 840, WriteCentsPerMillion: 100, StorageCentsPerGiBMonth: 20,
	}); err != nil {
		t.Fatal(err)
	}
	return Service{deps: core.Deps{Store: store, Usage: meter}}, p, meter
}

func TestInternalBillAggregatesServicesAndOnlyReportsCap(t *testing.T) {
	service, p, meter := internalUsageFixture(t, memstore.New())
	before, err := service.deps.Store.Organization(t.Context(), p.OrganizationID)
	if err != nil {
		t.Fatal(err)
	}
	for range 2 {
		bill, err := service.CheckInternalBilling(t.Context(), p, p.OrganizationID, "user-token")
		if err != nil {
			t.Fatal(err)
		}
		if !bill.Available || !bill.ReportOnly || !bill.CapReached || bill.Reads != 42 || bill.Writes != 1_200_006 || bill.LogicalBytes != 12<<30 || bill.BaseCents != 500 || bill.WriteCents != 120 || bill.StorageCents != 220 || bill.TotalCents != 840 {
			t.Fatalf("bill = %+v", bill)
		}
	}
	after, err := service.deps.Store.SnapshotAs(t.Context(), p)
	if err != nil {
		t.Fatal(err)
	}
	if !reflect.DeepEqual(before, after.Organizations[p.OrganizationID]) || len(after.Notifications) != 1 || !after.Notifications[0].Unread() {
		t.Fatalf("cap changed entitlement/resources or duplicated alerts: notifications=%+v", after.Notifications)
	}
	for _, scope := range meter.scopes {
		if scope.ProjectID == "" || scope.Granularity == "day" && scope.From.Day() != 1 {
			t.Fatalf("invalid usage scope: %+v", scope)
		}
	}
	flow := subscriptions.NewBillingService(service.deps.Store)
	if _, err := flow.BeginDemoCheckout(t.Context(), p, "starter", "monthly", "/organization"); !errors.Is(err, core.ErrForbidden) {
		t.Fatalf("internal checkout = %v", err)
	}
	if err := flow.ProjectCustomerState(t.Context(), payments.CustomerState{OrganizationID: p.OrganizationID, CustomerID: "cus_fake"}, nil); !errors.Is(err, core.ErrForbidden) {
		t.Fatalf("Stripe projection = %v", err)
	}
}

func TestInternalBillRejectsPartialUsageAndCannotEnableEnforcement(t *testing.T) {
	service, p, meter := internalUsageFixture(t, memstore.New())
	_, meter.fail = (core.Project{ID: p.ProjectID}).TerraBaseIDs(p.OrganizationID)
	bill, err := service.CheckInternalBilling(t.Context(), p, p.OrganizationID, "user-token")
	if !errors.Is(err, core.ErrUnavailable) || bill.Available || bill.CapReached {
		t.Fatalf("partial bill = %+v, %v", bill, err)
	}
	meter.fail = ""
	paidExcess := false
	if err := service.SetSpendingControls(t.Context(), p, spendingControls{PaidExcess: &paidExcess}); err != nil {
		t.Fatal(err)
	}
	org, err := service.deps.Store.Organization(t.Context(), p.OrganizationID)
	if err != nil || !org.Billing.Internal || !org.Billing.PaidExcessEnabled {
		t.Fatalf("internal policy changed: %+v, %v", org.Billing, err)
	}
	account, err := service.deps.Store.SnapshotAs(t.Context(), p)
	if err != nil || len(account.Notifications) != 0 {
		t.Fatalf("partial report generated alert: %+v, %v", account.Notifications, err)
	}
	if report, err := service.Usage(t.Context(), p, "", "", ""); err != nil || report.InternalBill.Available {
		t.Fatalf("tokenless bill = %+v, %v", report.InternalBill, err)
	}
}

func TestInternalBillUsesExactCentsAndRejectsOverflow(t *testing.T) {
	for _, tc := range []struct {
		units, rate, scale, want int64
		ok                       bool
	}{
		{1, 100, 1_000_000, 1, true}, {1_000_000, 100, 1_000_000, 100, true},
		{math.MaxInt64, 0, 1, 0, true}, {math.MaxInt64, math.MaxInt64, 1, 0, false}, {-1, 100, 1, 0, false},
	} {
		got, ok := estimateCharge(tc.units, tc.rate, tc.scale)
		if ok != tc.ok || ok && got != tc.want {
			t.Fatalf("charge %+v = %d, %v", tc, got, ok)
		}
	}
	report := usageReport{Allowances: []allowanceUsage{{Measured: true}, {Measured: true, Used: 1}, {Measured: true}}}
	bill := computeInternalBill(core.Plan{ID: core.NewULID(), PriceCents: math.MaxInt64, WriteCentsPerMillion: 100}, report, core.UsagePeriod{})
	if bill.Available || bill.CapReached {
		t.Fatalf("overflow bill = %+v", bill)
	}
}

func TestInternalBillKeepsTheMeteredMonthAcrossMidnight(t *testing.T) {
	period := core.UsagePeriod{
		From: time.Date(2026, 10, 1, 0, 0, 0, 0, time.UTC),
		To:   time.Date(2026, 10, 31, 23, 59, 59, 0, time.UTC),
	}
	report := usageReport{SpendCapCents: 500, Allowances: []allowanceUsage{{Measured: true}, {Measured: true}, {Measured: true}}}
	bill := computeInternalBill(core.Plan{ID: core.NewULID(), PriceCents: 500}, report, period)
	if !bill.Available || !bill.CapReached || bill.PeriodStart != period.From || bill.PeriodEnd != period.To {
		t.Fatalf("bill changed the measured October window: %+v", bill)
	}
}

func TestInternalCapAlertPersistsAcrossRestart(t *testing.T) {
	if testing.Short() {
		t.Skip("starts pinned TerraBase release")
	}
	tb := terrabasetest.Start(t)
	db, _ := terrabasetest.Database(t, tb, "internal-cap")
	if err := terrabase.EnsureCollections(t.Context(), db); err != nil {
		t.Fatal(err)
	}
	store := terrabase.New(db)
	if err := store.SeedPlans(t.Context()); err != nil {
		t.Fatal(err)
	}
	service, p, meter := internalUsageFixture(t, store)
	bill, err := service.CheckInternalBilling(t.Context(), p, p.OrganizationID, "user-token")
	if err != nil {
		t.Fatal(err)
	}
	service.deps.Store, service.deps.Usage = terrabase.New(db), meter
	if _, err := service.CheckInternalBilling(t.Context(), p, p.OrganizationID, "user-token"); err != nil {
		t.Fatal(err)
	}
	a, err := service.deps.Store.SnapshotAs(t.Context(), p)
	if err != nil || len(a.Notifications) != 1 || !a.Organizations[p.OrganizationID].Billing.Internal {
		t.Fatalf("restart lost internal policy/alert: %+v, %v", a.Notifications, err)
	}
	if a.Notifications[0].BillingMonth != bill.PeriodStart.UTC().Format("2006-01") {
		t.Fatalf("restart lost alert billing month: %+v", a.Notifications[0])
	}
	// Notifications are globally keyed documents. The new owner's monthly
	// warning must not collide with the former owner's persisted warning.
	newOwner, err := service.deps.Store.CreateAccount(t.Context(), core.User{Name: "New owner", Email: "new-owner@example.test", PasswordHash: "unused"})
	if err != nil {
		t.Fatal(err)
	}
	if err := service.deps.Store.UpdateAs(t.Context(), p, func(account *core.Account) error {
		org := account.Organizations[p.OrganizationID]
		former := org.Members[p.UserID]
		former.Role = "member"
		org.Members[p.UserID] = former
		org.Members[newOwner.User.ID] = core.Member{UserID: newOwner.User.ID, Email: newOwner.User.Email, Name: newOwner.User.Name, Role: "owner", Status: "active"}
		org.OwnerUserID = newOwner.User.ID
		account.Organizations[org.ID] = org
		return nil
	}); err != nil {
		t.Fatal(err)
	}
	member, err := service.deps.Store.ScopedPrincipal(t.Context(), newOwner.Principal(), core.Scope{OrganizationID: p.OrganizationID})
	if err != nil {
		t.Fatal(err)
	}
	for range 2 {
		service.deps.Store = terrabase.New(db)
		if _, err := service.CheckInternalBilling(t.Context(), p, p.OrganizationID, "user-token"); err != nil {
			t.Fatal(err)
		}
	}
	current, err := service.deps.Store.SnapshotAs(t.Context(), member)
	if err != nil || len(current.Notifications) != 1 || current.Notifications[0].ID == a.Notifications[0].ID {
		t.Fatalf("new owner alert missing, duplicated or reused: %+v, %v", current.Notifications, err)
	}
}

func TestInternalCapAlertDedupesBillingMonthAcrossReadAndRollover(t *testing.T) {
	service, p, _ := internalUsageFixture(t, memstore.New())
	owner, err := service.deps.Store.SnapshotAs(t.Context(), p)
	if err != nil {
		t.Fatal(err)
	}
	org := owner.Organizations[p.OrganizationID]
	start := time.Date(2026, 10, 1, 0, 0, 0, 0, time.UTC)
	bill := &internalBill{PeriodStart: start, PeriodEnd: start.AddDate(0, 1, 0), SpendCapCents: org.Billing.SpendCapCents, Alert: "Internal spending cap reached"}
	before := time.Now().UTC()
	for range 2 {
		if err := service.recordInternalCapAlert(t.Context(), owner, org, bill); err != nil {
			t.Fatal(err)
		}
	}
	account, err := service.deps.Store.SnapshotAs(t.Context(), p)
	if err != nil || len(account.Notifications) != 1 {
		t.Fatalf("October ending November 1 duplicated alerts: %+v, %v", account.Notifications, err)
	}
	alert := account.Notifications[0]
	if alert.BillingMonth != "2026-10" || alert.CreatedAt.Before(before) || alert.CreatedAt.After(time.Now().UTC()) || !regexp.MustCompile(`^[0-7][0-9A-HJKMNP-TV-Z]{25}$`).MatchString(alert.ID) {
		t.Fatalf("alert must retain billing month, actual creation time and ULID: %+v", alert)
	}
	if err := service.deps.Store.UpdateAs(t.Context(), p, func(a *core.Account) error {
		a.Notifications[0].Status = "Read"
		return nil
	}); err != nil {
		t.Fatal(err)
	}
	if err := service.recordInternalCapAlert(t.Context(), owner, org, bill); err != nil {
		t.Fatal(err)
	}
	account, err = service.deps.Store.SnapshotAs(t.Context(), p)
	if err != nil || len(account.Notifications) != 1 || account.Notifications[0].Unread() || account.Notifications[0].ID != alert.ID {
		t.Fatalf("repeated October report replaced or unread the alert: %+v, %v", account.Notifications, err)
	}
	bill.PeriodStart, bill.PeriodEnd = bill.PeriodEnd, bill.PeriodEnd.AddDate(0, 1, 0)
	for range 2 {
		if err := service.recordInternalCapAlert(t.Context(), owner, org, bill); err != nil {
			t.Fatal(err)
		}
	}
	account, err = service.deps.Store.SnapshotAs(t.Context(), p)
	if err != nil || len(account.Notifications) != 2 || account.Notifications[1].BillingMonth != "2026-11" || !account.Notifications[1].Unread() || account.Notifications[1].ID == alert.ID {
		t.Fatalf("month rollover must create one new alert: %+v, %v", account.Notifications, err)
	}
}

func TestInternalCapAlertDedupesConcurrentReports(t *testing.T) {
	service, p, _ := internalUsageFixture(t, memstore.New())
	const reports = 12
	start := make(chan struct{})
	results := make(chan error, reports)
	for range reports {
		go func() {
			<-start
			_, err := service.CheckInternalBilling(t.Context(), p, p.OrganizationID, "user-token")
			results <- err
		}()
	}
	close(start)
	for range reports {
		if err := <-results; err != nil {
			t.Error(err)
		}
	}
	account, err := service.deps.Store.SnapshotAs(t.Context(), p)
	if err != nil || len(account.Notifications) != 1 {
		t.Fatalf("concurrent reports duplicated alerts: %+v, %v", account.Notifications, err)
	}
}
