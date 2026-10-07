package config

import (
	"strings"
	"testing"
)

func TestPublicURLConfiguration(t *testing.T) {
	t.Setenv("TS_DASHBOARD_PRODUCTION_DEMO", "1")
	t.Setenv("FLY_APP_NAME", "")
	t.Setenv("DASHBOARD_DEV", "0")
	t.Setenv("DASHBOARD_SECURE_COOKIES", "")
	t.Setenv("DASHBOARD_DEMO_ADMIN_EMAIL", "")
	t.Setenv("DASHBOARD_DEMO_ADMIN_PASSWORD", "")
	t.Setenv("TS_DASHBOARD_CSRF_SECRET", "")
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
	t.Setenv("TS_DASHBOARD_CSRF_SECRET", "")
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

func TestCSRFSecretConfiguration(t *testing.T) {
	t.Setenv("FLY_APP_NAME", "")
	t.Setenv("DASHBOARD_CSP_REPORT_URL", "")
	t.Setenv("DASHBOARD_PUBLIC_URL", "")
	t.Setenv("DASHBOARD_DEMO_ADMIN_EMAIL", "")
	t.Setenv("DASHBOARD_DEMO_ADMIN_PASSWORD", "")
	secret := "0123456789abcdef0123456789abcdef"
	for _, tc := range []struct {
		name, demo, secret string
		want               string
		invalid            bool
	}{
		{name: "demo draws a random key", demo: "1"},
		{name: "demo shares a configured key", demo: "1", secret: secret, want: secret},
		{name: "short key", demo: "1", secret: "short", invalid: true},
		{name: "one byte short of the minimum", demo: "1", secret: secret[:31], invalid: true},
		{name: "exactly the minimum", demo: "1", secret: secret[:32], want: secret[:32]},
		{name: "unset backend keeps the memory demo", demo: ""},
	} {
		t.Run(tc.name, func(t *testing.T) {
			t.Setenv("TS_DASHBOARD_PRODUCTION_DEMO", tc.demo)
			t.Setenv("TS_DASHBOARD_CSRF_SECRET", tc.secret)
			cfg, err := Load()
			if (err != nil) != tc.invalid || string(cfg.CSRFSecret) != tc.want {
				t.Fatalf("secret=%q error=%v", cfg.CSRFSecret, err)
			}
		})
	}
}

func TestTerraBaseBackendConfiguration(t *testing.T) {
	for _, name := range []string{"DASHBOARD_PUBLIC_URL", "DASHBOARD_DEMO_ADMIN_EMAIL", "DASHBOARD_DEMO_ADMIN_PASSWORD", "DASHBOARD_CSP_REPORT_URL", "FLY_APP_NAME", "TS_DASHBOARD_CSRF_SECRET", "TERRABASE_URL", "TERRABASE_API_KEY", "TERRABASE_PROJECT_ID", "TERRABASE_DATABASE"} {
		t.Setenv(name, "")
	}
	cfg, err := Load()
	if err != nil || !cfg.Demo {
		t.Fatalf("unset backend: demo=%v err=%v", cfg.Demo, err)
	}
	t.Setenv("TERRABASE_URL", "http://127.0.0.1:5555")
	if _, err := Load(); err == nil {
		t.Fatal("partial config accepted")
	}
	t.Setenv("TERRABASE_API_KEY", "tb_"+strings.Repeat("a", 43))
	t.Setenv("TERRABASE_PROJECT_ID", "11111111-1111-4111-8111-111111111111")
	t.Setenv("TERRABASE_DATABASE", "dashboard")
	t.Setenv("TS_DASHBOARD_PRODUCTION_DEMO", "1")
	if _, err := Load(); err == nil {
		t.Fatal("persistent backend accepted without stable CSRF key")
	}
	t.Setenv("TS_DASHBOARD_CSRF_SECRET", strings.Repeat("a", 32))
	cfg, err = Load()
	if err != nil || cfg.Demo || cfg.TerraBaseDatabase != "dashboard" {
		t.Fatalf("persistent backend: demo=%v database=%q err=%v", cfg.Demo, cfg.TerraBaseDatabase, err)
	}
}

func TestTSAuthConfiguration(t *testing.T) {
	t.Setenv("TS_DASHBOARD_PRODUCTION_DEMO", "1")
	t.Setenv("FLY_APP_NAME", "")
	t.Setenv("DASHBOARD_DEMO_ADMIN_EMAIL", "")
	t.Setenv("DASHBOARD_DEMO_ADMIN_PASSWORD", "")
	t.Setenv("TS_DASHBOARD_CSRF_SECRET", "")
	t.Setenv("DASHBOARD_CSP_REPORT_URL", "")
	t.Setenv("DASHBOARD_SECURE_COOKIES", "")
	for _, name := range []string{"TERRABASE_URL", "TERRABASE_API_KEY", "TERRABASE_PROJECT_ID", "TERRABASE_DATABASE"} {
		t.Setenv(name, "")
	}
	const callback = "https://dashboard.example/login/callback"
	for _, tc := range []struct {
		name, dev, publicURL, issuer, clientID, secret, redirect string
		want                                                     TSAuth
		origin                                                   string
		invalid                                                  bool
	}{
		{name: "unset", origin: "https://gru.auth.terrascale.tech"},
		{name: "partial", issuer: "https://auth.example", clientID: "dashboard", invalid: true},
		{name: "secret only", secret: "s3cret", invalid: true},
		{name: "derived redirect", publicURL: "https://dashboard.example", issuer: "https://auth.example/", clientID: "dashboard", secret: "s3cret",
			want: TSAuth{Issuer: "https://auth.example/", ClientID: "dashboard", ClientSecret: "s3cret", RedirectURL: callback}, origin: "https://auth.example"},
		{name: "explicit redirect", publicURL: "https://dashboard.example", issuer: "https://auth.example", clientID: "dashboard", secret: "s3cret", redirect: callback,
			want: TSAuth{Issuer: "https://auth.example", ClientID: "dashboard", ClientSecret: "s3cret", RedirectURL: callback}, origin: "https://auth.example"},
		{name: "regional issuer", publicURL: "https://dashboard.example", issuer: "https://gru.auth.example/terrascale", clientID: "dashboard", secret: "s3cret",
			want: TSAuth{Issuer: "https://gru.auth.example/terrascale", ClientID: "dashboard", ClientSecret: "s3cret", RedirectURL: callback}, origin: "https://gru.auth.example"},
		{name: "insecure cookies", publicURL: "http://dashboard.example", issuer: "https://auth.example", clientID: "dashboard", secret: "s3cret", redirect: callback, invalid: true},
		{name: "no public URL", issuer: "https://auth.example", clientID: "dashboard", secret: "s3cret", redirect: callback, invalid: true},
		{name: "no redirect or public URL", issuer: "https://auth.example", clientID: "dashboard", secret: "s3cret", invalid: true},
		{name: "http issuer", issuer: "http://localhost:9000", clientID: "dashboard", secret: "s3cret", redirect: callback, invalid: true},
		{name: "http issuer in dev", dev: "1", publicURL: "http://localhost:5481", issuer: "http://localhost:9000", clientID: "dashboard", secret: "s3cret",
			want: TSAuth{Issuer: "http://localhost:9000", ClientID: "dashboard", ClientSecret: "s3cret", RedirectURL: "http://localhost:5481/login/callback"}, origin: "http://localhost:9000"},
		{name: "issuer with query", issuer: "https://auth.example?x=1", clientID: "dashboard", secret: "s3cret", redirect: callback, invalid: true},
		{name: "redirect path", issuer: "https://auth.example", clientID: "dashboard", secret: "s3cret", redirect: "https://dashboard.example/callback", invalid: true},
		{name: "redirect query", issuer: "https://auth.example", clientID: "dashboard", secret: "s3cret", redirect: callback + "?next=/", invalid: true},
		{name: "redirect fragment", issuer: "https://auth.example", clientID: "dashboard", secret: "s3cret", redirect: callback + "#top", invalid: true},
		{name: "issuer empty query", issuer: "https://auth.example?", clientID: "dashboard", secret: "s3cret", redirect: callback, invalid: true},
		{name: "redirect empty query", issuer: "https://auth.example", clientID: "dashboard", secret: "s3cret", redirect: callback + "?", invalid: true},
		{name: "redirect empty fragment", issuer: "https://auth.example", clientID: "dashboard", secret: "s3cret", redirect: callback + "#", invalid: true},
		{name: "redirect escaped path", issuer: "https://auth.example", clientID: "dashboard", secret: "s3cret", redirect: "https://dashboard.example/login/%63allback", invalid: true},
	} {
		t.Run(tc.name, func(t *testing.T) {
			t.Setenv("DASHBOARD_DEV", tc.dev)
			t.Setenv("DASHBOARD_PUBLIC_URL", tc.publicURL)
			t.Setenv("TS_AUTH_ISSUER", tc.issuer)
			t.Setenv("TS_AUTH_CLIENT_ID", tc.clientID)
			t.Setenv("TS_AUTH_CLIENT_SECRET", tc.secret)
			t.Setenv("TS_AUTH_REDIRECT_URL", tc.redirect)
			cfg, err := Load()
			if (err != nil) != tc.invalid || cfg.TSAuth != tc.want || cfg.TSAuthOrigin != tc.origin {
				t.Fatalf("ts-auth=%+v origin=%q error=%v", cfg.TSAuth, cfg.TSAuthOrigin, err)
			}
		})
	}
}

// TestFlyTerraBaseNeedsTSAuth covers production: on Fly.io with TerraBase,
// users' databases must live in TerraBase, which needs the user's ts-auth
// token, so the dashboard refuses to start without ts-auth.
func TestFlyTerraBaseNeedsTSAuth(t *testing.T) {
	for key, value := range map[string]string{
		"TERRABASE_URL": "https://gru.api.example", "TERRABASE_API_KEY": "tb_key", "TERRABASE_PROJECT_ID": "project",
		"TERRABASE_DATABASE": "dashboard", "TS_DASHBOARD_CSRF_SECRET": strings.Repeat("s", 32), "FLY_APP_NAME": "ts-dashboard",
		"DASHBOARD_PUBLIC_URL": "https://dash.example", "DASHBOARD_DEMO_ADMIN_EMAIL": "", "DASHBOARD_DEMO_ADMIN_PASSWORD": "",
		"TS_AUTH_ISSUER": "", "TS_AUTH_CLIENT_ID": "", "TS_AUTH_CLIENT_SECRET": "", "TS_AUTH_REDIRECT_URL": "",
	} {
		t.Setenv(key, value)
	}
	if _, err := Load(); err == nil || !strings.Contains(err.Error(), "TS_AUTH_ISSUER") {
		t.Fatalf("Fly with TerraBase and no ts-auth = %v, want refused", err)
	}
	t.Setenv("TS_AUTH_ISSUER", "https://gru.auth.example/terrascale")
	t.Setenv("TS_AUTH_CLIENT_ID", "ts-dashboard")
	t.Setenv("TS_AUTH_CLIENT_SECRET", "secret")
	if cfg, err := Load(); err != nil || cfg.Demo {
		t.Fatalf("Fly with TerraBase and ts-auth = %+v, %v; want live", cfg, err)
	}
}
