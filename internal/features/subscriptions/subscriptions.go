package subscriptions

import (
	"cmp"
	"fmt"
	"net/http"
	"strings"
	"time"

	"github.com/a-h/templ"
	"terrascale.tech/dashboard/internal/core"
	"terrascale.tech/dashboard/internal/features/payments"
)

// planView is one plan card, priced for the billing interval the page shows.
type planView struct {
	Key      string
	Name     string
	Tone     string
	Price    string
	Billed   string
	Features []string
	Interval string
	Current  bool
	// Upcoming marks the plan an accepted change moves to; its Badge says
	// whether the change is scheduled or waiting for payment.
	Upcoming bool
	Badge    string
	// Locked disables the action while another change is in flight, since
	// billing accepts one change at a time.
	Locked bool
	Action string
}

type subscriptionsPage struct {
	OrganizationName string
	PlanName         string
	Summary          string
	BillingStatus    string
	StatusTone       string
	BillingInterval  string
	Renewal          string
	PaymentValidated bool
	PaymentMethod    string
	HasSubscription  bool
	AwaitingPayment  bool
	CanManage        bool
	ShowPortal       bool
	Change           upcomingPlanChange
	Demo             bool
	PaymentNotice    string
	Interval         string
	AnnualSavings    int
	Plans            []planView
}

// upcomingPlanChange is a plan change billing accepted but has not applied yet: a
// downgrade scheduled for renewal, or an upgrade waiting for its payment.
type upcomingPlanChange struct {
	Key      string
	Interval string
	PlanName string
	Pending  bool
	Date     string
}

// Label names the target plan and, for paid plans, its billing interval.
func (change upcomingPlanChange) Label() string {
	switch change.Interval {
	case intervalMonthly:
		return change.PlanName + ", billed monthly"
	case intervalAnnual:
		return change.PlanName + ", billed annually"
	}
	return change.PlanName
}

type checkoutPage struct {
	SessionID string
	PlanName  string
	Interval  string
	ExpiresAt string
}

type portalPage struct {
	OrganizationName string
	PlanName         string
	BillingStatus    string
	Renewal          string
	Demo             bool
}

