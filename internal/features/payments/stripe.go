package payments

import (
	"context"
	"encoding/json/v2"
	"errors"
	"fmt"
	"io"
	"log/slog"
	"net/http"
	"net/url"
	"os"
	"strings"
	"time"

	"terrascale.tech/dashboard/internal/core"

	stripe "github.com/stripe/stripe-go/v86"
	"github.com/stripe/stripe-go/v86/webhook"
)

var (
	ErrDemoMode             = errors.New("Stripe is disabled in dashboard demo mode")
	ErrStripeUnavailable    = errors.New("Stripe is not configured")
	ErrStripeDevelopment    = errors.New("Stripe API calls are disabled outside production")
	ErrInvalidBillingInput  = errors.New("invalid billing input")
	ErrUnconfiguredPrice    = errors.New("no Stripe price is configured for this plan and interval")
	ErrInvalidStripeOptions = errors.New("invalid Stripe configuration")
)

const (
	PlanStarter  = "starter"
	PlanPro      = "pro"
	PlanBusiness = "business"

	IntervalMonthly = "monthly"
	IntervalAnnual  = "annual"
)

// PriceKey identifies the Stripe Price selected by trusted server state.
type PriceKey struct {
	PlanKey  string
	Interval string
}

// StripeConfig contains only server-side provider settings. Demo mode ignores
// all Stripe secrets and price IDs so a production demo cannot contact Stripe.
type StripeConfig struct {
	Demo          bool
	Production    bool
	Mode          string
	SecretKey     string
	WebhookSecret string
	PublicOrigin  string
	PriceIDs      map[PriceKey]string
}

// LoadStripeConfig reads Stripe settings without requiring a configuration
// framework. When TS_DASHBOARD_PRODUCTION_DEMO=1, Stripe values are ignored.
// An unset secret also selects the local demo provider path.
func LoadStripeConfig(lookup func(string) string) (StripeConfig, error) {
	if lookup == nil {
		return StripeConfig{}, fmt.Errorf("%w: environment lookup is required", ErrInvalidStripeOptions)
	}
	if strings.TrimSpace(lookup("TS_DASHBOARD_PRODUCTION_DEMO")) == "1" {
		return StripeConfig{Demo: true, Mode: "demo"}, nil
	}
	production := strings.EqualFold(strings.TrimSpace(lookup("APP_ENV")), "production")
	if !production {
		return StripeConfig{Demo: true, Mode: "demo"}, nil
	}

	secretKey := envValue(lookup, "STRIPE_SECRET_KEY", "Payments__Stripe__SecretKey")
	if secretKey == "" {
		return StripeConfig{Demo: true, Mode: "demo"}, nil
	}

	mode := strings.ToLower(envValue(lookup, "STRIPE_MODE", "Payments__Stripe__Mode"))
	if mode == "" {
		mode = "test"
	}
	if mode != "test" && mode != "live" {
		return StripeConfig{}, fmt.Errorf("%w: STRIPE_MODE must be test or live", ErrInvalidStripeOptions)
	}
	isTestKey := strings.HasPrefix(secretKey, "sk_test_")
	isLiveKey := strings.HasPrefix(secretKey, "sk_live_")
	if mode == "test" && !isTestKey {
		return StripeConfig{}, fmt.Errorf("%w: test mode requires a Stripe test key", ErrInvalidStripeOptions)
	}
	if mode == "live" && (isTestKey || !isLiveKey) {
		return StripeConfig{}, fmt.Errorf("%w: live mode requires a Stripe live key", ErrInvalidStripeOptions)
	}
	webhookSecret := envValue(lookup, "STRIPE_WEBHOOK_SECRET", "Payments__Stripe__WebhookSecret")
	if webhookSecret == "" {
		return StripeConfig{}, fmt.Errorf("%w: Stripe webhook secret is required", ErrInvalidStripeOptions)
	}

	prices := make(map[PriceKey]string)
	for _, plan := range []string{PlanStarter, PlanPro, PlanBusiness} {
		for _, interval := range []string{IntervalMonthly, IntervalAnnual} {
			envName := "STRIPE_PRICE_" + strings.ToUpper(plan) + "_" + strings.ToUpper(interval)
			legacyName := "Payments__Stripe__" + title(plan) + title(interval) + "PriceId"
			priceID := envValue(lookup, envName, legacyName)
			if priceID == "" {
				continue
			}
			if !strings.HasPrefix(priceID, "price_") {
				return StripeConfig{}, fmt.Errorf("%w: %s must be a Stripe price ID", ErrInvalidStripeOptions, envName)
			}
			prices[PriceKey{PlanKey: plan, Interval: interval}] = priceID
		}
	}

	publicOrigin := envValue(lookup, "DASHBOARD_PUBLIC_ORIGIN", "Payments__Stripe__PublicOrigin")
	if publicOrigin != "" {
		parsed, err := url.ParseRequestURI(publicOrigin)
		if err != nil || parsed.Host == "" || (parsed.Scheme != "https" && parsed.Scheme != "http") {
			return StripeConfig{}, fmt.Errorf("%w: public origin must be an absolute HTTP(S) URL", ErrInvalidStripeOptions)
		}
		publicOrigin = strings.TrimRight(parsed.Scheme+"://"+parsed.Host, "/")
	}

	return StripeConfig{
		Production:    true,
		Mode:          mode,
		SecretKey:     secretKey,
		WebhookSecret: webhookSecret,
		PublicOrigin:  publicOrigin,
		PriceIDs:      prices,
	}, nil
}

