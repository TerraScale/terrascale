package usage

import (
	"context"
	"fmt"
	"net/http"
	"net/http/httptest"
	"strings"
	"testing"
	"time"

	"terrascale.tech/dashboard/internal/core"
	"terrascale.tech/dashboard/internal/core/memstore"
	"terrascale.tech/dashboard/internal/features/subscriptions"
)

type reviewSessions struct{ principal core.Principal }

func (sessions reviewSessions) Start(http.ResponseWriter, *http.Request, core.Principal) error {
	return nil
}
func (sessions reviewSessions) Principal(*http.Request) (core.Principal, bool) {
	return sessions.principal, true
}
func (sessions reviewSessions) BearerPrincipal(*http.Request) (core.Principal, bool) {
	return core.Principal{}, false
}
func (sessions reviewSessions) End(http.ResponseWriter, *http.Request) error     { return nil }
func (sessions reviewSessions) RevokeUser(context.Context, string, string) error { return nil }

// viewRenderer writes the feature view without the shell layout.
type viewRenderer struct{}

func (viewRenderer) Page(w http.ResponseWriter, r *http.Request, page core.Page) error {
	return page.View(page).Render(r.Context(), w)
}

// reviewFixture is a Personal organization owner on a demo Starter
// subscription with the usage page mounted.
type reviewFixture struct {
	store     core.Store
	billing   subscriptions.BillingService
	principal core.Principal
	mux       *http.ServeMux
}

func newReviewFixture(t *testing.T) reviewFixture {
	t.Helper()
	store := memstore.New()
	account, err := store.CreateAccount(t.Context(), core.User{Name: "Owner", Email: "plan-review@example.test", PasswordHash: "unused"})
	if err != nil {
		t.Fatal(err)
	}
	fixture := reviewFixture{store: store, billing: subscriptions.NewBillingService(store), principal: account.Principal(), mux: http.NewServeMux()}
	session, err := fixture.billing.BeginDemoCheckout(t.Context(), fixture.principal, "starter", "monthly", "/usage?tab=billing")
	if err != nil {
		t.Fatal(err)
	}
	if _, err := fixture.billing.CompleteDemoCheckout(t.Context(), fixture.principal, session.ID, false); err != nil {
		t.Fatal(err)
	}
	Mount(fixture.mux, core.Deps{Store: store, Sessions: reviewSessions{principal: fixture.principal}, Render: viewRenderer{}, Tools: core.NewTools(store), Demo: true}, nil)
	return fixture
}

func (fixture reviewFixture) review(t *testing.T, plan, interval string) string {
	t.Helper()
	response := httptest.NewRecorder()
	fixture.mux.ServeHTTP(response, httptest.NewRequest(http.MethodGet, "/usage?tab=billing&review=1&plan="+plan+"&interval="+interval, nil))
	if response.Code != http.StatusOK {
		t.Fatalf("review %s %s = %d: %s", plan, interval, response.Code, response.Body.String())
	}
	return response.Body.String()
}

func (fixture reviewFixture) update(t *testing.T, change func(*core.Organization)) {
	t.Helper()
	if err := fixture.store.UpdateAs(t.Context(), fixture.principal, func(account *core.Account) error {
		org := account.Organizations[fixture.principal.OrganizationID]
		change(&org)
		account.Organizations[org.ID] = org
		return nil
	}); err != nil {
		t.Fatal(err)
	}
}

func requireContains(t *testing.T, html string, want ...string) {
	t.Helper()
	for _, text := range want {
		if !strings.Contains(html, text) {
			t.Errorf("page is missing %q", text)
		}
	}
}

func requireOmits(t *testing.T, html string, unwanted ...string) {
	t.Helper()
	for _, text := range unwanted {
		if strings.Contains(html, text) {
			t.Errorf("page should not contain %q", text)
		}
	}
}

func TestPlanReviewShowsThePricePeriod(t *testing.T) {
	fixture := newReviewFixture(t)
	requireContains(t, fixture.review(t, "pro", "annual"), "US$ 500.00 / year", "Confirm change", "Demo billing doesn’t contact Stripe")
	requireContains(t, fixture.review(t, "business", "monthly"), "US$ 99.00 / month")
}

func TestPlanReviewWarnsWhenTheOrganizationExceedsTheTargetPlan(t *testing.T) {
	fixture := newReviewFixture(t)
	requireOmits(t, fixture.review(t, "free", ""), "More databases than Free allows")

	renewal := time.Date(2026, time.October, 27, 0, 0, 0, 0, time.UTC)
	fixture.update(t, func(org *core.Organization) {
		org.BillingRenewal = renewal
		for id, project := range org.Projects {
			project.Databases = map[string]core.Database{"db_one": {ID: "db_one", Name: "one"}, "db_two": {ID: "db_two", Name: "two"}}
			org.Projects[id] = project
		}
	})
	html := fixture.review(t, "free", "")
	requireContains(t, html, "More databases than Free allows", "Personal has 2 databases and Free allows 1.", "At renewal on Oct 27, 2026", "Confirm change")
}

func TestPlanReviewBlocksChangesWhileAChangeIsScheduled(t *testing.T) {
	fixture := newReviewFixture(t)
	renewal := time.Date(2026, time.October, 27, 0, 0, 0, 0, time.UTC)
	if err := fixture.billing.ScheduleDemoChange(t.Context(), fixture.principal, "free", "", renewal); err != nil {
		t.Fatal(err)
	}
	html := fixture.review(t, "business", "monthly")
	requireContains(t, html, "A change to Free is scheduled to take effect on Oct 27, 2026.", `action="/billing/change/undo"`, "Your plan will change to Free on Oct 27, 2026.")
	requireOmits(t, html, "Confirm change")
}