// Mount registers the billing pages. billing is nil in demo mode.
func Mount(mux *http.ServeMux, deps core.Deps, billing *payments.Stripe) {
	service := NewBillingService(deps.Store)
	mountTools(deps.Tools, service)
	mux.HandleFunc("GET /billing", func(w http.ResponseWriter, r *http.Request) {
		principal, ok := requestPrincipal(deps, w, r)
		if !ok {
			return
		}
		view, err := service.Snapshot(principal)
		if err != nil {
			http.Error(w, http.StatusText(http.StatusForbidden), http.StatusForbidden)
			return
		}
		interval := intervalMonthly
		if r.URL.Query().Get("interval") == intervalAnnual {
			interval = intervalAnnual
		}
		data := billingPage(deps.Store, principal, view, interval)
		data.Demo = deps.Demo
		data.PaymentNotice = paymentNotice(r.URL.Query().Get("payment"))
		if err := deps.Render.Page(w, r, core.Page{
			Layout: "shell", Title: "Billing | TerraScale", Principal: principal,
			View: func(page core.Page) templ.Component {
				return subscriptionsContent(page.CSRFToken, data)
			},
		}); err != nil {
			http.Error(w, "Could not render billing.", http.StatusInternalServerError)
		}
	})

	mux.HandleFunc("GET /checkout/{sessionID}", func(w http.ResponseWriter, r *http.Request) {
		if !deps.Demo {
			http.NotFound(w, r)
			return
		}
		principal, ok := checkoutPrincipal(deps, service, w, r)
		if !ok {
			return
		}
		session, err := service.Checkout(principal, r.PathValue("sessionID"))
		if err != nil {
			http.Error(w, http.StatusText(http.StatusNotFound), http.StatusNotFound)
			return
		}
		plan, ok := deps.Store.Plan(session.PlanID)
		if !ok {
			http.Error(w, http.StatusText(http.StatusNotFound), http.StatusNotFound)
			return
		}
		data := checkoutPage{
			SessionID: session.ID, PlanName: plan.Name, Interval: session.Interval,
			ExpiresAt: session.ExpiresAt.UTC().Format(time.RFC3339),
		}
		if err := deps.Render.Page(w, r, core.Page{
			Layout: "auth", Title: "Local payment setup | TerraScale", Principal: principal,
			View: func(page core.Page) templ.Component {
				return checkoutContent(page.CSRFToken, data)
			},
		}); err != nil {
			http.Error(w, "Could not render checkout.", http.StatusInternalServerError)
		}
	})

	mux.HandleFunc("POST /checkout/{sessionID}/complete", func(w http.ResponseWriter, r *http.Request) {
		completeDemoCheckout(deps, service, w, r, false)
	})
	mux.HandleFunc("POST /checkout/{sessionID}/cancel", func(w http.ResponseWriter, r *http.Request) {
		completeDemoCheckout(deps, service, w, r, true)
	})

	mux.HandleFunc("GET /billing/portal/{sessionID}", func(w http.ResponseWriter, r *http.Request) {
		if !deps.Demo {
			http.NotFound(w, r)
			return
		}
		principal, ok := requestPrincipal(deps, w, r)
		if !ok {
			return
		}
		view, err := service.Snapshot(principal)
		if err != nil || view.Organization.OwnerUserID != principal.UserID || view.Organization.Archived || view.Organization.DeletionRequested {
			http.Error(w, http.StatusText(http.StatusForbidden), http.StatusForbidden)
			return
		}
		data := portalPage{
			OrganizationName: view.Organization.Name, PlanName: view.Plan.Name,
			BillingStatus: billingLabel(view.Organization), Renewal: formatRenewal(view.Organization.BillingRenewal), Demo: true,
		}
		if err := deps.Render.Page(w, r, core.Page{
			Layout: "auth", Title: "Local billing portal | TerraScale", Principal: principal,
			View: func(core.Page) templ.Component {
				return portalContent(data)
			},
		}); err != nil {
			http.Error(w, "Could not render billing portal.", http.StatusInternalServerError)
		}
	})

	payments.Mount(mux, deps, service, billing)
}

// planTones color plan cards from the cheapest plan up.
var planTones = []string{"cyan", "violet", "amber", "rose"}

// billingPage describes the organization's plan and prices every available
// plan against it for the billing interval the page shows.
func billingPage(store *core.Store, principal core.Principal, view BillingSnapshot, interval string) subscriptionsPage {
	org := view.Organization
	hasSubscription := org.Billing.SubscriptionID != "" && org.Billing.Status != "canceled" && org.Billing.Status != "incomplete_expired"
	// Billing routes accept changes only from the owner of an active
	// organization.
	owner := org.OwnerUserID == principal.UserID && org.Status == "active" && !org.Archived && !org.DeletionRequested
	data := subscriptionsPage{
		OrganizationName: org.Name,
		PlanName:         cmp.Or(view.Plan.Name, "Free"),
		Summary:          org.Name + " is billed on this plan.",
		BillingStatus:    billingLabel(org),
		StatusTone:       billingTone(org),
		BillingInterval:  intervalLabel(org.Billing.Interval),
		Renewal:          formatRenewal(org.BillingRenewal),
		PaymentValidated: org.PaymentMethodValidated,
		PaymentMethod:    paymentMethodLabel(org.Billing),
		HasSubscription:  hasSubscription,
		AwaitingPayment:  !hasSubscription && org.BillingStatus == "billing_required",
		CanManage:        owner,
		ShowPortal:       owner && (hasSubscription || org.Billing.CustomerID != ""),
		Change:           upcomingChange(store, org.Billing),
		Interval:         interval,
		Plans:            make([]planView, 0, len(view.Plans)),
	}
	if data.AwaitingPayment {
		data.PlanName, data.Summary = "No plan", org.Name+" has no active plan."
	}
	current := planCardContext{
		Interval: interval, Key: normalizePlanKey(org.Billing.PlanKey), BillingInterval: org.Billing.Interval,
		PriceCents: view.Plan.PriceCents, TrialEligible: !hasSubscription && !org.Billing.TrialUsed,
		AwaitingPayment: data.AwaitingPayment, Change: data.Change,
	}
	for i, plan := range view.Plans {
		data.Plans = append(data.Plans, makePlanView(plan, i, current))
		if plan.AnnualPriceCents > 0 {
			data.AnnualSavings = max(data.AnnualSavings, int(100-plan.AnnualPriceCents*100/(plan.PriceCents*12)))
		}
	}
	return data
}

