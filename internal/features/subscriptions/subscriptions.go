package subscriptions

import (
	"fmt"
	"net/http"
	"net/url"
	"strings"
	"time"

	"github.com/a-h/templ"
	"terrascale.tech/dashboard/internal/core"
	"terrascale.tech/dashboard/internal/features/payments"
)

type planView struct {
	ID              string
	Key             string
	Name            string
	MonthlyPrice    string
	AnnualPrice     string
	AnnualAvailable bool
	TrialDays       int
	StorageMiB      int64
	MonthlyRequests int64
	ShowLimits      bool
}

type subscriptionsPage struct {
	OrganizationName string
	PlanName         string
	PlanKey          string
	BillingStatus    string
	BillingInterval  string
	Renewal          string
	PaymentValidated bool
	Demo             bool
	PaymentNotice    string
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
		data := subscriptionsPage{
			OrganizationName: view.Organization.Name,
			PlanName:         view.Plan.Name,
			PlanKey:          view.Organization.Billing.PlanKey,
			BillingStatus:    billingLabel(view.Organization),
			BillingInterval:  strings.ToUpper(view.Organization.Billing.Interval),
			Renewal:          formatRenewal(view.Organization.BillingRenewal),
			PaymentValidated: view.Organization.PaymentMethodValidated,
			Demo:             deps.Demo,
			PaymentNotice:    paymentNotice(r.URL.Query().Get("payment")),
			Plans:            make([]planView, 0, len(view.Plans)),
		}
		for _, plan := range view.Plans {
			data.Plans = append(data.Plans, planView{
				ID: plan.ID, Key: normalizePlanKey(plan.Key), Name: plan.Name,
				MonthlyPrice: formatUSD(plan.PriceCents), AnnualPrice: formatUSD(plan.AnnualPriceCents),
				AnnualAvailable: plan.AnnualPriceCents > 0,
				TrialDays:       plan.TrialDays, StorageMiB: plan.StorageMiB, MonthlyRequests: plan.MonthlyRequests,
				ShowLimits: plan.StorageMiB > 0 && plan.MonthlyRequests > 0,
			})
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
