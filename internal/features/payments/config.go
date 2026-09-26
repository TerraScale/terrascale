package payments

import (
	"errors"
	"fmt"
	"net"
	"net/url"
	"strings"
)

const (
	PlanStarter  = "starter"
	PlanPro      = "pro"
	PlanBusiness = "business"

	IntervalMonthly = "monthly"
	IntervalAnnual  = "annual"

	ModeTest = "test"
	ModeLive = "live"
)

var (
	paidPlans = []string{PlanStarter, PlanPro, PlanBusiness}
	intervals = []string{IntervalMonthly, IntervalAnnual}
)

// PriceKey identifies the Stripe Price selected by trusted server state.
type PriceKey struct {
	PlanKey  string
	Interval string
}

// StripeConfig contains only server-side provider settings. It is loaded once
// at startup and never in demo mode, so a production demo cannot contact Stripe.
type StripeConfig struct {
	Mode          string
	SecretKey     string
	WebhookSecret string
	PublicOrigin  string
	PriceIDs      map[PriceKey]string
	State         StateScope
}

// StateScope locates the dashboard's own TerraBase database, where synced
// Stripe state is stored.
type StateScope struct {
	TenantID   string
	ProjectID  string
	DatabaseID string
}

// LoadStripeConfig reads and validates every setting the live billing flow
// needs. It reports all problems at once so a misconfigured deploy fails at
// startup with one actionable message instead of degrading at request time.
func LoadStripeConfig(lookup func(string) string) (StripeConfig, error) {
	get := func(name string) string { return strings.TrimSpace(lookup(name)) }
	config := StripeConfig{
		Mode:          strings.ToLower(get("STRIPE_MODE")),
		SecretKey:     get("STRIPE_SECRET_KEY"),
		WebhookSecret: get("STRIPE_WEBHOOK_SECRET"),
		PriceIDs:      make(map[PriceKey]string, len(paidPlans)*len(intervals)),
		State: StateScope{
			TenantID:   get("TS_BILLING_TENANT_ID"),
			ProjectID:  get("TS_BILLING_PROJECT_ID"),
			DatabaseID: get("TS_BILLING_DATABASE_ID"),
		},
	}
	var problems []error
	if config.Mode != ModeTest && config.Mode != ModeLive {
		problems = append(problems, errors.New("STRIPE_MODE must be test or live"))
	} else if !validSecretKey(config.SecretKey, config.Mode) {
		problems = append(problems, fmt.Errorf("STRIPE_SECRET_KEY must be a %[1]s-mode secret or restricted key (sk_%[1]s_… or rk_%[1]s_…)", config.Mode))
	}
	if !strings.HasPrefix(config.WebhookSecret, "whsec_") {
		problems = append(problems, errors.New("STRIPE_WEBHOOK_SECRET must be the endpoint signing secret (whsec_…)"))
	}
	for _, setting := range []struct{ name, value string }{
		{"TS_BILLING_TENANT_ID", config.State.TenantID},
		{"TS_BILLING_PROJECT_ID", config.State.ProjectID},
		{"TS_BILLING_DATABASE_ID", config.State.DatabaseID},
	} {
		if setting.value == "" {
			problems = append(problems, fmt.Errorf("%s must name the TerraBase scope that stores Stripe state", setting.name))
		}
	}
	if config.State.DatabaseID != "" && !validDatabaseName(config.State.DatabaseID) {
		problems = append(problems, errors.New("TS_BILLING_DATABASE_ID must be a TerraBase database name: 1-63 lowercase letters, digits, or dashes"))
	}
	origin, err := publicOrigin(get("DASHBOARD_PUBLIC_ORIGIN"))
	if err != nil {
		problems = append(problems, err)
	}
	config.PublicOrigin = origin

	owners := make(map[string]string, len(paidPlans)*len(intervals))
	for _, plan := range paidPlans {
		for _, interval := range intervals {
			name := PriceEnvName(plan, interval)
			priceID := get(name)
			switch {
			case !strings.HasPrefix(priceID, "price_"):
				problems = append(problems, fmt.Errorf("%s must be a Stripe price ID (price_…)", name))
			case owners[priceID] != "":
				problems = append(problems, fmt.Errorf("%s reuses the price configured for %s", name, owners[priceID]))
			default:
				owners[priceID] = name
				config.PriceIDs[PriceKey{PlanKey: plan, Interval: interval}] = priceID
			}
		}
	}
	if len(problems) > 0 {
		return StripeConfig{}, fmt.Errorf("%w: %w", ErrInvalidStripeOptions, errors.Join(problems...))
	}
	return config, nil
}