func TestPlanReviewNamesTheNextRenewalWhenTheScheduledDateIsUnknown(t *testing.T) {
	fixture := newReviewFixture(t)
	fixture.update(t, func(org *core.Organization) {
		org.Billing.ScheduledPlanKey, org.Billing.ScheduledInterval = "free", ""
	})
	html := fixture.review(t, "business", "monthly")
	requireContains(t, html, "A change to Free is scheduled to take effect at the next renewal.", "Your plan will change to Free at the next renewal.")
	requireOmits(t, html, "Jan 1, 1")
}

func TestPlanReviewBlocksChangesWhileAnUpgradeAwaitsPayment(t *testing.T) {
	fixture := newReviewFixture(t)
	fixture.update(t, func(org *core.Organization) {
		org.Billing.PendingPlanKey, org.Billing.PendingInterval = "pro", "monthly"
	})
	html := fixture.review(t, "business", "monthly")
	requireContains(t, html, "Stripe is still confirming the change to Pro.", "Stripe is confirming the change to Pro.")
	requireOmits(t, html, "Confirm change", `action="/billing/change/undo"`)
}

func TestPlanReviewBlocksChangesWhileAPaymentFails(t *testing.T) {
	fixture := newReviewFixture(t)
	fixture.update(t, func(org *core.Organization) {
		org.Billing.Status, org.BillingStatus = "past_due", "billing_required"
	})
	html := fixture.review(t, "pro", "monthly")
	requireContains(t, html, "Stripe reports a payment problem with this subscription.")
	requireOmits(t, html, "Confirm change")
}

func TestPlanReviewBlocksChangesWhileTheSubscriptionIsNotEntitled(t *testing.T) {
	fixture := newReviewFixture(t)
	fixture.update(t, func(org *core.Organization) {
		org.Billing.Status = "incomplete"
	})
	html := fixture.review(t, "pro", "monthly")
	requireContains(t, html, "Stripe reports a payment problem with this subscription.")
	requireOmits(t, html, "Confirm change")
}

func TestPlanReviewBlocksChangesWhileTheSubscriptionEndsAtRenewal(t *testing.T) {
	fixture := newReviewFixture(t)
	renewal := time.Date(2026, time.November, 3, 0, 0, 0, 0, time.UTC)
	fixture.update(t, func(org *core.Organization) {
		org.Billing.CancelAtPeriodEnd, org.BillingRenewal = true, renewal
	})
	html := fixture.review(t, "business", "monthly")
	requireContains(t, html, "This subscription ends on Nov 3, 2026. Resume it in Manage payment before changing plans.")
	requireOmits(t, html, "Confirm change")
}

func TestLimitWarningsCountMembersAndPendingInvitations(t *testing.T) {
	now := time.Date(2026, time.September, 27, 0, 0, 0, 0, time.UTC)
	starter := core.Plan{Name: "Starter", MaxDatabases: 5, MaxMembers: 5}
	org := core.Organization{
		Name: "Acme",
		Members: map[string]core.Member{
			"a": {Status: "active"}, "b": {Status: "active"}, "c": {Status: "active"}, "d": {Status: "removed"},
		},
		Invitations: map[string]core.Invitation{
			"x": {Status: "pending", ExpiresAt: now.Add(time.Hour)},
			"y": {Status: "pending", ExpiresAt: now.Add(time.Hour)},
			"z": {Status: "pending", ExpiresAt: now.Add(-time.Hour)},
		},
	}
	if warnings := limitWarnings(org, starter, now); len(warnings) != 0 {
		t.Fatalf("five seats on a five-member plan warned: %q", warnings)
	}
	org.Invitations["w"] = core.Invitation{Status: "pending", ExpiresAt: now.Add(time.Hour)}
	warnings := limitWarnings(org, starter, now)
	if len(warnings) != 1 || warnings[0].Title != "More members than Starter allows" || !strings.Contains(warnings[0].Detail, "Acme has 6 members and pending invitations and Starter allows 5.") {
		t.Fatalf("warnings = %q", warnings)
	}
	if warnings := limitWarnings(org, core.Plan{Name: "Free", MaxDatabases: 1}, now); len(warnings) != 0 {
		t.Fatalf("a plan without a member limit warned: %q", warnings)
	}
}

// unavailablePlans is a store whose plan lookups fail like a database outage.
type unavailablePlans struct{ core.Store }

func (unavailablePlans) Plan(context.Context, string) (core.Plan, error) {
	return core.Plan{}, fmt.Errorf("%w: database down", core.ErrUnavailable)
}

func TestUsagePageReportsAnUnavailableStore(t *testing.T) {
	fixture := newReviewFixture(t)
	mux := http.NewServeMux()
	store := unavailablePlans{fixture.store}
	Mount(mux, core.Deps{Store: store, Sessions: reviewSessions{principal: fixture.principal}, Render: viewRenderer{}, Tools: core.NewTools(store), Demo: true}, nil)
	for _, path := range []string{"/usage?tab=billing", "/usage?tab=billing&review=1&plan=pro&interval=monthly"} {
		response := httptest.NewRecorder()
		mux.ServeHTTP(response, httptest.NewRequest(http.MethodGet, path, nil))
		if response.Code != http.StatusServiceUnavailable {
			t.Fatalf("GET %s = %d, want %d: %s", path, response.Code, http.StatusServiceUnavailable, response.Body.String())
		}
	}
}
