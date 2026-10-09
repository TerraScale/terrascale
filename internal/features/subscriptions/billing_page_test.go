package subscriptions

import (
	"context"
	"slices"
	"strings"
	"testing"
	"time"

	"terrascale.tech/dashboard/internal/core"
	"terrascale.tech/dashboard/internal/core/memstore"
)

// billingFixture is one Personal organization owner and its billing service.
type billingFixture struct {
	store     core.Store
	service   BillingService
	principal core.Principal
}

func newBillingFixture(t *testing.T) billingFixture {
	t.Helper()
	store := memstore.New()
	account, err := store.CreateAccount(t.Context(), core.User{Name: "Owner", Email: "billing-page@example.test", PasswordHash: "unused"})
	if err != nil {
		t.Fatal(err)
	}
	return billingFixture{store: store, service: NewBillingService(store), principal: account.Principal()}
}

// subscribe completes a local demo checkout for the plan.
func (fixture billingFixture) subscribe(t *testing.T, planKey, interval string) {
	t.Helper()
	session, err := fixture.service.BeginDemoCheckout(t.Context(), fixture.principal, planKey, interval, "/billing")
	if err != nil {
		t.Fatal(err)
	}
	if _, err := fixture.service.CompleteDemoCheckout(t.Context(), fixture.principal, session.ID, false); err != nil {
		t.Fatal(err)
	}
}

func (fixture billingFixture) page(t *testing.T, principal core.Principal, interval string) subscriptionsPage {
	t.Helper()
	view, err := fixture.service.Snapshot(t.Context(), fixture.principal)
	if err != nil {
		t.Fatal(err)
	}
	page, err := billingPage(t.Context(), fixture.store, principal, view, interval)
	if err != nil {
		t.Fatal(err)
	}
	return page
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
	for _, want := range []string{"5 GB-region storage", "1M reads/month", "1M writes/month", "5 databases", "5 team members"} {
		if !slices.Contains(starter.Features, want) {
			t.Errorf("Starter features %q are missing %q", starter.Features, want)
		}
	}
}