func envValue(lookup func(string) string, names ...string) string {
	for _, name := range names {
		if value := strings.TrimSpace(lookup(name)); value != "" {
			return value
		}
	}
	return ""
}

func title(value string) string {
	return strings.ToUpper(value[:1]) + value[1:]
}

// NormalizePlanKey converts the one supported legacy billing key to the
// canonical Business key. Unknown input stays empty and cannot select a price.
func NormalizePlanKey(value string) string {
	switch strings.ToLower(strings.TrimSpace(value)) {
	case PlanStarter:
		return PlanStarter
	case PlanPro:
		return PlanPro
	case PlanBusiness, "scale":
		return PlanBusiness
	default:
		return ""
	}
}

// PriceID returns the server-configured Stripe Price for an allowed plan and
// interval. The caller never supplies a Stripe Price ID directly.
func (config StripeConfig) PriceID(planKey, interval string) (string, bool) {
	planKey = NormalizePlanKey(planKey)
	if planKey == "" || (interval != IntervalMonthly && interval != IntervalAnnual) {
		return "", false
	}
	priceID, ok := config.PriceIDs[PriceKey{PlanKey: planKey, Interval: interval}]
	return priceID, ok && priceID != ""
}

type Customer struct {
	ID string
}

type CheckoutRequest struct {
	CustomerID       string
	OrganizationID   string
	OrganizationName string
	PlanKey          string
	Interval         string
	TrialDays        int
	SuccessURL       string
	CancelURL        string
	IdempotencyKey   string
}

type CheckoutSession struct {
	ID                string
	URL               string
	CustomerID        string
	SubscriptionID    string
	ClientReferenceID string
	Status            string
	PaymentStatus     string
}

type PortalSession struct {
	ID  string
	URL string
}

// SubscriptionSnapshot is retrieved from Stripe after a signed event. Its
// current price, status, and period end are authoritative when events arrive
// out of order or the customer changes their plan in Stripe's portal.
type SubscriptionSnapshot struct {
	ID                string
	CustomerID        string
	Status            string
	PriceID           string
	ItemID            string
	ScheduleID        string
	CancelAtPeriodEnd bool
	PendingUpdate     bool
	CurrentPeriodEnd  time.Time
}

// BillingEvent contains only provider fields used by the billing state
// projection. The signed Stripe object is converted to this shape before it
// crosses the payment boundary.
type BillingEvent struct {
	ID                string
	Type              string
	CreatedAt         time.Time
	CheckoutID        string
	CustomerID        string
	SubscriptionID    string
	ClientReferenceID string
	OrganizationID    string
	PlanKey           string
	Interval          string
	CheckoutStatus    string
	PaymentStatus     string
	SubscriptionState string
	PeriodEnd         time.Time
	ProviderCurrent   bool
	PendingUpdate     bool
	ObservedAt        time.Time
	Invoice           *core.Invoice
}

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

// BillingProvider isolates the two modes of billing from HTTP handlers. The
// demo path is implemented by the in-memory application store and never calls
// this provider.
type BillingProvider interface {
	CreateCustomer(context.Context, string, string, string) (Customer, error)
	CreateCheckoutSession(context.Context, CheckoutRequest) (CheckoutSession, error)
	ExpireCheckoutSession(context.Context, string) error
	RetrieveCheckoutSession(context.Context, string) (CheckoutSession, error)
	RetrieveSubscription(context.Context, string) (SubscriptionSnapshot, error)
	CreatePortalSession(context.Context, string, string, string) (PortalSession, error)
}

type stripeProvider struct {
	config StripeConfig
	client *stripe.Client
}

