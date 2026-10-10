package payments

import (
	"context"
	"errors"
	"fmt"
	"log/slog"
	"net"
	"net/http"
	"time"

	"terrascale.tech/dashboard/internal/core"
	"terrascale.tech/sdk/v1"
)

var (
	ErrStripeUnavailable    = errors.New("Stripe is not configured")
	ErrInvalidBillingInput  = errors.New("invalid billing input")
	ErrUnconfiguredPrice    = errors.New("no Stripe price is configured for this plan and interval")
	ErrInvalidStripeOptions = errors.New("invalid Stripe configuration")
)

// Stripe is the live billing provider, built once at startup and shared by
// every billing route. A nil *Stripe disables provider billing; demo mode
// instead uses the Store-backed simulator.
type Stripe struct {
	config   StripeConfig
	provider BillingProvider
	store    StateStore
}

// New loads the Stripe configuration, creates the official stripe-go client,
// and connects the TerraBase state store through ts-go. Demo mode returns nil
// and ignores every Stripe setting. Unconfigured live billing stays disabled.
func New(_ context.Context, demo bool, lookup func(string) string, db *v1.Database) (*Stripe, error) {
	if demo || (lookup("STRIPE_MODE") == "" && lookup("STRIPE_SECRET_KEY") == "" && lookup("STRIPE_WEBHOOK_SECRET") == "") {
		return nil, nil
	}
	config, err := LoadStripeConfig(lookup)
	if err != nil {
		return nil, err
	}
	if db == nil {
		return nil, fmt.Errorf("%w: TerraBase persistence is required for Stripe state", ErrInvalidStripeOptions)
	}
	store := newTerraBaseStateStore(db)
	slog.Info("Stripe billing enabled", "mode", config.Mode, "public_origin", config.PublicOrigin, "state_database", config.State.DatabaseID)
	return newStripe(config, newStripeProvider(config), store), nil
}

func newStripe(config StripeConfig, provider BillingProvider, store StateStore) *Stripe {
	return &Stripe{config: config, provider: provider, store: store}
}

// PreviewPlanChange quotes an immediate upgrade against the same Stripe
// subscription the mutation route changes. The caller must authorize the
// organization owner first.
func (billing *Stripe) PreviewPlanChange(ctx context.Context, customerID, subscriptionID, planKey, interval string) (PlanChangeQuote, error) {
	if billing == nil || !billing.config.PlanChangesEnabled() {
		return PlanChangeQuote{}, ErrStripeUnavailable
	}
	priceID, ok := billing.config.PriceID(planKey, interval)
	if !ok {
		return PlanChangeQuote{}, ErrUnconfiguredPrice
	}
	current, err := billing.provider.RetrieveSubscription(ctx, subscriptionID)
	if err != nil {
		return PlanChangeQuote{}, err
	}
	if current.ID != subscriptionID || current.CustomerID != customerID || !IsEntitledStatus(current.Status) {
		return PlanChangeQuote{}, ErrInvalidBillingInput
	}
	return billing.provider.PreviewPlanChange(ctx, current, priceID)
}

// customerFor returns the organization's Stripe customer, creating it before
// the first checkout. The binding is stored create-only, so every checkout,
// webhook, and sync for the organization resolves to one customer even when
// two checkouts race. A bound customer is bound again so a binding an
// interrupted checkout left half written is completed before payment.
func (billing *Stripe) customerFor(ctx context.Context, email string, organization core.Organization) (string, error) {
	customerID, bound, err := billing.store.CustomerFor(ctx, organization.ID)
	if err != nil {
		return "", err
	}
	if !bound {
		created, err := billing.provider.CreateCustomer(ctx, CustomerRequest{
			Email: email, Name: organization.Name, OrganizationID: organization.ID,
			IdempotencyKey: "customer:" + organization.ID,
		})
		if err != nil {
			return "", err
		}
		customerID = created.ID
	}
	return billing.store.BindCustomer(ctx, organization.ID, customerID)
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
		slog.WarnContext(ctx, "expire unbound Stripe checkout session", "session_id", sessionID, "error", err)
	}
}

func newStripeHTTPClient() *http.Client {
	return &http.Client{
		Timeout: stripeRequestTimeout,
		Transport: &http.Transport{
			Proxy:               http.ProxyFromEnvironment,
			DialContext:         (&net.Dialer{Timeout: 5 * time.Second, KeepAlive: 30 * time.Second}).DialContext,
			ForceAttemptHTTP2:   true,
			TLSHandshakeTimeout: 5 * time.Second,
			MaxIdleConnsPerHost: 8,
			IdleConnTimeout:     90 * time.Second,
		},
	}
}

// slogStripeLogger routes stripe-go diagnostics into the structured log.
// Per-request info lines are demoted to debug to keep production logs quiet.
type slogStripeLogger struct{}

func (slogStripeLogger) Debugf(ctx context.Context, format string, v ...any) {
	slog.DebugContext(ctx, fmt.Sprintf(format, v...), "component", "stripe")
}

func (slogStripeLogger) Infof(ctx context.Context, format string, v ...any) {
	slog.DebugContext(ctx, fmt.Sprintf(format, v...), "component", "stripe")
}

func (slogStripeLogger) Warnf(ctx context.Context, format string, v ...any) {
	slog.WarnContext(ctx, fmt.Sprintf(format, v...), "component", "stripe")
}

func (slogStripeLogger) Errorf(ctx context.Context, format string, v ...any) {
	slog.ErrorContext(ctx, fmt.Sprintf(format, v...), "component", "stripe")
}