// PriceEnvName is the environment variable holding a plan's Stripe Price ID.
func PriceEnvName(planKey, interval string) string {
	return "STRIPE_PRICE_" + strings.ToUpper(planKey) + "_" + strings.ToUpper(interval)
}

// validDatabaseName mirrors TerraBase's database identifier rule, which ts-go
// enforces on schema calls.
func validDatabaseName(name string) bool {
	if len(name) == 0 || len(name) > 63 || name[0] == '-' || name[len(name)-1] == '-' {
		return false
	}
	for _, char := range name {
		if !(char >= 'a' && char <= 'z' || char >= '0' && char <= '9' || char == '-') {
			return false
		}
	}
	return true
}

func validSecretKey(key, mode string) bool {
	return strings.HasPrefix(key, "sk_"+mode+"_") || strings.HasPrefix(key, "rk_"+mode+"_")
}

// publicOrigin accepts an HTTPS origin, or plain HTTP on loopback for a local
// `stripe listen` session. Stripe redirects customers back to this origin.
func publicOrigin(value string) (string, error) {
	invalid := errors.New("DASHBOARD_PUBLIC_ORIGIN must be an HTTPS origin such as https://dashboard.terrascale.tech")
	parsed, err := url.Parse(value)
	if err != nil || parsed.Host == "" || parsed.User != nil || strings.Trim(parsed.Path, "/") != "" || parsed.RawQuery != "" || parsed.Fragment != "" {
		return "", invalid
	}
	switch parsed.Scheme {
	case "https":
	case "http":
		if ip := net.ParseIP(parsed.Hostname()); parsed.Hostname() != "localhost" && (ip == nil || !ip.IsLoopback()) {
			return "", invalid
		}
	default:
		return "", invalid
	}
	return parsed.Scheme + "://" + parsed.Host, nil
}

// PlanChangesEnabled keeps in-place plan changes limited to Stripe test mode
// until the staging gate in docs/stripe-sandbox-dsh207.md passes.
func (config StripeConfig) PlanChangesEnabled() bool {
	return config.Mode == ModeTest
}

// PriceID returns the server-configured Stripe Price for an allowed plan and
// interval. The caller never supplies a Stripe Price ID directly.
func (config StripeConfig) PriceID(planKey, interval string) (string, bool) {
	priceID, ok := config.PriceIDs[PriceKey{PlanKey: NormalizePlanKey(planKey), Interval: interval}]
	return priceID, ok
}

// planFromPriceID maps a Stripe Price back to the dashboard plan. Prices are
// unique by construction, so at most one key matches.
func (config StripeConfig) planFromPriceID(priceID string) (string, string, bool) {
	for key, configuredID := range config.PriceIDs {
		if priceID != "" && configuredID == priceID {
			return key.PlanKey, key.Interval, true
		}
	}
	return "", "", false
}

// NormalizePlanKey returns the canonical paid plan key, or empty for anything
// that cannot select a Stripe price.
func NormalizePlanKey(value string) string {
	switch value = strings.ToLower(strings.TrimSpace(value)); value {
	case PlanStarter, PlanPro, PlanBusiness:
		return value
	default:
		return ""
	}
}

// PlanRank orders plans so upgrades apply now and downgrades at renewal.
func PlanRank(key string) int {
	switch key {
	case PlanStarter:
		return 1
	case PlanPro:
		return 2
	case PlanBusiness:
		return 3
	default:
		return 0
	}
}

// IsEntitledStatus reports whether a Stripe subscription status grants the
// plan's entitlements.
func IsEntitledStatus(status string) bool { return status == "active" || status == "trialing" }

// IsDunningStatus reports whether Stripe is still trying to collect payment
// for a subscription that should retain its selected plan while billing is
// resolved.
func IsDunningStatus(status string) bool {
	switch status {
	case "past_due", "unpaid", "paused":
		return true
	default:
		return false
	}
}
