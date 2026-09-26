package payments

import (
	"context"
	"errors"
	"log/slog"
	"net/http"
	"net/url"
	"strings"
	"time"

	"terrascale.tech/dashboard/internal/core"
)

// checkoutLifetime is how long a Stripe Checkout Session stays payable. The
// local pending checkout uses the expiry Stripe returns, so both agree.
const checkoutLifetime = time.Hour

var ErrInvalidReturnPath = errors.New("return path must stay inside the dashboard")

// BillingFlow is implemented by the subscriptions slice. It keeps provider
// callbacks behind the state owner while this package owns the HTTP boundary.
type BillingFlow interface {
	BeginDemoCheckout(core.Principal, string, string, string) (core.CheckoutSession, error)
	Checkout(core.Principal, string) (core.CheckoutSession, error)
	CheckoutForReturn(core.Principal, string) (core.CheckoutSession, error)
	CompleteDemoCheckout(core.Principal, string, bool) (string, error)
	RecordCustomer(core.Principal, string) (string, error)
	RecordExternalCheckout(core.Principal, string, string, string, CheckoutSession) error
	ExpireExternalCheckout(core.Principal, string) error
	ReconcileCheckoutReturn(core.Principal, string, CheckoutSession) (string, string, error)
	ApplyStripeEvent(BillingEvent) error
}

// Mount registers billing provider routes. In demo mode billing is nil and
// every route stays on the in-memory simulator.
func Mount(mux *http.ServeMux, deps core.Deps, flow BillingFlow, billing *Stripe) {
	mountPlanChanges(mux, deps, flow, billing)
	mux.HandleFunc("POST /billing/checkout", func(w http.ResponseWriter, r *http.Request) {
		if !billingForm(w, r) {
			return
		}
		principal, ok := requestPrincipal(deps, w, r)
		if !ok {
			return
		}
		planKey, interval, returnPath := r.Form.Get("plan"), r.Form.Get("interval"), r.Form.Get("return")
		if deps.Demo {
			session, err := flow.BeginDemoCheckout(principal, planKey, interval, returnPath)
			if err != nil {
				writeBillingError(w, err)
				return
			}
			http.Redirect(w, r, "/checkout/"+url.PathEscape(session.ID), http.StatusSeeOther)
			return
		}
		if billing == nil {
			http.Error(w, "Billing is not configured.", http.StatusServiceUnavailable)
			return
		}
		billing.startCheckout(w, r, deps, flow, principal, planKey, interval, returnPath)
	})

	mux.HandleFunc("GET /billing/checkout/return", func(w http.ResponseWriter, r *http.Request) {
		principal, ok := requestPrincipal(deps, w, r)
		if !ok {
			return
		}
		sessionID := strings.TrimSpace(r.URL.Query().Get("session_id"))
		if sessionID == "" {
			http.Error(w, "Checkout session is missing.", http.StatusBadRequest)
			return
		}
		if deps.Demo {
			http.Redirect(w, r, "/checkout/"+url.PathEscape(sessionID), http.StatusSeeOther)
			return
		}
		if billing == nil {
			http.Error(w, "Billing is not configured.", http.StatusServiceUnavailable)
			return
		}
		if _, err := flow.CheckoutForReturn(principal, sessionID); err != nil {
			http.Error(w, http.StatusText(http.StatusNotFound), http.StatusNotFound)
			return
		}
		remote, err := billing.provider.RetrieveCheckoutSession(r.Context(), sessionID)
		if err != nil {
			slog.Error("retrieve Stripe checkout session", "session_id", sessionID, "error", err)
			http.Error(w, "Could not verify checkout status.", http.StatusBadGateway)
			return
		}
		returnPath, state, err := flow.ReconcileCheckoutReturn(principal, sessionID, remote)
		if err != nil {
			writeBillingError(w, err)
			return
		}
		http.Redirect(w, r, WithPaymentOutcome(returnPath, state), http.StatusSeeOther)
	})

	mux.HandleFunc("GET /billing/portal", func(w http.ResponseWriter, r *http.Request) {
		principal, ok := requestPrincipal(deps, w, r)
		if !ok {
			return
		}
		if !deps.Demo && billing == nil {
			http.Error(w, "Billing is not configured.", http.StatusServiceUnavailable)
			return
		}
		account, err := deps.Store.SnapshotAs(principal)
		if err != nil || account.ActiveOrganizationID != principal.OrganizationID {
			http.Error(w, http.StatusText(http.StatusForbidden), http.StatusForbidden)
			return
		}
		organization, err := account.OrganizationFor(principal, principal.OrganizationID)
		if err != nil || organization.OwnerUserID != principal.UserID || organization.Archived || organization.DeletionRequested {
			http.Error(w, http.StatusText(http.StatusForbidden), http.StatusForbidden)
			return
		}
		if deps.Demo {
			http.Redirect(w, r, "/billing/portal/local", http.StatusSeeOther)
			return
		}
		if organization.Billing.CustomerID == "" {
			http.Error(w, "No billing account is available for this organization.", http.StatusConflict)
			return
		}
		portal, err := billing.provider.CreatePortalSession(r.Context(), organization.Billing.CustomerID, billing.config.PublicOrigin+"/billing", core.NewID("portal"))
		if err != nil {
			slog.Error("create Stripe billing portal", "organization_id", organization.ID, "error", err)
			http.Error(w, "Could not open the billing portal.", http.StatusBadGateway)
			return
		}
		if _, err := absoluteHTTPURL(portal.URL); err != nil {
			http.Error(w, "Billing provider returned an invalid URL.", http.StatusBadGateway)
			return
		}
		http.Redirect(w, r, portal.URL, http.StatusSeeOther)
	})

	mux.HandleFunc("POST "+WebhookPath, handleWebhook(flow, billing))
}