func TestPlanCardsOmitUnpublishedAllowances(t *testing.T) {
	plan := core.Plan{Key: "custom", Name: "Custom", PriceCents: 1000}
	card := makePlanView(plan, 0, planCardContext{Interval: intervalMonthly})
	if len(card.Features) != 0 {
		t.Fatalf("plan with unpublished allowances has features %q", card.Features)
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

func TestAnnualPlanActionUsesDisplayedMonthlyEquivalent(t *testing.T) {
	current := planCardContext{
		Interval: intervalAnnual, Key: "current", BillingInterval: intervalAnnual,
		PriceCents: 10_000, AnnualPriceCents: 60_000,
	}
	plan := core.Plan{
		Key: "target", Name: "Target", PriceCents: 12_000, AnnualPriceCents: 48_000,
	}
	card := makePlanView(plan, 0, current)
	if card.Price != "$40.00" {
		t.Fatalf("annual plan price = %s, want $40.00", card.Price)
	}
	if card.Action != "Switch to Target" {
		t.Fatalf("annual plan action = %q, want a switch based on the $40.00 annual equivalent", card.Action)
	}
}

func TestBillingPageShowsTheScheduledChangeAndLocksOtherPlans(t *testing.T) {
	fixture := newBillingFixture(t)
	fixture.subscribe(t, "pro", intervalMonthly)
	renewal := time.Date(2026, time.October, 27, 0, 0, 0, 0, time.UTC)
	if err := fixture.service.ScheduleDemoChange(t.Context(), fixture.principal, "starter", intervalMonthly, renewal); err != nil {
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
	if business := page.card(t, "business"); business.Upcoming || !business.Locked || business.Action != "Unavailable while a change is in progress" {
		t.Errorf("Business card should be locked while a change is scheduled: %#v", business)
	}
	if starter := page.card(t, "starter"); starter.Action != "Scheduled" {
		t.Errorf("scheduled Starter action = %q, want Scheduled", starter.Action)
	}
	html := renderBillingPage(t, page)
	for _, want := range []string{"Change scheduled", "Pro stays active until October 27, 2026", "moves to Starter, billed monthly", `action="/billing/change/undo"`} {
		if !strings.Contains(html, want) {
			t.Errorf("billing page is missing %q", want)
		}
	}
}

func TestBillingPageExplainsScheduledFreeChangeWithoutAFreeCard(t *testing.T) {
	fixture := newBillingFixture(t)
	fixture.subscribe(t, "pro", intervalMonthly)
	renewal := time.Date(2026, time.October, 27, 0, 0, 0, 0, time.UTC)
	if err := fixture.service.ScheduleDemoChange(t.Context(), fixture.principal, "free", "", renewal); err != nil {
		t.Fatal(err)
	}

	page := fixture.page(t, fixture.principal, intervalMonthly)
	if page.Change.PlanName != "Free" || page.Change.Interval != "" {
		t.Fatalf("scheduled Free change = %#v", page.Change)
	}
	for _, card := range page.Plans {
		if !card.Current && (!card.Locked || card.Action != "Unavailable while a change is in progress") {
			t.Errorf("non-current plan remains actionable during scheduled Free change: %#v", card)
		}
	}
	html := renderBillingPage(t, page)
	for _, want := range []string{"moves to Free", "Unavailable while a change is in progress"} {
		if !strings.Contains(html, want) {
			t.Errorf("billing page is missing %q", want)
		}
	}
}

func TestBillingPageNamesTheNextRenewalWhenTheScheduledDateIsUnknown(t *testing.T) {
	fixture := newBillingFixture(t)
	fixture.subscribe(t, "pro", intervalMonthly)
	if err := fixture.store.UpdateAs(t.Context(), fixture.principal, func(account *core.Account) error {
		org := account.Organizations[fixture.principal.OrganizationID]
		org.Billing.ScheduledPlanKey = "starter"
		org.Billing.ScheduledInterval = intervalMonthly
		account.Organizations[org.ID] = org
		return nil
	}); err != nil {
		t.Fatal(err)
	}

	html := renderBillingPage(t, fixture.page(t, fixture.principal, intervalMonthly))
	if !strings.Contains(html, "Pro stays active until the next renewal, then") || strings.Contains(html, "until Not scheduled") {
		t.Errorf("scheduled change without a date reads wrong:\n%s", html)
	}
}

func TestUpcomingChangeHumanizesMissingPlanKey(t *testing.T) {
	fixture := newBillingFixture(t)
	change, err := upcomingChange(t.Context(), fixture.store, core.Billing{ScheduledPlanKey: "missing_plan-key"})
	if err != nil {
		t.Fatal(err)
	}
	if change.PlanName != "Missing plan key" {
		t.Fatalf("missing plan name = %q, want Missing plan key", change.PlanName)
	}
}

func TestBillingPageShowsAPendingUpgrade(t *testing.T) {
	fixture := newBillingFixture(t)
	fixture.subscribe(t, "starter", intervalMonthly)
	if err := fixture.store.UpdateAs(t.Context(), fixture.principal, func(account *core.Account) error {
		org := account.Organizations[fixture.principal.OrganizationID]
		org.Billing.PendingPlanKey = "business"
		org.Billing.PendingInterval = intervalAnnual
		account.Organizations[org.ID] = org
		return nil
	}); err != nil {
		t.Fatal(err)
	}
	page := fixture.page(t, fixture.principal, intervalAnnual)
	if business := page.card(t, "business"); !business.Upcoming || business.Badge != "Awaiting payment" || !business.Locked || business.Action != "Awaiting payment" {
		t.Errorf("pending Business card = %#v", business)
	}
	html := renderBillingPage(t, page)
	if !strings.Contains(html, "Stripe is confirming the change to Business, billed annually") || strings.Contains(html, "/billing/change/undo") {
		t.Errorf("pending upgrade notice is wrong:\n%s", html)
	}
}

func TestCheckoutActionsStayDisabledForAnExpiredSubscriptionWithAPendingChange(t *testing.T) {
	fixture := newBillingFixture(t)
	if err := fixture.store.UpdateAs(t.Context(), fixture.principal, func(account *core.Account) error {
		org := account.Organizations[fixture.principal.OrganizationID]
		org.Billing.SubscriptionID = "sub_expired"
		org.Billing.Status = "incomplete_expired"
		org.Billing.PendingPlanKey = "starter"
		org.Billing.PendingInterval = intervalMonthly
		account.Organizations[org.ID] = org
		return nil
	}); err != nil {
		t.Fatal(err)
	}

	page := fixture.page(t, fixture.principal, intervalMonthly)
	if page.HasSubscription {
		t.Fatal("expired subscription was treated as active")
	}
	html := renderBillingPage(t, page)
	if !strings.Contains(html, `disabled>Awaiting payment</button>`) {
		t.Fatal("checkout form allows submitting a plan with a pending change")
	}
}

func TestBillingPageForAnOrganizationAwaitingItsFirstPayment(t *testing.T) {
	fixture := newBillingFixture(t)
	if err := fixture.store.UpdateAs(t.Context(), fixture.principal, func(account *core.Account) error {
		org := account.Organizations[fixture.principal.OrganizationID]
		org.Kind, org.PlanID, org.BillingStatus = "business", "443RCMMF70MNP6S1QMT230NF99", "billing_required"
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
	member.UserID = "3815H39Y2X0TANA4N0YVSSWWH3"

	owner := fixture.page(t, fixture.principal, intervalMonthly)
	if !owner.CanManage || !owner.ShowPortal {
		t.Fatalf("owner page = %#v", owner)
	}
	html := renderBillingPage(t, fixture.page(t, member, intervalMonthly))
	for _, owned := range []string{`href="/billing/portal"`, `action="/organization"`, `action="/billing/checkout"`} {
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
	if err := fixture.store.UpdateAs(t.Context(), fixture.principal, func(account *core.Account) error {
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