// NewStripeProvider creates the official stripe-go adapter. It refuses to
// create a live API client in demo mode, with no configured secret, or outside
// production, so a development server cannot accidentally mutate Stripe.
func NewStripeProvider(config StripeConfig, demo bool) (BillingProvider, error) {
	if demo || config.Demo {
		return nil, ErrDemoMode
	}
	if config.SecretKey == "" {
		return nil, ErrStripeUnavailable
	}
	if !config.Production {
		return nil, ErrStripeDevelopment
	}
	isTestKey := strings.HasPrefix(config.SecretKey, "sk_test_")
	isLiveKey := strings.HasPrefix(config.SecretKey, "sk_live_")
	if config.WebhookSecret == "" || (config.Mode == "test" && !isTestKey) || (config.Mode == "live" && !isLiveKey) || (config.Mode != "test" && config.Mode != "live") {
		return nil, ErrInvalidStripeOptions
	}
	return &stripeProvider{config: config, client: stripe.NewClient(config.SecretKey)}, nil
}

func (provider *stripeProvider) CreateCustomer(ctx context.Context, email, organizationID, idempotencyKey string) (Customer, error) {
	if ctx == nil || strings.TrimSpace(email) == "" || strings.TrimSpace(organizationID) == "" || strings.TrimSpace(idempotencyKey) == "" {
		return Customer{}, ErrInvalidBillingInput
	}
	params := &stripe.CustomerCreateParams{
		Email:    new(strings.TrimSpace(email)),
		Metadata: map[string]string{"organization_id": organizationID},
	}
	params.SetIdempotencyKey(idempotencyKey)
	created, err := provider.client.V1Customers.Create(ctx, params)
	if err != nil {
		return Customer{}, err
	}
	return Customer{ID: created.ID}, nil
}

func (provider *stripeProvider) CreateCheckoutSession(ctx context.Context, request CheckoutRequest) (CheckoutSession, error) {
	if ctx == nil || strings.TrimSpace(request.CustomerID) == "" || strings.TrimSpace(request.OrganizationID) == "" || strings.TrimSpace(request.IdempotencyKey) == "" {
		return CheckoutSession{}, ErrInvalidBillingInput
	}
	planKey := NormalizePlanKey(request.PlanKey)
	if planKey == "" || (request.Interval != IntervalMonthly && request.Interval != IntervalAnnual) || request.TrialDays < 0 {
		return CheckoutSession{}, ErrInvalidBillingInput
	}
	priceID, ok := provider.config.PriceID(planKey, request.Interval)
	if !ok {
		return CheckoutSession{}, ErrUnconfiguredPrice
	}
	successURL, err := absoluteHTTPURL(request.SuccessURL)
	if err != nil {
		return CheckoutSession{}, ErrInvalidBillingInput
	}
	cancelURL, err := absoluteHTTPURL(request.CancelURL)
	if err != nil {
		return CheckoutSession{}, ErrInvalidBillingInput
	}

	metadata := map[string]string{
		"organization_id":   request.OrganizationID,
		"organization_name": request.OrganizationName,
		"plan_key":          planKey,
		"interval":          request.Interval,
	}
	params := &stripe.CheckoutSessionCreateParams{
		Mode:                    new("subscription"),
		PaymentMethodCollection: new("always"),
		Customer:                new(request.CustomerID),
		ClientReferenceID:       new(request.OrganizationID),
		LineItems: []*stripe.CheckoutSessionCreateLineItemParams{{
			Price:    new(priceID),
			Quantity: new(int64(1)),
		}},
		SuccessURL: new(successURL),
		CancelURL:  new(cancelURL),
		Metadata:   metadata,
		SubscriptionData: &stripe.CheckoutSessionCreateSubscriptionDataParams{
			Metadata: map[string]string{
				"organization_id": request.OrganizationID,
				"plan_key":        planKey,
				"interval":        request.Interval,
			},
		},
	}
	if request.TrialDays > 0 {
		params.SubscriptionData.TrialPeriodDays = new(int64(request.TrialDays))
	}
	params.SetIdempotencyKey(request.IdempotencyKey)
	created, err := provider.client.V1CheckoutSessions.Create(ctx, params)
	if err != nil {
		return CheckoutSession{}, err
	}
	return checkoutSessionFromStripe(created), nil
}

func (provider *stripeProvider) RetrieveCheckoutSession(ctx context.Context, sessionID string) (CheckoutSession, error) {
	if ctx == nil || strings.TrimSpace(sessionID) == "" {
		return CheckoutSession{}, ErrInvalidBillingInput
	}
	session, err := provider.client.V1CheckoutSessions.Retrieve(ctx, sessionID, nil)
	if err != nil {
		return CheckoutSession{}, err
	}
	return checkoutSessionFromStripe(session), nil
}

