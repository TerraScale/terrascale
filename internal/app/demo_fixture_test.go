package app

import (
	"reflect"
	"strings"
	"testing"

	"terrascale.tech/dashboard/internal/core"
	"terrascale.tech/dashboard/internal/platform/config"
)

func TestMarioDemoFixtureContainsScopedProductRecords(t *testing.T) {
	application, err := New(config.Config{Demo: true})
	if err != nil {
		t.Fatal(err)
	}
	account, ok := application.Store.FindByEmail(demoAccountEmail)
	if !ok {
		t.Fatal("Mario demo account is missing")
	}
	if !account.OnboardingCompleted || account.OnboardingStep != "complete" || !account.DemoSecurity.MFAEnabled {
		t.Fatal("Mario demo profile did not retain its completed, MFA-enabled state")
	}

	org, ok := account.Organizations[demoLabsOrganizationID]
	if !ok {
		t.Fatal("TerraScale Labs organization is missing")
	}
	if org.OwnerUserID != account.User.ID || org.PlanID != "plan_pro" || org.Billing.PlanKey != "pro" ||
		org.Billing.Interval != "monthly" || org.BillingStatus != "active" || len(org.Projects) != 3 ||
		len(org.Members) != 3 || len(org.Invitations) != 2 {
		t.Fatalf("TerraScale Labs fixture has incorrect ownership, plan, or record counts: %#v", org)
	}
	if account.ActiveOrganizationID != org.ID || account.ActiveProjectID != "prj_01JC9Q6W3M5N7P9R2T4V6X8ZA" {
		t.Fatal("Mario demo account does not open the core-platform project")
	}
	personalFound := false
	for _, organization := range account.Organizations {
		personalFound = personalFound || organization.Kind == "personal"
	}
	if !personalFound || len(account.Organizations) != 2 {
		t.Fatal("Mario's personal organization was not retained alongside TerraScale Labs")
	}

	projectsByName := make(map[string]core.Project, len(org.Projects))
	databaseCount := 0
	for _, project := range org.Projects {
		projectsByName[project.Name] = project
		databaseCount += len(project.Databases)
		if project.OrganizationID != org.ID || len(project.Regions) != 1 || !isDemoCatalogRegion(project.Region) || project.Regions[0] != project.Region {
			t.Fatalf("project %q has invalid organization or catalog region: %#v", project.Name, project)
		}
	}
	if databaseCount != 4 || projectsByName["core-platform"].Region != "SFO" ||
		projectsByName["edge-analytics"].Region != "FRA" || projectsByName["staging-sandbox"].Region != "GRU" {
		t.Fatalf("project/database region fixture mismatch: projects=%v databases=%d", projectsByName, databaseCount)
	}
	primary := projectsByName["core-platform"].Databases["db_01JC9Q8Y5N7P9R3T5V7X9Z2AC"]
	if primary.ProjectID != projectsByName["core-platform"].ID || primary.Name != "core-primary" || len(primary.Documents["orders"]) != 3 {
		t.Fatalf("core-primary fixture is incomplete: %#v", primary)
	}
	if len(primary.IndexRecommendations) != 1 {
		t.Fatalf("core-primary index recommendations = %d, want 1", len(primary.IndexRecommendations))
	}
	recommendation := primary.IndexRecommendations[0]
	if recommendation.DatabaseID != primary.ID || recommendation.Collection != "orders" ||
		len(recommendation.Fields) != 1 || recommendation.Fields[0] != "total" || recommendation.State != "recommended" {
		t.Fatalf("index recommendation is not scoped to seeded orders data: %#v", recommendation)
	}
	if len(projectsByName["core-platform"].SavedQueries) != 2 {
		t.Fatal("core-platform does not have both saved queries")
	}

	apiKeyCount, m2mCount, revokedCount := 0, 0, 0
	for _, project := range org.Projects {
		for _, key := range project.APIKeys {
			if key.ProjectID != project.ID || key.SecretHash != "" || !strings.Contains(key.Prefix, "demo") {
				t.Fatalf("seeded credential is improperly scoped or usable: %#v", key)
			}
			switch key.Kind {
			case "api-key":
				apiKeyCount++
			case "m2m":
				m2mCount++
			}
			if !key.RevokedAt.IsZero() {
				revokedCount++
			}
		}
	}
	if apiKeyCount != 3 || m2mCount != 2 || revokedCount != 1 {
		t.Fatalf("credential counts = api keys %d, M2M %d, revoked %d", apiKeyCount, m2mCount, revokedCount)
	}
	if len(account.Notifications) != 4 || len(account.AuditEvents) != 5 {
		t.Fatalf("notification/audit counts = %d/%d, want 4/5", len(account.Notifications), len(account.AuditEvents))
	}
	for _, event := range account.AuditEvents {
		if event.OrganizationID != org.ID {
			t.Fatalf("audit event has wrong organization scope: %#v", event)
		}
	}
	if len(org.Billing.Invoices) != 14 {
		t.Fatalf("invoice count = %d, want 14", len(org.Billing.Invoices))
	}
	invoiceStatuses := map[string]int{}
	for _, invoice := range org.Billing.Invoices {
		invoiceStatuses[invoice.Status]++
		if invoice.PeriodStart.IsZero() || invoice.PeriodEnd.IsZero() || invoice.HostedURL != "" || invoice.PDFURL != "" {
			t.Fatalf("invoice has missing period or invented provider URL: %#v", invoice)
		}
	}
	if invoiceStatuses["paid"] != 12 || invoiceStatuses["open"] != 1 || invoiceStatuses["void"] != 1 {
		t.Fatalf("invoice status counts = %v, want 12 paid, 1 open, 1 void", invoiceStatuses)
	}

	assertPlan := func(id string, price, annual int64) {
		t.Helper()
		plan, ok := application.Store.Plan(id)
		if !ok || plan.PriceCents != price || plan.AnnualPriceCents != annual {
			t.Fatalf("plan %q = %#v, want monthly %d and annual %d cents", id, plan, price, annual)
		}
	}
	assertPlan("plan_starter", 500, 5000)
	assertPlan("plan_pro", 5000, 50000)
	assertPlan("plan_business", 9900, 99000)
	if _, exists := application.Store.Plan("plan_scale"); exists {
		t.Fatal("legacy Scale fixture plan was added to the canonical checkout catalog")
	}
}

func TestMarioFixtureDoesNotChangeFreshAccountState(t *testing.T) {
	store := core.NewStore()
	fresh, err := store.CreateAccount(core.User{
		Name: "Fresh account", Email: "fresh@example.test", PasswordHash: "test-only-hash",
	})
	if err != nil {
		t.Fatal(err)
	}
	before, ok := store.Snapshot(fresh.ID)
	if !ok {
		t.Fatal("fresh account was not stored")
	}
	if err := seedDemoAccount(store); err != nil {
		t.Fatal(err)
	}
	after, ok := store.Snapshot(fresh.ID)
	if !ok || !reflect.DeepEqual(after, before) {
		t.Fatal("seeding Mario changed another account's state")
	}
	if after.ActiveOrganizationID != before.ActiveOrganizationID || after.ActiveProjectID != before.ActiveProjectID ||
		after.OnboardingCompleted || after.OnboardingStep != "payment-method" {
		t.Fatal("fresh account onboarding state was replaced by demo setup")
	}
}

func isDemoCatalogRegion(region string) bool {
	switch region {
	case "GRU", "SFO", "FRA":
		return true
	default:
		return false
	}
}
