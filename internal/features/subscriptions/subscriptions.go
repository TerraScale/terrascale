package subscriptions

import (
	"cmp"
	"fmt"
	"net/http"
	"net/url"
	"strings"
	"time"

	"github.com/a-h/templ"
	"terrascale.tech/dashboard/internal/core"
	"terrascale.tech/dashboard/internal/features/payments"
)

// planView is one plan card, priced for the billing interval the page shows.
type planView struct {
	Key       string
	Name      string
	Tone      string
	Price     string
	Billed    string
	Features  []string
	Interval  string
	Current   bool
	Action    string
}

type subscriptionsPage struct {
	OrganizationName string
	PlanName         string
	BillingStatus    string
	StatusTone       string
	BillingInterval  string
	Renewal          string
	PaymentValidated bool
	Demo             bool
	PaymentNotice    string
	Interval         string
	AnnualSavings    int
	Plans            []planView
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

func Mount(mux *http.ServeMux, deps core.Deps) {
	service := NewBillingService(deps.Store)
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
		data := subscriptionsPage{
			OrganizationName: view.Organization.Name,
			PlanName:         cmp.Or(view.Plan.Name, "Free"),
			BillingStatus:    billingLabel(view.Organization),
			StatusTone:       billingTone(view.Organization),
			BillingInterval:  intervalLabel(view.Organization.Billing.Interval),
			Renewal:          formatRenewal(view.Organization.BillingRenewal),
			PaymentValidated: view.Organization.PaymentMethodValidated,
			Demo:             deps.Demo,
			PaymentNotice:    paymentNotice(r.URL.Query().Get("payment")),
			Interval:         interval,
			Plans:            make([]planView, 0, len(view.Plans)),
		}
		current := normalizePlanKey(view.Organization.Billing.PlanKey)
		for i, plan := range view.Plans {
			data.Plans = append(data.Plans, makePlanView(plan, i, interval, current, view.Organization.Billing.Interval, view.Plan.PriceCents))
			if plan.AnnualPriceCents > 0 {
				data.AnnualSavings = max(data.AnnualSavings, int(100-plan.AnnualPriceCents*100/(plan.PriceCents*12)))
			}
		}
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
		principal, ok := requestPrincipal(deps, w, r)
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
		if err != nil || view.Account.ActiveOrganizationID != principal.OrganizationID || view.Organization.OwnerUserID != principal.UserID || view.Organization.Archived || view.Organization.DeletionRequested {
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

	payments.Mount(mux, deps, service)
}

// planTones color plan cards from the cheapest plan up.
var planTones = []string{"cyan", "violet", "amber", "rose"}

func makePlanView(plan core.Plan, index int, interval, currentKey, currentInterval string, currentPrice int64) planView {
	key := normalizePlanKey(plan.Key)
	card := planView{
		Key: key, Name: plan.Name, Tone: planTones[index%len(planTones)],
		Price: formatUSD(plan.PriceCents), Billed: "Billed monthly",
		Interval: intervalMonthly,
	}
	if interval == intervalAnnual {
		if plan.AnnualPriceCents > 0 {
			card.Price = formatUSD(plan.AnnualPriceCents / 12)
			card.Billed = formatUSD(plan.AnnualPriceCents) + " billed annually"
			card.Interval = intervalAnnual
		} else {
			card.Billed = "Monthly billing only"
		}
	}
	if plan.StorageMiB > 0 {
		card.Features = append(card.Features, formatStorage(plan.StorageMiB)+" storage")
	}
	if plan.MonthlyRequests > 0 {
		card.Features = append(card.Features, formatCount(plan.MonthlyRequests)+" requests per month")
	}
	if plan.TrialDays > 0 {
		card.Features = append(card.Features, fmt.Sprintf("%d-day free trial", plan.TrialDays))
	}
	switch {
	case key == currentKey && card.Interval == currentInterval:
		card.Current, card.Action = true, "Current plan"
	case key == currentKey:
		card.Action = "Switch to " + card.Interval + " billing"
	case plan.PriceCents > currentPrice:
		card.Action = "Upgrade to " + plan.Name
	default:
		card.Action = "Switch to " + plan.Name
	}
	return card
}

func formatStorage(mib int64) string {
	if mib >= 1024 && mib%1024 == 0 {
		return fmt.Sprintf("%d GiB", mib/1024)
	}
	return fmt.Sprintf("%d MiB", mib)
}

func formatCount(n int64) string {
	switch {
	case n >= 1_000_000 && n%1_000_000 == 0:
		return fmt.Sprintf("%dM", n/1_000_000)
	case n >= 1_000 && n%1_000 == 0:
		return fmt.Sprintf("%dK", n/1_000)
	default:
		return fmt.Sprint(n)
	}
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

func paymentNotice(outcome string) string {
	switch outcome {
	case "success":
		return "The local demo billing setup is complete."
	case "processing":
		return "Checkout completed. Billing will update after Stripe confirms the subscription."
	case "pending":
		return "Checkout has not been confirmed yet. Billing will update when Stripe confirms it."
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
	principal, ok := requestPrincipal(deps, w, r)
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

func requestPrincipal(deps core.Deps, w http.ResponseWriter, r *http.Request) (core.Principal, bool) {
	principal, ok := deps.Sessions.Principal(r)
	if ok {
		return principal, true
	}
	target := r.URL.RequestURI()
	http.Redirect(w, r, "/login?returnTo="+url.QueryEscape(target), http.StatusSeeOther)
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
