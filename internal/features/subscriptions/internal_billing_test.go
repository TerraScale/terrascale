package subscriptions

import (
	"context"
	"errors"
	"reflect"
	"regexp"
	"testing"

	"terrascale.tech/dashboard/internal/adapters/terrabase"
	"terrascale.tech/dashboard/internal/core"
	"terrascale.tech/dashboard/internal/core/memstore"
	"terrascale.tech/dashboard/internal/features/admin"
	"terrascale.tech/dashboard/internal/platform/terrabasetest"
	sdk "terrascale.tech/sdk/v1"
)

func internalBillingFixture(t *testing.T, store core.Store) (core.Principal, core.Account, core.Account, InternalBillingConfig) {
	t.Helper()
	administrator, err := store.CreateAccount(t.Context(), core.User{Name: "Admin", Email: "internal-admin@example.test", PasswordHash: "unused", PlatformAdmin: true})
	if err != nil {
		t.Fatal(err)
	}
	owner, err := core.NewAccount(core.User{Name: "Owner", Email: "internal-owner@example.test", PasswordHash: "unused"})
	if err != nil {
		t.Fatal(err)
	}
	org := owner.Organizations[owner.ActiveOrganizationID]
	org.Name, org.Kind = "terrascale", "business"
	org.PlanID = "09XCWRH05BWWBADSBPTPDMFA7D"
	for _, name := range []string{"ts-dashboard", "ts-auth", "ts-router", "ts-status", "ts-orchestrator", "ku", "ts-reviewer"} {
		project := core.Project{
			ID: core.NewID(), OrganizationID: org.ID, Name: name, Status: "active", SpendCapCents: 123,
			Databases: map[string]core.Database{}, APIKeys: map[string]core.APIKey{},
		}
		db := core.Database{
			ID: core.NewID(), ProjectID: project.ID, Name: "main", Status: "ready", SpendCapCents: 45,
			Documents: map[string]map[string][]byte{"records": {"record": []byte(`{"value":"keep"}`)}},
		}
		project.Databases[db.ID] = db
		key := core.APIKey{ID: core.NewID(), ProjectID: project.ID, Name: "existing", SecretHash: "keep"}
		project.APIKeys[key.ID] = key
		org.Projects[project.ID] = project
	}
	owner.Organizations[org.ID] = org
	if err := store.InsertAccount(t.Context(), owner); err != nil {
		t.Fatal(err)
	}
	other, err := store.CreateAccount(t.Context(), core.User{Name: "Customer", Email: "internal-customer@example.test", PasswordHash: "unused"})
	if err != nil {
		t.Fatal(err)
	}
	return administrator.Principal(), owner, other, InternalBillingConfig{
		OrganizationID: org.ID, BasePlanKey: "business", SpendCapCents: 25_000,
		WriteCentsPerMillion: 120, StorageCentsPerGiBMonth: 35,
	}
}

func TestConfigureInternalBillingClonesPlanAndPreservesData(t *testing.T) {
	for _, zeroRates := range []bool{false, true} {
		t.Run(map[bool]string{false: "explicit-rates", true: "zero-rates"}[zeroRates], func(t *testing.T) {
			store := memstore.New()
			principal, owner, _, config := internalBillingFixture(t, store)
			base, err := store.Plan(t.Context(), config.BasePlanKey)
			if err != nil {
				t.Fatal(err)
			}
			base.PriceCents, base.AnnualPriceCents, base.TrialDays = 12_345, 123_450, 14
			base.StorageMiB, base.MonthlyRequests, base.OperationsPerMonth, base.StorageLabel = 2345, 67_890, 12_345, "existing allowance"
			base.MaxDatabases, base.MaxMembers = 7, 11
			base.WriteCentsPerMillion, base.StorageCentsPerGiBMonth = 999, 888
			if err := store.UpsertPlanAs(t.Context(), principal, base); err != nil {
				t.Fatal(err)
			}
			if zeroRates {
				config.WriteCentsPerMillion, config.StorageCentsPerGiBMonth = 0, 0
			}
			want := store.Export()
			if err := ConfigureInternalBilling(t.Context(), store, principal, config); err != nil {
				t.Fatal(err)
			}
			internal, err := store.Plan(t.Context(), "internal")
			if err != nil || !regexp.MustCompile(`^[0-7][0-9A-HJKMNP-TV-Z]{25}$`).MatchString(internal.ID) {
				t.Fatalf("internal plan ID = %q, %v", internal.ID, err)
			}
			plan := base
			plan.ID = internal.ID
			plan.Key, plan.Name, plan.Visible = "internal", "TerraScale Internal", false
			plan.WriteCentsPerMillion, plan.StorageCentsPerGiBMonth = config.WriteCentsPerMillion, config.StorageCentsPerGiBMonth
			want.Plans[plan.ID] = plan
			org := want.Organizations[config.OrganizationID]
			org.PlanID, org.Status, org.BillingStatus = plan.ID, "active", "active"
			org.Billing.Internal = true
			org.Billing.Status, org.Billing.PlanKey, org.Billing.Interval = "active", plan.Key, "monthly"
			org.Billing.PaidExcessEnabled, org.Billing.SpendCapCents = true, config.SpendCapCents
			want.Organizations[org.ID] = org
			if err := ConfigureInternalBilling(t.Context(), store, principal, config); err != nil {
				t.Fatal(err)
			}
			if got := store.Export(); !reflect.DeepEqual(got, want) {
				t.Fatal("configuration did not preserve the source plan, other accounts, identities, projects, keys, data and lower-level caps")
			}
			if err := ConfigureInternalBilling(t.Context(), store, principal, config); err != nil {
				t.Fatal(err)
			}
			if got := store.Export(); !reflect.DeepEqual(got, want) {
				t.Fatal("repeating the configuration changed stored state")
			}
			view, err := NewBillingService(store).Snapshot(t.Context(), owner.Principal())
			if err != nil {
				t.Fatal(err)
			}
			if view.Plan != plan {
				t.Fatalf("assigned plan = %+v, want %+v", view.Plan, plan)
			}
			for _, available := range view.Plans {
				if available.Key == "internal" {
					t.Fatal("internal plan is available in the customer catalog")
				}
			}
		})
	}
}

