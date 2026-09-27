package subscriptions

import (
	"context"
	"slices"
	"strings"
	"testing"
	"time"

	"terrascale.tech/dashboard/internal/core"
)

// billingFixture is one Personal organization owner and its billing service.
type billingFixture struct {
	store     *core.Store
	service   BillingService
	principal core.Principal
}

func newBillingFixture(t *testing.T) billingFixture {
	t.Helper()
	store := core.NewStore()
	account, err := store.CreateAccount(core.User{Name: "Owner", Email: "billing-page@example.test", PasswordHash: "unused"})
	if err != nil {
		t.Fatal(err)
	}
	return billingFixture{store: store, service: NewBillingService(store), principal: account.Principal()}
}

// subscribe completes a local demo checkout for the plan.
func (fixture billingFixture) subscribe(t *testing.T, planKey, interval string) {
	t.Helper()
	session, err := fixture.service.BeginDemoCheckout(fixture.principal, planKey, interval, "/billing")
	if err != nil {
		t.Fatal(err)
	}
	if _, err := fixture.service.CompleteDemoCheckout(fixture.principal, session.ID, false); err != nil {
		t.Fatal(err)
	}
}

func (fixture billingFixture) page(t *testing.T, principal core.Principal, interval string) subscriptionsPage {
	t.Helper()
	view, err := fixture.service.Snapshot(fixture.principal)
	if err != nil {
		t.Fatal(err)
	}
	return billingPage(fixture.store, principal, view, interval)
}

func (page subscriptionsPage) card(t *testing.T, key string) planView {
	t.Helper()
	index := slices.IndexFunc(page.Plans, func(plan planView) bool { return plan.Key == key })
	if index < 0 {
		t.Fatalf("no %s plan card in %#v", key, page.Plans)
	}
	return page.Plans[index]
}

func renderBillingPage(t *testing.T, page subscriptionsPage) string {
	t.Helper()
	var html strings.Builder
	if err := subscriptionsContent("csrf-token", page).Render(context.Background(), &html); err != nil {
		t.Fatal(err)
	}
	return html.String()
}

func TestPlanCardsListEveryAllowance(t *testing.T) {
	fixture := newBillingFixture(t)
	starter := fixture.page(t, fixture.principal, intervalMonthly).card(t, "starter")
	for _, want := range []string{"5 GB-region storage", "1M operations/month", "5 databases", "5 team members"} {
		if !slices.Contains(starter.Features, want) {
			t.Errorf("Starter features %q are missing %q", starter.Features, want)
		}
	}
}

func TestPlanCardsOfferTrialOnlyWhileCheckoutGrantsOne(t *testing.T) {
	fixture := newBillingFixture(t)
	if features := fixture.page(t, fixture.principal, intervalMonthly).card(t, "pro").Features; !slices.Contains(features, "30-day free trial") {
		t.Fatalf("a new organization's Pro card %q does not offer the trial", features)
	}
	fixture.subscribe(t, "starter", intervalMonthly)
	for _, card := range fixture.page(t, fixture.principal, intervalMonthly).Plans {
		if slices.Contains(card.Features, "30-day free trial") {
			t.Errorf("subscribed organization's %s card still offers a trial: %q", card.Name, card.Features)
		}
	}
}

func TestAnnualPlanCardsRoundTheMonthlyEquivalent(t *testing.T) {
	fixture := newBillingFixture(t)
	page := fixture.page(t, fixture.principal, intervalAnnual)
	for key, want := range map[string]string{"starter": "$4.17", "pro": "$41.67", "business": "$82.50"} {
		if got := page.card(t, key).Price; got != want {
			t.Errorf("%s annual monthly price = %s, want %s", key, got, want)
		}
	}
}

func TestBillingPageShowsTheScheduledChangeAndLocksOtherPlans(t *testing.T) {
	fixture := newBillingFixture(t)
	fixture.subscribe(t, "pro", intervalMonthly)
	renewal := time.Date(2026, time.October, 27, 0, 0, 0, 0, time.UTC)
	if err := fixture.service.ScheduleDemoChange(fixture.principal, "starter", intervalMonthly, renewal); err != nil {
		t.Fatal(err)
	}

	page := fixture.page(t, fixture.principal, intervalMonthly)
	if page.Change.PlanName != "Starter" || page.Change.Pending || page.Change.Date != "October 27, 2026" {
		t.Fatalf("scheduled change = %#v", page.Change)
	}
	if pro := page.card(t, "pro"); !pro.Current || pro.Locked {
		t.Errorf("current Pro card = %#v", pro)
	}
	if starter := page.card(t, "starter"); !starter.Upcoming || starter.Badge != "Scheduled" || !starter.Locked {
		t.Errorf("scheduled Starter card = %#v", starter)
	}
	if business := page.card(t, "business"); business.Upcoming || !business.Locked {
		t.Errorf("Business card should be locked while a change is scheduled: %#v", business)
	}
	html := renderBillingPage(t, page)
	for _, want := range []string{"Change scheduled", "Pro stays active until October 27, 2026", "moves to Starter, billed monthly", `action="/billing/change/undo"`} {
		if !strings.Contains(html, want) {
			t.Errorf("billing page is missing %q", want)
		}
	}
}

