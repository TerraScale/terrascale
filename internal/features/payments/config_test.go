package payments

import (
	"errors"
	"strings"
	"testing"
)

func validStripeEnv() map[string]string {
	env := map[string]string{
		"STRIPE_MODE":             "test",
		"STRIPE_SECRET_KEY":       "sk_test_123",
		"STRIPE_WEBHOOK_SECRET":   "whsec_123",
		"DASHBOARD_PUBLIC_ORIGIN": "https://dashboard.example.test/",
		"TS_BILLING_TENANT_ID":    "tenant_dashboard",
		"TS_BILLING_PROJECT_ID":   "project_billing",
		"TS_BILLING_DATABASE_ID":  "dashboard-billing",
	}
	for _, plan := range paidPlans {
		for _, interval := range intervals {
			env[PriceEnvName(plan, interval)] = "price_" + plan + "_" + interval
		}
	}
	return env
}

func TestLoadStripeConfigAcceptsCompleteSettings(t *testing.T) {
	config, err := LoadStripeConfig(func(name string) string { return validStripeEnv()[name] })
	if err != nil {
		t.Fatal(err)
	}
	if config.PublicOrigin != "https://dashboard.example.test" || len(config.PriceIDs) != 6 || !config.PlanChangesEnabled() ||
		config.State != (StateScope{TenantID: "tenant_dashboard", ProjectID: "project_billing", DatabaseID: "dashboard-billing"}) {
		t.Fatalf("config = %#v", config)
	}
	if priceID, ok := config.PriceID("Business", IntervalAnnual); !ok || priceID != "price_business_annual" {
		t.Fatalf("business annual price = %q, %v", priceID, ok)
	}
	if plan, interval, ok := config.planFromPriceID("price_pro_monthly"); !ok || plan != PlanPro || interval != IntervalMonthly {
		t.Fatalf("price lookup = %q %q %v", plan, interval, ok)
	}
}

func TestLoadStripeConfigRejectsInvalidSettings(t *testing.T) {
	tests := []struct {
		name    string
		change  map[string]string
		problem string
	}{
		{"missing mode", map[string]string{"STRIPE_MODE": ""}, "STRIPE_MODE"},
		{"live key in test mode", map[string]string{"STRIPE_SECRET_KEY": "sk_live_123"}, "STRIPE_SECRET_KEY"},
		{"test key in live mode", map[string]string{"STRIPE_MODE": "live"}, "STRIPE_SECRET_KEY"},
		{"publishable key", map[string]string{"STRIPE_SECRET_KEY": "pk_test_123"}, "STRIPE_SECRET_KEY"},
		{"webhook secret", map[string]string{"STRIPE_WEBHOOK_SECRET": "secret"}, "STRIPE_WEBHOOK_SECRET"},
		{"plain HTTP origin", map[string]string{"DASHBOARD_PUBLIC_ORIGIN": "http://dashboard.example.test"}, "DASHBOARD_PUBLIC_ORIGIN"},
		{"origin with path", map[string]string{"DASHBOARD_PUBLIC_ORIGIN": "https://dashboard.example.test/app"}, "DASHBOARD_PUBLIC_ORIGIN"},
		{"missing price", map[string]string{"STRIPE_PRICE_PRO_ANNUAL": ""}, "STRIPE_PRICE_PRO_ANNUAL"},
		{"duplicate price", map[string]string{"STRIPE_PRICE_PRO_ANNUAL": "price_pro_monthly"}, "STRIPE_PRICE_PRO_ANNUAL reuses"},
		{"missing state database", map[string]string{"TS_BILLING_DATABASE_ID": ""}, "TS_BILLING_DATABASE_ID"},
		{"invalid state database", map[string]string{"TS_BILLING_DATABASE_ID": "Dashboard_Billing"}, "TS_BILLING_DATABASE_ID"},
	}
	for _, test := range tests {
		t.Run(test.name, func(t *testing.T) {
			env := validStripeEnv()
			for name, value := range test.change {
				env[name] = value
			}
			_, err := LoadStripeConfig(func(name string) string { return env[name] })
			if !errors.Is(err, ErrInvalidStripeOptions) || !strings.Contains(err.Error(), test.problem) {
				t.Fatalf("error = %v, want %q", err, test.problem)
			}
		})
	}
}

func TestLoadStripeConfigReportsEveryProblem(t *testing.T) {
	_, err := LoadStripeConfig(func(string) string { return "" })
	for _, name := range []string{"STRIPE_MODE", "STRIPE_WEBHOOK_SECRET", "DASHBOARD_PUBLIC_ORIGIN", "STRIPE_PRICE_STARTER_MONTHLY", "STRIPE_PRICE_BUSINESS_ANNUAL", "TS_BILLING_TENANT_ID"} {
		if err == nil || !strings.Contains(err.Error(), name) {
			t.Fatalf("error %v does not mention %s", err, name)
		}
	}
}

func TestLoadStripeConfigAcceptsRestrictedKeysAndLoopbackHTTP(t *testing.T) {
	env := validStripeEnv()
	env["STRIPE_MODE"] = "live"
	env["STRIPE_SECRET_KEY"] = "rk_live_123"
	env["DASHBOARD_PUBLIC_ORIGIN"] = "http://127.0.0.1:5481"
	config, err := LoadStripeConfig(func(name string) string { return env[name] })
	if err != nil {
		t.Fatal(err)
	}
	if config.PublicOrigin != "http://127.0.0.1:5481" || config.PlanChangesEnabled() {
		t.Fatalf("config = %#v", config)
	}
}

func TestNewIgnoresStripeSettingsInDemoMode(t *testing.T) {
	billing, err := New(t.Context(), true, func(string) string { return "invalid" }, nil)
	if err != nil || billing != nil {
		t.Fatalf("demo billing = %v, %v", billing, err)
	}
	if _, err := New(t.Context(), false, func(string) string { return "" }, nil); !errors.Is(err, ErrInvalidStripeOptions) {
		t.Fatalf("live billing without settings error = %v", err)
	}
	if _, err := New(t.Context(), false, func(name string) string { return validStripeEnv()[name] }, nil); !errors.Is(err, ErrInvalidStripeOptions) {
		t.Fatalf("live billing without ts-go error = %v", err)
	}
}