func TestConfigureInternalBillingRejectsInvalidRequests(t *testing.T) {
	for _, test := range []struct {
		name   string
		change func(*core.Principal, *InternalBillingConfig, core.Account, core.Account)
		want   error
	}{
		{"unauthenticated", func(p *core.Principal, _ *InternalBillingConfig, _, _ core.Account) { *p = core.Principal{} }, core.ErrUnauthorized},
		{"nonadmin-owner", func(p *core.Principal, _ *InternalBillingConfig, owner, _ core.Account) { *p = owner.Principal() }, core.ErrForbidden},
		{"forged-admin", func(p *core.Principal, _ *InternalBillingConfig, owner, _ core.Account) {
			*p = owner.Principal()
			p.PlatformAdmin = true
		}, core.ErrForbidden},
		{"wrong-user", func(p *core.Principal, _ *InternalBillingConfig, _, _ core.Account) { p.UserID = "another-user" }, core.ErrForbidden},
		{"stale-credentials", func(p *core.Principal, _ *InternalBillingConfig, _, _ core.Account) { p.AuthVersion++ }, core.ErrUnauthorized},
		{"other-org", func(_ *core.Principal, c *InternalBillingConfig, _, other core.Account) {
			c.OrganizationID = other.ActiveOrganizationID
		}, core.ErrForbidden},
		{"name-is-not-id", func(_ *core.Principal, c *InternalBillingConfig, _, _ core.Account) { c.OrganizationID = "terrascale" }, core.ErrNotFound},
		{"missing-org", func(_ *core.Principal, c *InternalBillingConfig, _, _ core.Account) { c.OrganizationID = "" }, core.ErrInvalid},
		{"missing-base", func(_ *core.Principal, c *InternalBillingConfig, _, _ core.Account) { c.BasePlanKey = "" }, core.ErrInvalid},
		{"unknown-base", func(_ *core.Principal, c *InternalBillingConfig, _, _ core.Account) { c.BasePlanKey = "missing" }, core.ErrNotFound},
		{"free-base", func(_ *core.Principal, c *InternalBillingConfig, _, _ core.Account) { c.BasePlanKey = "free" }, core.ErrInvalid},
		{"base-id-not-key", func(_ *core.Principal, c *InternalBillingConfig, _, _ core.Account) {
			c.BasePlanKey = "09XCWRH05BWWBADSBPTPDMFA7D"
		}, core.ErrInvalid},
		{"zero-cap", func(_ *core.Principal, c *InternalBillingConfig, _, _ core.Account) { c.SpendCapCents = 0 }, core.ErrInvalid},
		{"negative-cap", func(_ *core.Principal, c *InternalBillingConfig, _, _ core.Account) { c.SpendCapCents = -1 }, core.ErrInvalid},
		{"negative-writes", func(_ *core.Principal, c *InternalBillingConfig, _, _ core.Account) { c.WriteCentsPerMillion = -1 }, core.ErrInvalid},
		{"negative-storage", func(_ *core.Principal, c *InternalBillingConfig, _, _ core.Account) { c.StorageCentsPerGiBMonth = -1 }, core.ErrInvalid},
	} {
		t.Run(test.name, func(t *testing.T) {
			store := memstore.New()
			principal, owner, other, config := internalBillingFixture(t, store)
			test.change(&principal, &config, owner, other)
			before := store.Export()
			if err := ConfigureInternalBilling(t.Context(), store, principal, config); !errors.Is(err, test.want) {
				t.Fatalf("configuration error = %v, want %v", err, test.want)
			}
			if !reflect.DeepEqual(store.Export(), before) {
				t.Fatal("rejected configuration changed stored state")
			}
		})
	}
}

