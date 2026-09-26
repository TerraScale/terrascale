package payments

import (
	"context"
	"errors"
	"fmt"
	"log/slog"
	"net"
	"net/http"
	"time"
)

var (
	ErrStripeUnavailable    = errors.New("Stripe is not configured")
	ErrInvalidBillingInput  = errors.New("invalid billing input")
	ErrUnconfiguredPrice    = errors.New("no Stripe price is configured for this plan and interval")
	ErrInvalidStripeOptions = errors.New("invalid Stripe configuration")
)

// Stripe is the live billing provider, built once at startup and shared by
// every billing route. A nil *Stripe means demo mode: billing runs through the
// in-memory simulator and nothing contacts Stripe.
type Stripe struct {
	config   StripeConfig
	provider BillingProvider
}

// New loads the Stripe configuration and creates the official stripe-go
// client. Demo mode returns nil and ignores every Stripe setting.
func New(demo bool, lookup func(string) string) (*Stripe, error) {
	if demo {
		return nil, nil
	}
	config, err := LoadStripeConfig(lookup)
	if err != nil {
		return nil, err
	}
	slog.Info("Stripe billing enabled", "mode", config.Mode, "public_origin", config.PublicOrigin)
	return newStripe(config, newStripeProvider(config)), nil
}

func newStripe(config StripeConfig, provider BillingProvider) *Stripe {
	return &Stripe{config: config, provider: provider}
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
