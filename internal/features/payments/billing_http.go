package payments

import (
	"context"
	"errors"
	"net/http"
	"net/url"
	"strings"
	"time"

	"terrascale.tech/dashboard/internal/core"
)

// checkoutLifetime is how long a Stripe Checkout Session stays payable. The
// local pending checkout uses the expiry Stripe returns, so both agree.
const checkoutLifetime = time.Hour

var ErrInvalidReturnPath = core.Invalid("return path must stay inside the dashboard")

// BillingFlow is implemented by the subscriptions slice. It keeps provider
// callbacks behind the state owner while this package owns the HTTP boundary.
type BillingFlow interface {
	BeginDemoCheckout(context.Context, core.Principal, string, string, string) (core.CheckoutSession, error)
	Checkout(context.Context, core.Principal, string) (core.CheckoutSession, error)
	CompleteDemoCheckout(context.Context, core.Principal, string, bool) (string, error)
	RecordCustomer(context.Context, core.Principal, string) (string, error)
	RecordExternalCheckout(context.Context, core.Principal, string, string, string, CheckoutSession) error
	ExpireExternalCheckout(context.Context, core.Principal, string) error
	// CheckoutPrincipal scopes a browser principal to the organization that
	// owns a checkout session, which MCP may start outside the selection.
	CheckoutPrincipal(context.Context, core.Principal, string) (core.Principal, error)
	// CheckoutOutcome reports where the owner returns after a checkout and
	// whether it succeeded, from the organization's projected state.
	CheckoutOutcome(context.Context, core.Principal, string) (string, string, error)
	// PendingCheckoutID returns the organization's open Checkout Session.
	PendingCheckoutID(ctx context.Context, organizationID string) (string, error)
	// ProjectCustomerState applies synced Stripe state to the organization.
	ProjectCustomerState(context.Context, CustomerState, *CheckoutSession) error
}

// Mount registers billing provider routes and tools. In demo mode billing is
// nil and every route stays on the in-memory simulator.
func Mount(mux *http.ServeMux, deps core.Deps, flow BillingFlow, billing *Stripe) {
	service := NewService(deps, flow, billing)
	if deps.Tools != nil {
		mountTools(deps.Tools, service)
	}
	mountPlanChanges(mux, deps, flow, service)
	mux.HandleFunc("POST /billing/checkout", func(w http.ResponseWriter, r *http.Request) {
		if !billingForm(w, r) {
			return
		}
		principal, ok := requestPrincipal(deps, w, r)
		if !ok {
			return
		}
		location, err := service.StartCheckout(r.Context(), principal, r.Form.Get("plan"), r.Form.Get("interval"), r.Form.Get("return"))
		switch {
		case errors.Is(err, ErrSubscriptionManaged):
			http.Redirect(w, r, "/billing/portal", http.StatusSeeOther)
		case err != nil:
			writeBillingError(w, err)
		default:
			http.Redirect(w, r, location, http.StatusSeeOther)
		}
	})
	mux.HandleFunc("GET /billing/checkout/return", func(w http.ResponseWriter, r *http.Request) {
		principal, ok := requestPrincipal(deps, w, r)
		if !ok {
			return
		}
		location, err := service.ReturnFromCheckout(r.Context(), principal, r.URL.Query().Get("session_id"))
		if err != nil {
			writeBillingError(w, err)
			return
		}
		http.Redirect(w, r, location, http.StatusSeeOther)
	})
	mux.HandleFunc("GET /billing/portal", func(w http.ResponseWriter, r *http.Request) {
		principal, ok := requestPrincipal(deps, w, r)
		if !ok {
			return
		}
		location, err := service.PortalURL(r.Context(), principal)
		if err != nil {
			writeBillingError(w, err)
			return
		}
		http.Redirect(w, r, location, http.StatusSeeOther)
	})
	handler := handleWebhook(flow, billing)
	mux.HandleFunc("POST "+WebhookPath, handler)
	mux.HandleFunc("POST "+LegacyWebhookPath, handler)
}

func billingForm(w http.ResponseWriter, r *http.Request) bool {
	r.Body = http.MaxBytesReader(w, r.Body, 64<<10)
	if err := r.ParseForm(); err != nil {
		http.Error(w, "Invalid billing form.", http.StatusBadRequest)
		return false
	}
	return true
}

func requestPrincipal(deps core.Deps, w http.ResponseWriter, r *http.Request) (core.Principal, bool) {
	principal, ok := deps.Sessions.Principal(r)
	if ok {
		return principal, true
	}
	core.RedirectToLogin(w, r)
	return core.Principal{}, false
}

func selectedPlan(ctx context.Context, store core.Store, key, interval string) (core.Plan, error) {
	key = NormalizePlanKey(key)
	unavailable := core.Invalid("choose an available paid plan and a monthly or annual interval")
	if key == "" || (interval != IntervalMonthly && interval != IntervalAnnual) {
		return core.Plan{}, unavailable
	}
	plan, err := store.Plan(ctx, key)
	if err != nil && !errors.Is(err, core.ErrNotFound) {
		return core.Plan{}, err
	}
	if err != nil || strings.EqualFold(strings.TrimSpace(plan.Key), "internal") || !plan.Visible || plan.PriceCents <= 0 || (interval == IntervalAnnual && plan.AnnualPriceCents <= 0) {
		return core.Plan{}, unavailable
	}
	return plan, nil
}

// NormalizeReturnPath rejects absolute, protocol-relative, escaped authority,
// and backslash paths so checkout cannot be used as an open redirect.
func NormalizeReturnPath(value, fallback string) (string, error) {
	value = strings.TrimSpace(value)
	if value == "" {
		value = fallback
	}
	if strings.ContainsAny(value, "\\\r\n") {
		return "", ErrInvalidReturnPath
	}
	parsed, err := url.ParseRequestURI(value)
	if err != nil || parsed.IsAbs() || parsed.Host != "" || parsed.User != nil || parsed.Fragment != "" || !strings.HasPrefix(parsed.Path, "/") || strings.HasPrefix(parsed.Path, "//") {
		return "", ErrInvalidReturnPath
	}
	if decoded, err := url.PathUnescape(parsed.EscapedPath()); err != nil || strings.HasPrefix(decoded, "//") || strings.Contains(decoded, "\\") {
		return "", ErrInvalidReturnPath
	}
	return parsed.RequestURI(), nil
}

// WithPaymentOutcome sets the payment query parameter on a normalized return
// path, replacing any earlier outcome.
func WithPaymentOutcome(returnPath, outcome string) string {
	parsed, err := url.Parse(returnPath)
	if err != nil {
		return "/organization?tab=billing"
	}
	query := parsed.Query()
	query.Set("payment", outcome)
	parsed.RawQuery = query.Encode()
	return parsed.RequestURI()
}

func writeBillingError(w http.ResponseWriter, err error) {
	status, message := billingErrorStatus(err)
	http.Error(w, message, status)
}