// startCheckout resumes the organization's open Stripe Checkout when it
// matches the requested plan, and otherwise creates one bound to the tenant
// before redirecting the owner to Stripe.
func (billing *Stripe) startCheckout(w http.ResponseWriter, r *http.Request, deps core.Deps, flow BillingFlow, principal core.Principal, planKey, interval, returnPath string) {
	account, err := deps.Store.SnapshotAs(principal)
	if err != nil || account.ActiveOrganizationID != principal.OrganizationID {
		http.Error(w, http.StatusText(http.StatusForbidden), http.StatusForbidden)
		return
	}
	organization, err := account.OrganizationFor(principal, principal.OrganizationID)
	if err != nil || organization.OwnerUserID != principal.UserID || organization.Status != "active" || organization.Archived || organization.DeletionRequested {
		http.Error(w, http.StatusText(http.StatusForbidden), http.StatusForbidden)
		return
	}
	if organization.Billing.SubscriptionID != "" && organization.Billing.Status != "canceled" && organization.Billing.Status != "incomplete_expired" {
		http.Redirect(w, r, "/billing/portal", http.StatusSeeOther)
		return
	}
	if organization.Billing.SubscriptionID == "" && organization.BillingStatus == "active" {
		http.Error(w, "An existing active plan must be managed through its billing account.", http.StatusConflict)
		return
	}
	plan, err := selectedPlan(deps.Store, planKey, interval)
	if err != nil {
		writeBillingError(w, err)
		return
	}
	if organization.Billing.CheckoutID != "" && billing.resumeCheckout(w, r, deps, flow, principal, organization, plan, interval) {
		return
	}
	returnPath, err = NormalizeReturnPath(returnPath, "/billing")
	if err != nil {
		writeBillingError(w, err)
		return
	}
	customerID := organization.Billing.CustomerID
	if customerID == "" {
		created, err := billing.provider.CreateCustomer(r.Context(), CustomerRequest{
			Email: account.User.Email, Name: organization.Name, OrganizationID: organization.ID,
			IdempotencyKey: "customer:" + organization.ID,
		})
		if err != nil {
			slog.Error("create Stripe customer", "organization_id", organization.ID, "error", err)
			http.Error(w, "Could not start checkout.", http.StatusBadGateway)
			return
		}
		customerID, err = flow.RecordCustomer(principal, created.ID)
		if err != nil {
			writeBillingError(w, err)
			return
		}
	}
	trialDays := 0
	if !organization.Billing.TrialUsed {
		trialDays = plan.TrialDays
	}
	created, err := billing.provider.CreateCheckoutSession(r.Context(), CheckoutRequest{
		CustomerID: customerID, OrganizationID: organization.ID, OrganizationName: organization.Name,
		PlanKey: plan.Key, Interval: interval, TrialDays: trialDays,
		SuccessURL:     billing.config.PublicOrigin + "/billing/checkout/return?session_id={CHECKOUT_SESSION_ID}",
		CancelURL:      billing.config.PublicOrigin + WithPaymentOutcome(returnPath, "canceled"),
		ExpiresAt:      time.Now().Add(checkoutLifetime),
		IdempotencyKey: core.NewID("checkout"),
	})
	if err != nil {
		slog.Error("create Stripe checkout session", "organization_id", organization.ID, "error", err)
		http.Error(w, "Could not start checkout.", http.StatusBadGateway)
		return
	}
	if _, err := absoluteHTTPURL(created.URL); err != nil {
		billing.expireUnboundCheckout(r.Context(), created.ID)
		http.Error(w, "Checkout provider returned an invalid URL.", http.StatusBadGateway)
		return
	}
	if created.CustomerID != customerID || created.ClientReferenceID != organization.ID {
		billing.expireUnboundCheckout(r.Context(), created.ID)
		http.Error(w, "Checkout provider returned an unbound session.", http.StatusBadGateway)
		return
	}
	if err := flow.RecordExternalCheckout(principal, plan.Key, interval, returnPath, created); err != nil {
		billing.expireUnboundCheckout(r.Context(), created.ID)
		writeBillingError(w, err)
		return
	}
	http.Redirect(w, r, created.URL, http.StatusSeeOther)
}