func TestBillingPageShowsAPendingUpgrade(t *testing.T) {
	fixture := newBillingFixture(t)
	fixture.subscribe(t, "starter", intervalMonthly)
	// Stripe reports an upgrade awaiting payment as the pending plan.
	if err := fixture.store.UpdateAs(fixture.principal, func(account *core.Account) error {
		org := account.Organizations[fixture.principal.OrganizationID]
		org.Billing.PendingPlanKey, org.Billing.PendingInterval = "business", intervalAnnual
		account.Organizations[org.ID] = org
		return nil
	}); err != nil {
		t.Fatal(err)
	}
	page := fixture.page(t, fixture.principal, intervalAnnual)
	if business := page.card(t, "business"); !business.Upcoming || business.Badge != "Awaiting payment" || !business.Locked {
		t.Errorf("pending Business card = %#v", business)
	}
	html := renderBillingPage(t, page)
	if !strings.Contains(html, "Stripe is confirming the change to Business, billed annually") || strings.Contains(html, "/billing/change/undo") {
		t.Errorf("pending upgrade notice is wrong:\n%s", html)
	}
}

func TestBillingPageForAnOrganizationAwaitingItsFirstPayment(t *testing.T) {
	fixture := newBillingFixture(t)
	if err := fixture.store.UpdateAs(fixture.principal, func(account *core.Account) error {
		org := account.Organizations[fixture.principal.OrganizationID]
		org.Kind, org.PlanID, org.BillingStatus = "business", "plan_starter", "billing_required"
		account.Organizations[org.ID] = org
		return nil
	}); err != nil {
		t.Fatal(err)
	}
	page := fixture.page(t, fixture.principal, intervalMonthly)
	if !page.AwaitingPayment || page.PlanName != "No plan" || page.ShowPortal {
		t.Fatalf("awaiting page = %#v", page)
	}
	for _, card := range page.Plans {
		if card.Current || card.Action != "Choose "+card.Name {
			t.Errorf("%s card = %#v, want a Choose action", card.Name, card)
		}
	}
	if html := renderBillingPage(t, page); !strings.Contains(html, "can’t be created in") {
		t.Error("billing page does not explain that the organization needs a plan")
	}
}

func TestBillingPageHidesOwnerActionsFromMembers(t *testing.T) {
	fixture := newBillingFixture(t)
	fixture.subscribe(t, "starter", intervalMonthly)
	member := fixture.principal
	member.UserID = "usr_member"

	owner := fixture.page(t, fixture.principal, intervalMonthly)
	if !owner.CanManage || !owner.ShowPortal {
		t.Fatalf("owner page = %#v", owner)
	}
	html := renderBillingPage(t, fixture.page(t, member, intervalMonthly))
	for _, owned := range []string{`href="/billing/portal"`, `action="/usage"`, `action="/billing/checkout"`} {
		if strings.Contains(html, owned) {
			t.Errorf("member billing page offers owner action %q", owned)
		}
	}
	if !strings.Contains(html, "Only the organization owner can change the plan.") {
		t.Error("member billing page does not say who can change the plan")
	}
}

func TestBillingPageLinksThePortalOnlyWithABillingAccount(t *testing.T) {
	fixture := newBillingFixture(t)
	if page := fixture.page(t, fixture.principal, intervalMonthly); page.ShowPortal {
		t.Fatal("Free organization without a billing account links the billing portal")
	}
	if err := fixture.store.UpdateAs(fixture.principal, func(account *core.Account) error {
		org := account.Organizations[fixture.principal.OrganizationID]
		org.Billing.CustomerID = "cus_canceled"
		account.Organizations[org.ID] = org
		return nil
	}); err != nil {
		t.Fatal(err)
	}
	if page := fixture.page(t, fixture.principal, intervalMonthly); !page.ShowPortal {
		t.Fatal("organization with a Stripe customer cannot open the billing portal")
	}
}