// upcomingChange reads the plan change billing accepted but has not applied.
// Billing holds at most one: a pending upgrade or a scheduled downgrade.
func upcomingChange(store *core.Store, billing core.Billing) upcomingPlanChange {
	change := upcomingPlanChange{Key: billing.ScheduledPlanKey, Interval: billing.ScheduledInterval, Date: formatRenewal(billing.ScheduledAt)}
	if billing.PendingPlanKey != "" {
		change = upcomingPlanChange{Key: billing.PendingPlanKey, Interval: billing.PendingInterval, Pending: true}
	}
	if change.Key == "" {
		return upcomingPlanChange{}
	}
	change.PlanName = change.Key
	if plan, ok := store.Plan(change.Key); ok {
		change.PlanName = plan.Name
	}
	return change
}

// planCardContext is the organization's billing state that each plan card is
// compared against.
type planCardContext struct {
	Interval        string
	Key             string
	BillingInterval string
	PriceCents      int64
	TrialEligible   bool
	AwaitingPayment bool
	Change          upcomingPlanChange
}

func makePlanView(plan core.Plan, index int, current planCardContext) planView {
	key := normalizePlanKey(plan.Key)
	card := planView{
		Key: key, Name: plan.Name, Tone: planTones[index%len(planTones)],
		Price: formatUSD(plan.PriceCents), Billed: "Billed monthly",
		Interval: intervalMonthly,
		Features: planFeatures(plan),
	}
	if current.Interval == intervalAnnual {
		if plan.AnnualPriceCents > 0 {
			// The monthly equivalent is rounded to the nearest cent.
			card.Price = formatUSD((plan.AnnualPriceCents + 6) / 12)
			card.Billed = formatUSD(plan.AnnualPriceCents) + " billed annually"
			card.Interval = intervalAnnual
		} else {
			card.Billed = "Monthly billing only"
		}
	}
	// Checkout grants a trial only to an organization that never had one;
	// plan changes on a subscription never include one.
	if current.TrialEligible && plan.TrialDays > 0 {
		card.Features = append(card.Features, fmt.Sprintf("%d-day free trial", plan.TrialDays))
	}
	switch {
	case current.AwaitingPayment:
		card.Action = "Choose " + plan.Name
	case key == current.Key && card.Interval == current.BillingInterval:
		card.Current, card.Action = true, "Current plan"
	case key == current.Key:
		card.Action = "Switch to " + card.Interval + " billing"
	case plan.PriceCents > current.PriceCents:
		card.Action = "Upgrade to " + plan.Name
	default:
		card.Action = "Switch to " + plan.Name
	}
	if current.Change.Key != "" && !card.Current {
		card.Locked = true
		if current.Change.Key == key && current.Change.Interval == card.Interval {
			card.Upcoming, card.Badge = true, "Scheduled"
			if current.Change.Pending {
				card.Badge = "Awaiting payment"
			}
		}
	}
	return card
}

// planFeatures lists a plan's allowances as a plan card shows them. Whether a
// trial applies depends on the organization, so callers add it.
func planFeatures(plan core.Plan) []string {
	return []string{plan.StorageAllowance(), plan.OperationsAllowance(), plan.DatabaseAllowance(), plan.MemberAllowance()}
}