func (provider *stripeProvider) ExpireCheckoutSession(ctx context.Context, sessionID string) error {
	if ctx == nil || strings.TrimSpace(sessionID) == "" {
		return ErrInvalidBillingInput
	}
	_, err := provider.client.V1CheckoutSessions.Expire(ctx, strings.TrimSpace(sessionID), nil)
	return err
}

func (provider *stripeProvider) RetrieveSubscription(ctx context.Context, subscriptionID string) (SubscriptionSnapshot, error) {
	if ctx == nil || strings.TrimSpace(subscriptionID) == "" {
		return SubscriptionSnapshot{}, ErrInvalidBillingInput
	}
	subscription, err := provider.client.V1Subscriptions.Retrieve(ctx, subscriptionID, nil)
	if err != nil {
		return SubscriptionSnapshot{}, err
	}
	snapshot := SubscriptionSnapshot{
		ID: subscription.ID, CustomerID: stripeCustomerID(subscription.Customer), Status: string(subscription.Status),
	}
	var priceID string
	if subscription.Items != nil && len(subscription.Items.Data) == 1 && subscription.Items.Data[0] != nil && subscription.Items.Data[0].Price != nil {
		priceID = subscription.Items.Data[0].Price.ID
		snapshot.ItemID = subscription.Items.Data[0].ID
	}
	snapshot.PriceID = priceID
	if subscription.Schedule != nil {
		snapshot.ScheduleID = subscription.Schedule.ID
	}
	snapshot.CancelAtPeriodEnd = subscription.CancelAtPeriodEnd
	snapshot.PendingUpdate = subscription.PendingUpdate != nil
	if subscription.Items != nil {
		for _, item := range subscription.Items.Data {
			if item != nil && item.CurrentPeriodEnd > 0 && (snapshot.CurrentPeriodEnd.IsZero() || time.Unix(item.CurrentPeriodEnd, 0).Before(snapshot.CurrentPeriodEnd)) {
				snapshot.CurrentPeriodEnd = time.Unix(item.CurrentPeriodEnd, 0).UTC()
			}
		}
	}
	return snapshot, nil
}

func (provider *stripeProvider) CreatePortalSession(ctx context.Context, customerID, returnURL, idempotencyKey string) (PortalSession, error) {
	if ctx == nil || strings.TrimSpace(customerID) == "" || strings.TrimSpace(idempotencyKey) == "" {
		return PortalSession{}, ErrInvalidBillingInput
	}
	validatedReturn, err := absoluteHTTPURL(returnURL)
	if err != nil {
		return PortalSession{}, ErrInvalidBillingInput
	}
	params := &stripe.BillingPortalSessionCreateParams{
		Customer:  new(customerID),
		ReturnURL: new(validatedReturn),
	}
	params.SetIdempotencyKey(idempotencyKey)
	created, err := provider.client.V1BillingPortalSessions.Create(ctx, params)
	if err != nil {
		return PortalSession{}, err
	}
	return PortalSession{ID: created.ID, URL: created.URL}, nil
}

func checkoutSessionFromStripe(session *stripe.CheckoutSession) CheckoutSession {
	result := CheckoutSession{
		ID:                session.ID,
		ClientReferenceID: session.ClientReferenceID,
		Status:            string(session.Status),
		PaymentStatus:     string(session.PaymentStatus),
	}
	if session.Customer != nil {
		result.CustomerID = session.Customer.ID
	}
	if session.Subscription != nil {
		result.SubscriptionID = session.Subscription.ID
	}
	if session.URL != "" {
		result.URL = session.URL
	}
	return result
}

func absoluteHTTPURL(value string) (string, error) {
	parsed, err := url.ParseRequestURI(strings.TrimSpace(value))
	if err != nil || parsed.Host == "" || (parsed.Scheme != "https" && parsed.Scheme != "http") {
		return "", ErrInvalidBillingInput
	}
	return parsed.String(), nil
}

// VerifyStripeWebhook verifies the raw request bytes with stripe-go's official
// signature parser and default five-minute timestamp tolerance. API version
// mismatch is ignored because this service projects a small stable event set.
func VerifyStripeWebhook(payload []byte, signature, secret string) (stripe.Event, error) {
	if len(payload) == 0 || strings.TrimSpace(signature) == "" || strings.TrimSpace(secret) == "" {
		return stripe.Event{}, ErrInvalidBillingInput
	}
	return webhook.ConstructEventWithOptions(payload, signature, secret, webhook.ConstructEventOptions{
		IgnoreAPIVersionMismatch: true,
		Tolerance:                5 * time.Minute,
	})
}