func TestConfigureInternalBillingRejectsInactiveOrStripeOrganizations(t *testing.T) {
	for _, test := range []struct {
		name   string
		change func(*core.Organization)
		want   error
	}{
		{"case-sensitive-name", func(o *core.Organization) { o.Name = "TerraScale" }, core.ErrForbidden},
		{"inactive", func(o *core.Organization) { o.Status = "suspended" }, core.ErrConflict},
		{"archived", func(o *core.Organization) { o.Archived = true }, core.ErrConflict},
		{"deletion-requested", func(o *core.Organization) { o.DeletionRequested = true }, core.ErrConflict},
		{"missing-service-project", func(o *core.Organization) {
			for id, project := range o.Projects {
				if project.Name == "ku" {
					delete(o.Projects, id)
				}
			}
		}, core.ErrConflict},
		{"inactive-service-project", func(o *core.Organization) {
			for id, project := range o.Projects {
				if project.Name == "ku" {
					project.Status = "suspended"
					o.Projects[id] = project
				}
			}
		}, core.ErrConflict},
		{"customer", func(o *core.Organization) { o.Billing.CustomerID = "cus_real" }, core.ErrConflict},
		{"subscription", func(o *core.Organization) { o.Billing.SubscriptionID = "sub_real" }, core.ErrConflict},
		{"checkout", func(o *core.Organization) { o.Billing.CheckoutID = "cs_real" }, core.ErrConflict},
		{"schedule", func(o *core.Organization) { o.Billing.ScheduleID = "sched_real" }, core.ErrConflict},
		{"pending-plan", func(o *core.Organization) { o.Billing.PendingPlanKey = "pro" }, core.ErrConflict},
		{"scheduled-plan", func(o *core.Organization) { o.Billing.ScheduledPlanKey = "pro" }, core.ErrConflict},
		{"checkout-history", func(o *core.Organization) {
			o.CheckoutSessions["cs_real"] = core.CheckoutSession{ID: "cs_real", PlanID: "09XCWRH05BWWBADSBPTPDMFA7D", Status: "complete"}
		}, core.ErrConflict},
	} {
		t.Run(test.name, func(t *testing.T) {
			store := memstore.New()
			principal, _, _, config := internalBillingFixture(t, store)
			if err := store.UpdateOrganization(t.Context(), config.OrganizationID, func(o *core.Organization, _ map[string]core.Plan) error {
				test.change(o)
				return nil
			}); err != nil {
				t.Fatal(err)
			}
			before := store.Export()
			if err := ConfigureInternalBilling(t.Context(), store, principal, config); !errors.Is(err, test.want) {
				t.Fatalf("configuration error = %v, want %v", err, test.want)
			}
			if !reflect.DeepEqual(store.Export(), before) {
				t.Fatal("rejected configuration changed billing or the plan catalog")
			}
		})
	}
}

func TestConfigureInternalBillingCannotMoveToAnotherOrganization(t *testing.T) {
	store := memstore.New()
	principal, _, other, config := internalBillingFixture(t, store)
	if err := ConfigureInternalBilling(t.Context(), store, principal, config); err != nil {
		t.Fatal(err)
	}
	if err := store.UpdateOrganization(t.Context(), other.ActiveOrganizationID, func(o *core.Organization, _ map[string]core.Plan) error {
		o.Name = "terrascale"
		return nil
	}); err != nil {
		t.Fatal(err)
	}
	before := store.Export()
	config.OrganizationID = other.ActiveOrganizationID
	if err := ConfigureInternalBilling(t.Context(), store, principal, config); !errors.Is(err, core.ErrConflict) {
		t.Fatalf("configuration error = %v, want conflict", err)
	}
	if !reflect.DeepEqual(store.Export(), before) {
		t.Fatal("attempt to move internal billing changed stored state")
	}
}

type checkoutDuringInternalBilling struct{ core.Store }