func intervalLabel(interval string) string {
	switch interval {
	case intervalAnnual:
		return "Annual"
	case intervalMonthly:
		return "Monthly"
	default:
		return "None"
	}
}

func billingTone(org core.Organization) string {
	switch {
	case org.Billing.Status == "trialing":
		return "sky"
	case org.BillingStatus == "billing_required":
		return "amber"
	case org.BillingStatus == "" || org.BillingStatus == "active":
		return "emerald"
	default:
		return "slate"
	}
}

// paymentMethodLabel describes the card Stripe charges, such as
// "Visa •••• 4242, expires 08/28", or is empty when none is known.
func paymentMethodLabel(billing core.Billing) string {
	if billing.PaymentMethodLastFour == "" {
		return ""
	}
	label := cmp.Or(cardBrands[billing.PaymentMethodBrand], "Card") + " •••• " + billing.PaymentMethodLastFour
	if billing.PaymentMethodExpiry != "" {
		label += ", expires " + billing.PaymentMethodExpiry
	}
	return label
}

var cardBrands = map[string]string{
	"amex": "American Express", "diners": "Diners Club", "discover": "Discover", "eftpos_au": "eftpos",
	"jcb": "JCB", "mastercard": "Mastercard", "unionpay": "UnionPay", "visa": "Visa",
}

func paymentNotice(outcome string) string {
	switch outcome {
	case "success":
		return "Billing setup is complete."
	case "processing":
		return "Checkout is still processing. Billing will update as soon as Stripe confirms the payment."
	case "canceled":
		return "Checkout was canceled. Your current plan is unchanged."
	default:
		return ""
	}
}

func completeDemoCheckout(deps core.Deps, service BillingService, w http.ResponseWriter, r *http.Request, canceled bool) {
	if !deps.Demo {
		http.NotFound(w, r)
		return
	}
	principal, ok := checkoutPrincipal(deps, service, w, r)
	if !ok {
		return
	}
	returnTo, err := service.CompleteDemoCheckout(principal, r.PathValue("sessionID"), canceled)
	if err != nil {
		http.Error(w, http.StatusText(http.StatusNotFound), http.StatusNotFound)
		return
	}
	http.Redirect(w, r, returnTo, http.StatusSeeOther)
}

// checkoutPrincipal is the request principal scoped to the organization of
// the checkout session in the path.
func checkoutPrincipal(deps core.Deps, service BillingService, w http.ResponseWriter, r *http.Request) (core.Principal, bool) {
	principal, ok := requestPrincipal(deps, w, r)
	if !ok {
		return core.Principal{}, false
	}
	principal, err := service.CheckoutPrincipal(principal, r.PathValue("sessionID"))
	if err != nil {
		http.Error(w, http.StatusText(http.StatusNotFound), http.StatusNotFound)
		return core.Principal{}, false
	}
	return principal, true
}

func requestPrincipal(deps core.Deps, w http.ResponseWriter, r *http.Request) (core.Principal, bool) {
	principal, ok := deps.Sessions.Principal(r)
	if ok {
		return principal, true
	}
	core.RedirectToLogin(w, r)
	return core.Principal{}, false
}

func billingLabel(org core.Organization) string {
	if org.Billing.Status == "trialing" {
		return "Trialing"
	}
	if org.BillingStatus == "billing_required" {
		return "Payment required"
	}
	if org.BillingStatus == "" {
		return "Active"
	}
	return strings.ToUpper(org.BillingStatus[:1]) + org.BillingStatus[1:]
}

func formatRenewal(value time.Time) string {
	if value.IsZero() {
		return "Not scheduled"
	}
	return value.UTC().Format("January 2, 2006")
}

func formatUSD(cents int64) string {
	return fmt.Sprintf("$%d.%02d", cents/100, cents%100)
}
