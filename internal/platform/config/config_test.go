package config

import "testing"

func TestPublicURLConfiguration(t *testing.T) {
	t.Setenv("TS_DASHBOARD_PRODUCTION_DEMO", "1")
	t.Setenv("FLY_APP_NAME", "")
	t.Setenv("DASHBOARD_DEV", "0")
	t.Setenv("DASHBOARD_SECURE_COOKIES", "")
	t.Setenv("DASHBOARD_DEMO_ADMIN_EMAIL", "")
	t.Setenv("DASHBOARD_DEMO_ADMIN_PASSWORD", "")
	for _, tc := range []struct {
		input, origin string
		invalid       bool
	}{
		{"", "", false},
		{" https://dashboard.example/ ", "https://dashboard.example", false},
		{"http://localhost:5481/", "http://localhost:5481", false},
		{"http://[::1]:5481", "http://[::1]:5481", false},
		{"//dashboard.example", "", true},
		{"ftp://dashboard.example", "", true},
		{"https://", "", true},
		{"https://user:password@dashboard.example", "", true},
		{"https://dashboard.example/path", "", true},
		{"https://dashboard.example?redirect=evil", "", true},
		{"https://dashboard.example?", "", true},
		{"https://dashboard.example#fragment", "", true},
		{"https://dashboard.example#", "", true},
	} {
		t.Run(tc.input, func(t *testing.T) {
			t.Setenv("DASHBOARD_PUBLIC_URL", tc.input)
			cfg, err := Load()
			if (err != nil) != tc.invalid || cfg.PublicURL != tc.origin {
				t.Fatalf("origin=%q error=%v", cfg.PublicURL, err)
			}
			if tc.origin == "https://dashboard.example" && !cfg.SecureCookies {
				t.Fatal("normalized HTTPS origin did not enable secure cookies")
			}
		})
	}
	t.Setenv("DASHBOARD_PUBLIC_URL", "")
	t.Setenv("FLY_APP_NAME", "dashboard-preview")
	if cfg, err := Load(); err != nil || cfg.PublicURL != "https://dashboard-preview.fly.dev" {
		t.Fatalf("Fly origin=%q error=%v", cfg.PublicURL, err)
	}
	t.Setenv("DASHBOARD_PUBLIC_URL", "https://dashboard.example")
	if cfg, err := Load(); err != nil || cfg.PublicURL != "https://dashboard.example" {
		t.Fatalf("explicit origin=%q error=%v", cfg.PublicURL, err)
	}
}

func TestErrorTrackingConfiguration(t *testing.T) {
	t.Setenv("TS_DASHBOARD_PRODUCTION_DEMO", "1")
	t.Setenv("DASHBOARD_PUBLIC_URL", "")
	t.Setenv("DASHBOARD_DEMO_ADMIN_EMAIL", "")
	t.Setenv("DASHBOARD_DEMO_ADMIN_PASSWORD", "")
	t.Setenv("SENTRY_DSN", " https://key@glitchtip.example/1 ")
	t.Setenv("SENTRY_ENVIRONMENT", "")
	t.Setenv("DASHBOARD_CSP_REPORT_URL", "")
	t.Setenv("FLY_APP_NAME", "")
	cfg, err := Load()
	if err != nil || cfg.SentryDSN != "https://key@glitchtip.example/1" || cfg.SentryEnvironment != "development" || cfg.CSPReportURL != "" {
		t.Fatalf("local config=%+v error=%v", cfg, err)
	}
	t.Setenv("FLY_APP_NAME", "ts-dashboard")
	if cfg, err := Load(); err != nil || cfg.SentryEnvironment != "production" {
		t.Fatalf("Fly environment=%q error=%v", cfg.SentryEnvironment, err)
	}
	t.Setenv("SENTRY_ENVIRONMENT", "preview")
	if cfg, err := Load(); err != nil || cfg.SentryEnvironment != "preview" {
		t.Fatalf("explicit environment=%q error=%v", cfg.SentryEnvironment, err)
	}
	for _, tc := range []struct {
		input   string
		invalid bool
	}{
		{"https://glitchtip.example/api/1/security/?glitchtip_key=abc", false},
		{"http://localhost:8000/report", false},
		{"/csp-report", true},
		{"ftp://glitchtip.example/report", true},
		{"https://user:pass@glitchtip.example/report", true},
		{"https://glitchtip.example/report; script-src *", true},
		{"https://glitchtip.example/report,default-src *", true},
		{"https://glitchtip.example/report'", true},
	} {
		t.Run(tc.input, func(t *testing.T) {
			t.Setenv("DASHBOARD_CSP_REPORT_URL", tc.input)
			cfg, err := Load()
			if (err != nil) != tc.invalid || (!tc.invalid && cfg.CSPReportURL != tc.input) {
				t.Fatalf("report URL=%q error=%v", cfg.CSPReportURL, err)
			}
		})
	}
}