func (s checkoutDuringInternalBilling) UpsertPlanAndUpdateOrganizationAs(ctx context.Context, p core.Principal, id string, plan core.Plan, mutate func(*core.Organization) error) error {
	if err := s.Store.UpdateOrganization(ctx, id, func(org *core.Organization, _ map[string]core.Plan) error {
		org.Billing.CheckoutID = "concurrent-checkout"
		return nil
	}); err != nil {
		return err
	}
	return s.Store.UpsertPlanAndUpdateOrganizationAs(ctx, p, id, plan, mutate)
}

func TestConfigureInternalBillingRechecksCheckoutBeforeAssignment(t *testing.T) {
	store := memstore.New()
	principal, _, _, config := internalBillingFixture(t, store)
	if err := ConfigureInternalBilling(t.Context(), checkoutDuringInternalBilling{store}, principal, config); !errors.Is(err, core.ErrConflict) {
		t.Fatalf("configuration error = %v, want conflict", err)
	}
	org, err := store.Organization(t.Context(), config.OrganizationID)
	if err != nil {
		t.Fatal(err)
	}
	if org.Billing.Internal || org.PlanID != "09XCWRH05BWWBADSBPTPDMFA7D" || org.Billing.CheckoutID != "concurrent-checkout" {
		t.Fatalf("concurrent checkout was replaced: %+v", org.Billing)
	}
	if _, err := store.Plan(t.Context(), "internal"); !errors.Is(err, core.ErrNotFound) {
		t.Fatalf("rejected assignment published an internal plan: %v", err)
	}
}

func TestInternalEstimateRatesSurviveAdminPlanEdit(t *testing.T) {
	store := memstore.New()
	principal, _, _, config := internalBillingFixture(t, store)
	if err := ConfigureInternalBilling(t.Context(), store, principal, config); err != nil {
		t.Fatal(err)
	}
	plan, err := store.Plan(t.Context(), "internal")
	if err != nil {
		t.Fatal(err)
	}
	plan.Name = "Updated internal plan"
	plan.WriteCentsPerMillion, plan.StorageCentsPerGiBMonth = 0, 0
	saved, err := admin.NewService(store).SavePlan(t.Context(), principal, plan)
	if err != nil {
		t.Fatal(err)
	}
	if saved.WriteCentsPerMillion != config.WriteCentsPerMillion || saved.StorageCentsPerGiBMonth != config.StorageCentsPerGiBMonth {
		t.Fatalf("admin plan editor cleared estimate rates: %+v", saved)
	}
	persisted, err := store.Plan(t.Context(), saved.ID)
	if err != nil || persisted != saved {
		t.Fatalf("persisted edited plan = %+v, %v, want %+v", persisted, err, saved)
	}
}

func TestInternalBillingPersistsAcrossDashboardRestart(t *testing.T) {
	if testing.Short() {
		t.Skip("starts pinned TerraBase release")
	}
	server := terrabasetest.Start(t)
	db, key := terrabasetest.Database(t, server, "internal-billing")
	if err := terrabase.EnsureCollections(t.Context(), db); err != nil {
		t.Fatal(err)
	}
	store := terrabase.New(db)
	if err := store.SeedPlans(t.Context()); err != nil {
		t.Fatal(err)
	}
	principal, _, _, config := internalBillingFixture(t, store)
	if err := ConfigureInternalBilling(t.Context(), store, principal, config); err != nil {
		t.Fatal(err)
	}
	wantPlan, err := store.Plan(t.Context(), "internal")
	if err != nil {
		t.Fatal(err)
	}
	wantOrg, err := store.Organization(t.Context(), config.OrganizationID)
	if err != nil {
		t.Fatal(err)
	}
	client, err := sdk.New(sdk.Config{BaseURL: server.URL, Token: key, HTTPClient: server.Client})
	if err != nil {
		t.Fatal(err)
	}
	restarted := terrabase.New(client.Database(db.Project(), db.Name()))
	gotPlan, err := restarted.Plan(t.Context(), "internal")
	if err != nil || gotPlan != wantPlan {
		t.Fatalf("restarted plan = %+v, %v, want %+v", gotPlan, err, wantPlan)
	}
	gotOrg, err := restarted.Organization(t.Context(), config.OrganizationID)
	if err != nil || !reflect.DeepEqual(gotOrg, wantOrg) {
		t.Fatalf("restarted organization = %+v, %v, want %+v", gotOrg, err, wantOrg)
	}
	if err := ConfigureInternalBilling(t.Context(), restarted, principal, config); err != nil {
		t.Fatal(err)
	}
	gotOrg, err = restarted.Organization(t.Context(), config.OrganizationID)
	if err != nil || !reflect.DeepEqual(gotOrg, wantOrg) {
		t.Fatalf("repeated configuration after restart = %+v, %v, want %+v", gotOrg, err, wantOrg)
	}
}