const maxStripeWebhookBody = 1 << 20

// Mount registers billing provider routes. It deliberately ignores Stripe
// environment settings in demo mode; no provider is created in that path.
func Mount(mux *http.ServeMux, deps core.Deps, flow BillingFlow) {
	config := StripeConfig{Demo: true, Mode: "demo"}
	var provider BillingProvider
	var configErr error
	if !deps.Demo {
		config, configErr = LoadStripeConfig(os.Getenv)
		if configErr == nil && !config.Demo {
			provider, configErr = NewStripeProvider(config, false)
		}
	}
	mount(mux, deps, flow, config, provider, configErr)
}

func mount(mux *http.ServeMux, deps core.Deps, flow BillingFlow, config StripeConfig, provider BillingProvider, configErr error) {
	mountPlanChanges(mux, deps, flow, config, provider, configErr)
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
		if configErr != nil || provider == nil {
			http.Error(w, "Billing is not configured.", http.StatusServiceUnavailable)
			return
		}
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
		if pendingID := organization.Billing.CheckoutID; pendingID != "" {
			pending, exists := organization.CheckoutSessions[pendingID]
			if !exists || pending.ID != pendingID || pending.OwnerUserID != principal.UserID || pending.OrganizationID != organization.ID || pending.Status != "open" {
				http.Error(w, "A checkout is already in progress for this organization.", http.StatusConflict)
				return
			}
			requestedPlanKey := NormalizePlanKey(planKey)
			if requestedPlanKey == "" || (interval != IntervalMonthly && interval != IntervalAnnual) {
				http.Error(w, "Choose an available plan and billing interval.", http.StatusBadRequest)
				return
			}
			pendingPlan, planExists := deps.Store.Plan(pending.PlanID)
			if !planExists || NormalizePlanKey(pendingPlan.Key) != requestedPlanKey || pending.Interval != interval {
				http.Error(w, "Finish the current checkout before choosing another plan.", http.StatusConflict)
				return
			}
			remote, err := provider.RetrieveCheckoutSession(r.Context(), pendingID)
			if err != nil {
				slog.Error("retrieve pending Stripe checkout", "session_id", pendingID, "error", err)
				http.Error(w, "Could not verify the existing checkout.", http.StatusBadGateway)
				return
			}
			if remote.ID != pendingID || remote.CustomerID != organization.Billing.CustomerID || remote.ClientReferenceID != organization.ID {
				http.Error(w, "Existing checkout is not bound to this organization.", http.StatusBadGateway)
				return
			}
			switch remote.Status {
			case "open":
				if _, err := absoluteHTTPURL(remote.URL); err != nil {
					http.Error(w, "Checkout provider returned an invalid URL.", http.StatusBadGateway)
					return
				}
				http.Redirect(w, r, remote.URL, http.StatusSeeOther)
				return
			case "complete":
				http.Redirect(w, r, "/billing/checkout/return?session_id="+url.QueryEscape(pendingID), http.StatusSeeOther)
				return
			case "expired":
				if err := flow.ExpireExternalCheckout(principal, pendingID); err != nil {
					writeBillingError(w, err)
					return
				}
			default:
				http.Error(w, "Existing checkout has an unsupported state.", http.StatusConflict)
				return
			}
		}
		plan, err := selectedPlan(deps.Store, planKey, interval)
		if err != nil {
			writeBillingError(w, err)
			return
		}
		if _, ok := config.PriceID(plan.Key, interval); !ok {
			http.Error(w, "Billing is not configured for this plan and interval.", http.StatusServiceUnavailable)
			return
		}
		returnPath, err = normalizeReturnPath(returnPath)
		if err != nil {
			writeBillingError(w, err)
			return
		}
		if config.PublicOrigin == "" {
			http.Error(w, "Billing return origin is not configured.", http.StatusServiceUnavailable)
			return
		}
		customerID := organization.Billing.CustomerID
		if customerID == "" {
			created, err := provider.CreateCustomer(r.Context(), account.User.Email, organization.ID, "customer:"+organization.ID)
			if err != nil {
				slog.Error("create Stripe customer", "error", err)
				http.Error(w, "Could not start checkout.", http.StatusBadGateway)
				return
			}
			customerID, err = flow.RecordCustomer(principal, created.ID)
			if err != nil {
				writeBillingError(w, err)
				return
			}
		}
		checkoutURL := config.PublicOrigin + "/billing/checkout/return?session_id={CHECKOUT_SESSION_ID}"
		cancelURL := config.PublicOrigin + returnPath
		if strings.Contains(cancelURL, "?") {
			cancelURL += "&payment=canceled"
		} else {
			cancelURL += "?payment=canceled"
		}
		trialDays := 0
		if !organization.Billing.TrialUsed {
			trialDays = plan.TrialDays
		}
		created, err := provider.CreateCheckoutSession(r.Context(), CheckoutRequest{
			CustomerID: customerID, OrganizationID: organization.ID, OrganizationName: organization.Name,
			PlanKey: plan.Key, Interval: interval, TrialDays: trialDays,
			SuccessURL: checkoutURL, CancelURL: cancelURL, IdempotencyKey: core.NewID("checkout"),
		})
		if err != nil {
			slog.Error("create Stripe checkout session", "error", err)
			http.Error(w, "Could not start checkout.", http.StatusBadGateway)
			return
		}
		expireUnboundSession := func() {
			if created.ID == "" {
				return
			}
			cleanupContext, cancel := context.WithTimeout(context.WithoutCancel(r.Context()), 5*time.Second)
			defer cancel()
			if err := provider.ExpireCheckoutSession(cleanupContext, created.ID); err != nil {
				slog.Warn("expire unbound Stripe checkout session", "session_id", created.ID, "error", err)
			}
		}
		if _, err := absoluteHTTPURL(created.URL); err != nil {
			expireUnboundSession()
			http.Error(w, "Checkout provider returned an invalid URL.", http.StatusBadGateway)
			return
		}
		if created.CustomerID != customerID || created.ClientReferenceID != organization.ID {
			expireUnboundSession()
			http.Error(w, "Checkout provider returned an unbound session.", http.StatusBadGateway)
			return
		}
		if err := flow.RecordExternalCheckout(principal, plan.Key, interval, returnPath, created); err != nil {
			expireUnboundSession()
			writeBillingError(w, err)
			return
		}
		http.Redirect(w, r, created.URL, http.StatusSeeOther)
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
		if configErr != nil || provider == nil {
			http.Error(w, "Billing is not configured.", http.StatusServiceUnavailable)
			return
		}
		if _, err := flow.CheckoutForReturn(principal, sessionID); err != nil {
			http.Error(w, http.StatusText(http.StatusNotFound), http.StatusNotFound)
			return
		}
		remote, err := provider.RetrieveCheckoutSession(r.Context(), sessionID)
		if err != nil {
			slog.Error("retrieve Stripe checkout session", "error", err)
			http.Error(w, "Could not verify checkout status.", http.StatusBadGateway)
			return
		}
		returnPath, state, err := flow.ReconcileCheckoutReturn(principal, sessionID, remote)
		if err != nil {
			writeBillingError(w, err)
			return
		}
		http.Redirect(w, r, withPaymentOutcome(returnPath, state), http.StatusSeeOther)
	})

	mux.HandleFunc("GET /billing/portal", func(w http.ResponseWriter, r *http.Request) {
		principal, ok := requestPrincipal(deps, w, r)
		if !ok {
			return
		}
		if !deps.Demo && (configErr != nil || provider == nil) {
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
		if organization.Billing.CustomerID == "" || config.PublicOrigin == "" {
			http.Error(w, "No billing account is available for this organization.", http.StatusConflict)
			return
		}
		portal, err := provider.CreatePortalSession(r.Context(), organization.Billing.CustomerID, config.PublicOrigin+"/billing", core.NewID("portal"))
		if err != nil {
			slog.Error("create Stripe billing portal", "error", err)
			http.Error(w, "Could not open the billing portal.", http.StatusBadGateway)
			return
		}
		if _, err := absoluteHTTPURL(portal.URL); err != nil {
			http.Error(w, "Billing provider returned an invalid URL.", http.StatusBadGateway)
			return
		}
		http.Redirect(w, r, portal.URL, http.StatusSeeOther)
	})

	mux.HandleFunc("POST /api/auth/stripe/webhook", func(w http.ResponseWriter, r *http.Request) {
		if configErr != nil || config.Demo || config.WebhookSecret == "" {
			http.Error(w, "Stripe webhook is not configured.", http.StatusServiceUnavailable)
			return
		}
		r.Body = http.MaxBytesReader(w, r.Body, maxStripeWebhookBody)
		payload, err := io.ReadAll(r.Body)
		if err != nil {
			if _, ok := errors.AsType[*http.MaxBytesError](err); ok {
				http.Error(w, "Stripe webhook body is too large.", http.StatusRequestEntityTooLarge)
				return
			}
			http.Error(w, "Could not read Stripe webhook.", http.StatusBadRequest)
			return
		}
		event, err := VerifyStripeWebhook(payload, r.Header.Get("Stripe-Signature"), config.WebhookSecret)
		if err != nil {
			http.Error(w, "Invalid Stripe webhook signature.", http.StatusBadRequest)
			return
		}
		if event.Livemode != (config.Mode == "live") {
			http.Error(w, "Stripe webhook mode mismatch.", http.StatusBadRequest)
			return
		}
		projection, handled, err := billingEventFromStripe(event)
		if err != nil {
			http.Error(w, "Invalid Stripe billing event.", http.StatusBadRequest)
			return
		}
		if handled {
			if projection.SubscriptionID != "" {
				if provider == nil {
					http.Error(w, "Stripe subscription verification is unavailable.", http.StatusServiceUnavailable)
					return
				}
				snapshot, err := provider.RetrieveSubscription(r.Context(), projection.SubscriptionID)
				if err != nil {
					slog.Error("retrieve Stripe subscription for webhook", "event_id", event.ID, "subscription_id", projection.SubscriptionID, "error", err)
					http.Error(w, "Could not verify Stripe subscription.", http.StatusBadGateway)
					return
				}
				if snapshot.ID != projection.SubscriptionID || snapshot.CustomerID == "" || (projection.CustomerID != "" && snapshot.CustomerID != projection.CustomerID) || snapshot.Status == "" {
					http.Error(w, "Stripe subscription snapshot does not match the signed event.", http.StatusBadGateway)
					return
				}
				projection.CustomerID = snapshot.CustomerID
				projection.SubscriptionState = snapshot.Status
				projection.PeriodEnd = snapshot.CurrentPeriodEnd
				projection.PlanKey, projection.Interval, _ = config.planFromPriceID(snapshot.PriceID)
				projection.ProviderCurrent = true
				projection.PendingUpdate = snapshot.PendingUpdate
				projection.ObservedAt = time.Now().UTC()
			}
			if err := flow.ApplyStripeEvent(projection); err != nil && !errors.Is(err, core.ErrAlreadyProcessed) && !errors.Is(err, core.ErrNotFound) {
				slog.Error("apply Stripe billing event", "event_id", event.ID, "error", err)
				http.Error(w, "Could not apply Stripe billing event.", http.StatusInternalServerError)
				return
			}
		}
		w.WriteHeader(http.StatusOK)
	})
}

