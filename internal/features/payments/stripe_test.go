package payments

import (
	"encoding/hex"
	"fmt"
	"testing"
	"time"

	"github.com/stripe/stripe-go/v86/webhook"
)

func TestLoadStripeConfigForcesDemoBeforeReadingSecrets(t *testing.T) {
	values := map[string]string{
		"TS_DASHBOARD_PRODUCTION_DEMO": "1",
		"APP_ENV":                      "production",
		"STRIPE_SECRET_KEY":            "sk_live_should_be_ignored",
		"STRIPE_WEBHOOK_SECRET":        "whsec_should_be_ignored",
		"STRIPE_PRICE_PRO_MONTHLY":     "price_should_be_ignored",
	}

	config, err := LoadStripeConfig(func(key string) string { return values[key] })
	if err != nil {
		t.Fatalf("LoadStripeConfig() error = %v", err)
	}
	if !config.Demo || config.Mode != "demo" || config.SecretKey != "" || config.WebhookSecret != "" || len(config.PriceIDs) != 0 {
		t.Fatalf("demo config retained Stripe settings: %#v", config)
	}
}

func TestLoadStripeConfigDefaultsToDemoWithoutProductionOrStripeKey(t *testing.T) {
	for name, values := range map[string]map[string]string{
		"no environment": {},
		"development with credentials": {
			"APP_ENV":               "development",
			"STRIPE_SECRET_KEY":     "sk_test_never_used",
			"STRIPE_WEBHOOK_SECRET": "whsec_never_used",
		},
		"production without key": {"APP_ENV": "production"},
	} {
		t.Run(name, func(t *testing.T) {
			config, err := LoadStripeConfig(func(key string) string { return values[key] })
			if err != nil {
				t.Fatalf("LoadStripeConfig() error = %v", err)
			}
			if !config.Demo || config.SecretKey != "" {
				t.Fatalf("expected network-free demo config, got %#v", config)
			}
		})
	}
}

func TestLoadStripeConfigValidatesProductionTestPricesAndLegacyNames(t *testing.T) {
	values := map[string]string{
		"APP_ENV":                                  "production",
		"Payments__Stripe__SecretKey":              "sk_test_dashboard",
		"Payments__Stripe__WebhookSecret":          "whsec_dashboard",
		"Payments__Stripe__ProAnnualPriceId":       "price_pro_annual",
		"Payments__Stripe__BusinessMonthlyPriceId": "price_business_monthly",
	}
	config, err := LoadStripeConfig(func(key string) string { return values[key] })
	if err != nil {
		t.Fatalf("LoadStripeConfig() error = %v", err)
	}
	if config.Demo || !config.Production || config.Mode != "test" {
		t.Fatalf("unexpected configured Stripe mode: %#v", config)
	}
	if priceID, ok := config.PriceID(" pro ", IntervalAnnual); !ok || priceID != "price_pro_annual" {
		t.Fatalf("Pro annual price = %q, %t", priceID, ok)
	}
	if priceID, ok := config.PriceID("business", IntervalMonthly); !ok || priceID != "price_business_monthly" {
		t.Fatalf("Business monthly price = %q, %t", priceID, ok)
	}
	if _, ok := config.PriceID("enterprise", IntervalMonthly); ok {
		t.Fatal("unknown plan unexpectedly resolved to a Stripe price")
	}
	if _, ok := config.PriceID(PlanPro, "weekly"); ok {
		t.Fatal("unknown interval unexpectedly resolved to a Stripe price")
	}
}