// resumeCheckout handles an organization's pending checkout. It reports true
// when it wrote a response; false means the pending checkout expired and a new
// one may be created.
func (billing *Stripe) resumeCheckout(w http.ResponseWriter, r *http.Request, deps core.Deps, flow BillingFlow, principal core.Principal, organization core.Organization, plan core.Plan, interval string) bool {
	pendingID := organization.Billing.CheckoutID
	pending, exists := organization.CheckoutSessions[pendingID]
	if !exists || pending.ID != pendingID || pending.OwnerUserID != principal.UserID || pending.OrganizationID != organization.ID || pending.Status != "open" {
		http.Error(w, "A checkout is already in progress for this organization.", http.StatusConflict)
		return true
	}
	pendingPlan, planExists := deps.Store.Plan(pending.PlanID)
	if !planExists || NormalizePlanKey(pendingPlan.Key) != NormalizePlanKey(plan.Key) || pending.Interval != interval {
		http.Error(w, "Finish the current checkout before choosing another plan.", http.StatusConflict)
		return true
	}
	remote, err := billing.provider.RetrieveCheckoutSession(r.Context(), pendingID)
	if err != nil {
		slog.Error("retrieve pending Stripe checkout", "session_id", pendingID, "error", err)
		http.Error(w, "Could not verify the existing checkout.", http.StatusBadGateway)
		return true
	}
	if remote.ID != pendingID || remote.CustomerID != organization.Billing.CustomerID || remote.ClientReferenceID != organization.ID {
		http.Error(w, "Existing checkout is not bound to this organization.", http.StatusBadGateway)
		return true
	}
	switch remote.Status {
	case "open":
		if _, err := absoluteHTTPURL(remote.URL); err != nil {
			http.Error(w, "Checkout provider returned an invalid URL.", http.StatusBadGateway)
			return true
		}
		http.Redirect(w, r, remote.URL, http.StatusSeeOther)
		return true
	case "complete":
		http.Redirect(w, r, "/billing/checkout/return?session_id="+url.QueryEscape(pendingID), http.StatusSeeOther)
		return true
	case "expired":
		if err := flow.ExpireExternalCheckout(principal, pendingID); err != nil {
			writeBillingError(w, err)
			return true
		}
		return false
	default:
		http.Error(w, "Existing checkout has an unsupported state.", http.StatusConflict)
		return true
	}
}

// expireUnboundCheckout closes a Checkout Session the dashboard could not bind
// to the tenant, so the customer can never pay for it.
func (billing *Stripe) expireUnboundCheckout(ctx context.Context, sessionID string) {
	if sessionID == "" {
		return
	}
	cleanup, cancel := context.WithTimeout(context.WithoutCancel(ctx), 5*time.Second)
	defer cancel()
	if err := billing.provider.ExpireCheckoutSession(cleanup, sessionID); err != nil {
		slog.Warn("expire unbound Stripe checkout session", "session_id", sessionID, "error", err)
	}
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

func selectedPlan(store *core.Store, key, interval string) (core.Plan, error) {
	key = NormalizePlanKey(key)
	if key == "" || (interval != IntervalMonthly && interval != IntervalAnnual) {
		return core.Plan{}, core.ErrInvalid
	}
	plan, ok := store.Plan(key)
	if !ok || !plan.Visible || plan.PriceCents <= 0 || (interval == IntervalAnnual && plan.AnnualPriceCents <= 0) {
		return core.Plan{}, core.ErrInvalid
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
		return "/billing"
	}
	query := parsed.Query()
	query.Set("payment", outcome)
	parsed.RawQuery = query.Encode()
	return parsed.RequestURI()
}

func writeBillingError(w http.ResponseWriter, err error) {
	switch {
	case errors.Is(err, core.ErrForbidden):
		http.Error(w, http.StatusText(http.StatusForbidden), http.StatusForbidden)
	case errors.Is(err, core.ErrNotFound):
		http.Error(w, http.StatusText(http.StatusNotFound), http.StatusNotFound)
	case errors.Is(err, core.ErrInvalid), errors.Is(err, ErrInvalidReturnPath):
		http.Error(w, http.StatusText(http.StatusBadRequest), http.StatusBadRequest)
	default:
		http.Error(w, http.StatusText(http.StatusConflict), http.StatusConflict)
	}
}