func billingEventFromStripe(event stripe.Event) (BillingEvent, bool, error) {
	typeName := string(event.Type)
	projection := BillingEvent{ID: event.ID, Type: typeName, CreatedAt: time.Unix(event.Created, 0).UTC()}
	switch typeName {
	case "invoice.finalized", "invoice.paid", "invoice.payment_failed", "invoice.voided":
		var invoice stripe.Invoice
		if event.Data == nil || len(event.Data.Raw) == 0 || json.Unmarshal(event.Data.Raw, &invoice) != nil {
			return BillingEvent{}, false, errors.New("invalid invoice object")
		}
		projection.CustomerID = stripeCustomerID(invoice.Customer)
		if invoice.Parent != nil && invoice.Parent.SubscriptionDetails != nil {
			projection.SubscriptionID = stripeSubscriptionID(invoice.Parent.SubscriptionDetails.Subscription)
		}
		if projection.CustomerID == "" || projection.SubscriptionID == "" || invoice.ID == "" {
			// Other Stripe products can send one-off invoices to this endpoint.
			return BillingEvent{}, false, nil
		}
		projection.Invoice = &core.Invoice{
			ID: invoice.ID, Number: invoice.Number, Status: string(invoice.Status), AmountCents: invoice.AmountDue,
			Currency: string(invoice.Currency), CreatedAt: time.Unix(invoice.Created, 0).UTC(),
			PeriodStart: time.Unix(invoice.PeriodStart, 0).UTC(), PeriodEnd: time.Unix(invoice.PeriodEnd, 0).UTC(),
			HostedURL: invoice.HostedInvoiceURL, PDFURL: invoice.InvoicePDF,
		}
		return projection, true, nil
	case "checkout.session.completed", "checkout.session.async_payment_succeeded":
		var session stripe.CheckoutSession
		if event.Data == nil || len(event.Data.Raw) == 0 || json.Unmarshal(event.Data.Raw, &session) != nil {
			return BillingEvent{}, false, errors.New("invalid checkout session object")
		}
		projection.CheckoutID = session.ID
		projection.CustomerID = stripeCustomerID(session.Customer)
		projection.SubscriptionID = stripeSubscriptionID(session.Subscription)
		projection.ClientReferenceID = session.ClientReferenceID
		projection.OrganizationID = session.Metadata["organization_id"]
		projection.PlanKey = session.Metadata["plan_key"]
		projection.Interval = session.Metadata["interval"]
		projection.CheckoutStatus = string(session.Status)
		projection.PaymentStatus = string(session.PaymentStatus)
		return projection, true, nil
	case "customer.subscription.created", "customer.subscription.updated", "customer.subscription.deleted":
		var subscription stripe.Subscription
		if event.Data == nil || len(event.Data.Raw) == 0 || json.Unmarshal(event.Data.Raw, &subscription) != nil {
			return BillingEvent{}, false, errors.New("invalid subscription object")
		}
		projection.CustomerID = stripeCustomerID(subscription.Customer)
		projection.SubscriptionID = subscription.ID
		projection.OrganizationID = subscription.Metadata["organization_id"]
		projection.PlanKey = subscription.Metadata["plan_key"]
		projection.Interval = subscription.Metadata["interval"]
		projection.SubscriptionState = string(subscription.Status)
		if subscription.Items != nil {
			for _, item := range subscription.Items.Data {
				if item != nil && item.CurrentPeriodEnd > 0 && (projection.PeriodEnd.IsZero() || time.Unix(item.CurrentPeriodEnd, 0).Before(projection.PeriodEnd)) {
					projection.PeriodEnd = time.Unix(item.CurrentPeriodEnd, 0).UTC()
				}
			}
		}
		return projection, true, nil
	default:
		return BillingEvent{}, false, nil
	}
}