func TestLoadStripeConfigRejectsModeKeyAndPriceMismatches(t *testing.T) {
	cases := map[string]map[string]string{
		"test with live key": {
			"APP_ENV": "production", "STRIPE_MODE": "test", "STRIPE_SECRET_KEY": "sk_live_key", "STRIPE_WEBHOOK_SECRET": "whsec_key",
		},
		"live with test key": {
			"APP_ENV": "production", "STRIPE_MODE": "live", "STRIPE_SECRET_KEY": "sk_test_key", "STRIPE_WEBHOOK_SECRET": "whsec_key",
		},
		"invalid price ID": {
			"APP_ENV": "production", "STRIPE_SECRET_KEY": "sk_test_key", "STRIPE_WEBHOOK_SECRET": "whsec_key", "STRIPE_PRICE_PRO_MONTHLY": "prod_not_a_price",
		},
		"missing webhook secret": {
			"APP_ENV": "production", "STRIPE_SECRET_KEY": "sk_test_key",
		},
		"live key outside allowed production mode": {
			"APP_ENV": "production", "STRIPE_MODE": "live", "STRIPE_SECRET_KEY": "rk_live_key", "STRIPE_WEBHOOK_SECRET": "whsec_key",
		},
	}
	for name, values := range cases {
		t.Run(name, func(t *testing.T) {
			if _, err := LoadStripeConfig(func(key string) string { return values[key] }); err == nil {
				t.Fatal("LoadStripeConfig() succeeded for invalid settings")
			}
		})
	}
}

func TestNormalizePlanKeySupportsScaleAlias(t *testing.T) {
	if got := NormalizePlanKey(" SCALE "); got != PlanBusiness {
		t.Fatalf("NormalizePlanKey(scale) = %q", got)
	}
	if got := NormalizePlanKey("unknown"); got != "" {
		t.Fatalf("NormalizePlanKey(unknown) = %q", got)
	}
}

func TestStripeProviderCannotBeCreatedInDemoOrDevelopment(t *testing.T) {
	config := StripeConfig{SecretKey: "sk_test_no_network", Mode: "test", Production: true}
	if _, err := NewStripeProvider(config, true); err != ErrDemoMode {
		t.Fatalf("demo provider error = %v, want %v", err, ErrDemoMode)
	}
	config.Production = false
	if _, err := NewStripeProvider(config, false); err != ErrStripeDevelopment {
		t.Fatalf("development provider error = %v, want %v", err, ErrStripeDevelopment)
	}
}

func TestVerifyStripeWebhookAcceptsFreshSignature(t *testing.T) {
	payload := []byte(`{"id":"evt_fresh","object":"event","type":"invoice.paid","data":{"object":{}}}`)
	event, err := VerifyStripeWebhook(payload, signedHeader(payload, time.Now()), "whsec_test")
	if err != nil {
		t.Fatalf("VerifyStripeWebhook() error = %v", err)
	}
	if event.ID != "evt_fresh" || event.Type != "invoice.paid" {
		t.Fatalf("event = (%q, %q)", event.ID, event.Type)
	}
}

func TestVerifyStripeWebhookRejectsWrongSecretStaleAndMalformedSignatures(t *testing.T) {
	payload := []byte(`{"id":"evt_test","object":"event","type":"invoice.paid","data":{"object":{}}}`)
	cases := map[string]string{
		"wrong secret":     signedHeader(payload, time.Now(), "whsec_other"),
		"stale signature":  signedHeader(payload, time.Now().Add(-30*time.Minute)),
		"malformed header": "not-a-signature",
		"empty header":     "",
	}
	for name, header := range cases {
		t.Run(name, func(t *testing.T) {
			if _, err := VerifyStripeWebhook(payload, header, "whsec_test"); err == nil {
				t.Fatal("VerifyStripeWebhook() accepted an invalid signature")
			}
		})
	}
	if _, err := VerifyStripeWebhook(nil, signedHeader(payload, time.Now()), "whsec_test"); err == nil {
		t.Fatal("VerifyStripeWebhook() accepted an empty payload")
	}
}

func signedHeader(payload []byte, at time.Time, secrets ...string) string {
	secret := "whsec_test"
	if len(secrets) > 0 {
		secret = secrets[0]
	}
	signature := webhook.ComputeSignature(at, payload, secret)
	return fmt.Sprintf("t=%d,v1=%s", at.Unix(), hex.EncodeToString(signature))
}