func (config StripeConfig) planFromPriceID(priceID string) (string, string, bool) {
	if priceID == "" {
		return "", "", false
	}
	planKey, interval := "", ""
	for key, configuredID := range config.PriceIDs {
		if configuredID != priceID {
			continue
		}
		if planKey != "" && (planKey != NormalizePlanKey(key.PlanKey) || interval != key.Interval) {
			return "", "", false
		}
		planKey, interval = NormalizePlanKey(key.PlanKey), key.Interval
	}
	return planKey, interval, planKey != "" && (interval == IntervalMonthly || interval == IntervalAnnual)
}

func stripeCustomerID(value *stripe.Customer) string {
	if value == nil {
		return ""
	}
	return value.ID
}

func stripeSubscriptionID(value *stripe.Subscription) string {
	if value == nil {
		return ""
	}
	return value.ID
}

func billingForm(w http.ResponseWriter, r *http.Request) bool {
	r.Body = http.MaxBytesReader(w, r.Body, maxStripeWebhookBody)
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
	key = strings.ToLower(strings.TrimSpace(key))
	if key == "scale" {
		if _, exists := store.Plan(key); exists {
			return core.Plan{}, core.ErrInvalid
		}
		key = "business"
	}
	if interval != "monthly" && interval != "annual" {
		return core.Plan{}, core.ErrInvalid
	}
	plan, ok := store.Plan(key)
	if !ok || !plan.Visible || plan.PriceCents <= 0 || (interval == "annual" && plan.AnnualPriceCents <= 0) {
		return core.Plan{}, core.ErrInvalid
	}
	return plan, nil
}

func normalizeReturnPath(value string) (string, error) {
	if strings.TrimSpace(value) == "" {
		value = "/billing"
	}
	if strings.ContainsAny(value, "\\\r\n") {
		return "", errors.New("invalid return path")
	}
	parsed, err := url.ParseRequestURI(value)
	if err != nil || parsed.IsAbs() || parsed.Host != "" || parsed.User != nil || parsed.Fragment != "" || !strings.HasPrefix(parsed.Path, "/") || strings.HasPrefix(parsed.Path, "//") {
		return "", errors.New("invalid return path")
	}
	decoded, err := url.PathUnescape(parsed.EscapedPath())
	if err != nil || strings.HasPrefix(decoded, "//") || strings.Contains(decoded, "\\") {
		return "", errors.New("invalid return path")
	}
	return parsed.RequestURI(), nil
}

func withPaymentOutcome(returnPath, outcome string) string {
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
	case errors.Is(err, core.ErrInvalid):
		http.Error(w, http.StatusText(http.StatusBadRequest), http.StatusBadRequest)
	default:
		http.Error(w, http.StatusText(http.StatusConflict), http.StatusConflict)
	}
}
