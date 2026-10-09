package config

import (
	"errors"
	"fmt"
	"net/http"
	"net/url"
	"os"
	"strings"

	"terrascale.tech/sdk/tsauth"
)

// defaultStatusURL is the public status page summary behind the shell's
// incident and maintenance banner.
const defaultStatusURL = "https://status.terrascale.tech/api/v2/summary.json"

// defaultAtlasURL is ts-atlas, whose discovery lists the regions a database
// can be placed in.
const defaultAtlasURL = "https://atlas.terrascale.tech"

// defaultTSAuthOrigin is production ts-auth's regional public origin.
const defaultTSAuthOrigin = "https://gru.auth.terrascale.tech"

type Config struct {
	PublicURL          string
	Addr               string
	Demo               bool // Disables Stripe; backend configuration selects the memory simulator.
	Dev                bool
	SecureCookies      bool
	AdminEmail         string
	AdminPassword      string
	TerraBaseURL       string
	TerraBaseAPIKey    string
	TerraBaseProjectID string
	TerraBaseDatabase  string
	// TerraBaseHTTPClient supplies the trusted local release certificate in tests.
	TerraBaseHTTPClient *http.Client
	// AtlasURL is the ts-atlas origin the database region picker reads;
	// empty leaves region discovery unconfigured.
	AtlasURL string
	// StatusURL is the Statuspage-compatible summary.json the shell banner
	// reads; empty disables the banner.
	StatusURL string
	// ClientIPHeader names the header in which the reverse proxy in front of
	// the dashboard reports the client address; empty means the connection's
	// remote address is the client's.
	ClientIPHeader string
	// SentryDSN is where errors and panics are reported (GlitchTip speaks the
	// Sentry protocol); empty disables error tracking.
	SentryDSN string
	// SentryEnvironment tags every reported event.
	SentryEnvironment string
	// CSPReportURL receives Content-Security-Policy violation reports; empty
	// sends none.
	CSPReportURL string
	// CSRFSecret keys CSRF tokens, from TS_DASHBOARD_CSRF_SECRET. Every
	// machine serving the dashboard must share it, so TerraBase persistence
	// requires it; the memory demo draws a random key per process.
	CSRFSecret []byte
	// TSAuth signs users in through ts-auth when its Issuer is set; unset,
	// the dashboard keeps its own password sign-in.
	TSAuth TSAuth
	// TSAuthOrigin is ts-auth's public origin, under which every project's
	// auth instance lives: TS_AUTH_ISSUER's origin, else production's.
	TSAuthOrigin string
}

// TSAuth is the dashboard's OpenID Connect client registration at ts-auth.
type TSAuth struct {
	// Issuer is TS_AUTH_ISSUER, exactly as ts-auth's discovery document
	// names it.
	Issuer       string
	ClientID     string
	ClientSecret string
	// RedirectURL is TS_AUTH_REDIRECT_URL, by default the public origin's
	// /login/callback.
	RedirectURL string
}

// minCSRFSecret is the shortest TS_DASHBOARD_CSRF_SECRET accepted, the size
// of the random key the demo draws.
const minCSRFSecret = 32

func Load() (Config, error) {
	c := Config{
		PublicURL:          strings.TrimRight(strings.TrimSpace(os.Getenv("DASHBOARD_PUBLIC_URL")), "/"),
		Addr:               strings.TrimSpace(os.Getenv("DASHBOARD_ADDR")),
		Demo:               os.Getenv("TS_DASHBOARD_PRODUCTION_DEMO") == "1",
		Dev:                os.Getenv("DASHBOARD_DEV") == "1",
		AdminEmail:         strings.TrimSpace(os.Getenv("DASHBOARD_DEMO_ADMIN_EMAIL")),
		AdminPassword:      os.Getenv("DASHBOARD_DEMO_ADMIN_PASSWORD"),
		TerraBaseURL:       strings.TrimSpace(os.Getenv("TERRABASE_URL")),
		TerraBaseAPIKey:    strings.TrimSpace(os.Getenv("TERRABASE_API_KEY")),
		TerraBaseProjectID: strings.TrimSpace(os.Getenv("TERRABASE_PROJECT_ID")),
		TerraBaseDatabase:  strings.TrimSpace(os.Getenv("TERRABASE_DATABASE")),
		SentryDSN:          strings.TrimSpace(os.Getenv("SENTRY_DSN")),
		SentryEnvironment:  strings.TrimSpace(os.Getenv("SENTRY_ENVIRONMENT")),
		CSPReportURL:       strings.TrimSpace(os.Getenv("DASHBOARD_CSP_REPORT_URL")),
	}
	if err := c.ValidateBackend(); err != nil {
		return Config{}, err
	}
	// Backend configuration must not override explicit provider isolation.
	c.Demo = c.Demo || c.TerraBaseURL == ""
	if c.Addr == "" {
		c.Addr = ":8080"
	}
	if c.PublicURL == "" && os.Getenv("FLY_APP_NAME") != "" {
		c.PublicURL = "https://" + os.Getenv("FLY_APP_NAME") + ".fly.dev"
	}
	if c.PublicURL != "" {
		u, err := url.Parse(c.PublicURL)
		if err != nil || (u.Scheme != "http" && u.Scheme != "https") || u.Hostname() == "" || u.User != nil || u.Path != "" || strings.ContainsAny(c.PublicURL, "?#") {
			return Config{}, errors.New("DASHBOARD_PUBLIC_URL must be an HTTP(S) origin")
		}
	}
	c.AtlasURL = defaultAtlasURL
	if atlasURL, set := os.LookupEnv("TS_ATLAS_URL"); set {
		c.AtlasURL = strings.TrimSpace(atlasURL)
	}
	// Set DASHBOARD_STATUS_SUMMARY_URL to another summary, or to an empty
	// value to disable the status banner.
	c.StatusURL = defaultStatusURL
	if statusURL, set := os.LookupEnv("DASHBOARD_STATUS_SUMMARY_URL"); set {
		c.StatusURL = strings.TrimSpace(statusURL)
	}
	if c.SentryEnvironment == "" {
		c.SentryEnvironment = "development"
		if os.Getenv("FLY_APP_NAME") != "" {
			c.SentryEnvironment = "production"
		}
	}
	if c.CSPReportURL != "" {
		// The URL is spliced into the Content-Security-Policy header, where
		// whitespace, ";" or "," would start another directive or policy.
		u, err := url.Parse(c.CSPReportURL)
		if err != nil || (u.Scheme != "http" && u.Scheme != "https") || u.Hostname() == "" || u.User != nil || strings.ContainsAny(c.CSPReportURL, " \t\r\n;,'\"") {
			return Config{}, errors.New("DASHBOARD_CSP_REPORT_URL must be an HTTP(S) URL")
		}
	}
	if secret := os.Getenv("TS_DASHBOARD_CSRF_SECRET"); secret != "" {
		if len(secret) < minCSRFSecret {
			return Config{}, fmt.Errorf("TS_DASHBOARD_CSRF_SECRET must be at least %d bytes", minCSRFSecret)
		}
		c.CSRFSecret = []byte(secret)
	} else if c.TerraBaseURL != "" {
		return Config{}, errors.New("TS_DASHBOARD_CSRF_SECRET is required with TerraBase persistence")
	}
	c.SecureCookies = os.Getenv("DASHBOARD_SECURE_COOKIES") == "1" ||
		(os.Getenv("DASHBOARD_SECURE_COOKIES") != "0" && !c.Dev &&
			(strings.HasPrefix(c.PublicURL, "https://") || os.Getenv("FLY_APP_NAME") != ""))
	// On Fly.io every request arrives through Fly Proxy, which reports the
	// client address in Fly-Client-IP.
	if os.Getenv("FLY_APP_NAME") != "" {
		c.ClientIPHeader = "Fly-Client-IP"
	}
	if err := c.loadTSAuth(); err != nil {
		return Config{}, err
	}
	// Users' databases live in TerraBase only when the dashboard can act
	// as the user there, with their ts-auth access token.
	if c.TerraBaseURL != "" && c.TSAuth.Issuer == "" && os.Getenv("FLY_APP_NAME") != "" {
		return Config{}, errors.New("TS_AUTH_ISSUER is required with TerraBase on Fly.io: without it users' databases would not live in TerraBase")
	}
	if (c.AdminEmail == "") != (c.AdminPassword == "") {
		return Config{}, errors.New("demo admin email and password must both be set")
	}
	return c, nil
}

// ValidateBackend rejects partial configuration rather than silently losing
// state in memory. The SDK validates the origin and key before app startup.
func (c Config) ValidateBackend() error {
	values := []string{c.TerraBaseURL, c.TerraBaseAPIKey, c.TerraBaseProjectID, c.TerraBaseDatabase}
	configured := 0
	for _, value := range values {
		if value != "" {
			configured++
		}
	}
	if configured != 0 && configured != len(values) {
		return errors.New("TERRABASE_URL, TERRABASE_API_KEY, TERRABASE_PROJECT_ID and TERRABASE_DATABASE must all be set")
	}
	return nil
}

// loadTSAuth reads the ts-auth client registration. ts-auth is a
// confidential client's issuer, so the secret is required with the rest.
func (c *Config) loadTSAuth() error {
	c.TSAuth = TSAuth{
		Issuer:       strings.TrimSpace(os.Getenv("TS_AUTH_ISSUER")),
		ClientID:     strings.TrimSpace(os.Getenv("TS_AUTH_CLIENT_ID")),
		ClientSecret: os.Getenv("TS_AUTH_CLIENT_SECRET"),
		RedirectURL:  strings.TrimSpace(os.Getenv("TS_AUTH_REDIRECT_URL")),
	}
	c.TSAuthOrigin = defaultTSAuthOrigin
	if c.TSAuth == (TSAuth{}) {
		return nil
	}
	if c.TSAuth.Issuer == "" || c.TSAuth.ClientID == "" || c.TSAuth.ClientSecret == "" {
		return errors.New("TS_AUTH_ISSUER, TS_AUTH_CLIENT_ID and TS_AUTH_CLIENT_SECRET must be set together")
	}
	// The sign-in flow and session cookies carry credentials.
	if !c.Dev && !c.SecureCookies {
		return errors.New("signing in through ts-auth needs secure cookies: serve the dashboard over HTTPS (DASHBOARD_PUBLIC_URL) or set DASHBOARD_SECURE_COOKIES=1")
	}
	u, err := url.Parse(c.TSAuth.Issuer)
	if err != nil || !c.allowedScheme(u.Scheme) || u.Hostname() == "" || u.User != nil || strings.ContainsAny(c.TSAuth.Issuer, "?#") {
		return errors.New("TS_AUTH_ISSUER must be an HTTPS URL")
	}
	// Validate the SDK contract before app.New constructs its auth client.
	// This does not contact the issuer.
	if _, err := tsauth.NewHeadless(tsauth.HeadlessConfig{
		Issuer: c.TSAuth.Issuer, ClientID: c.TSAuth.ClientID, ClientSecret: c.TSAuth.ClientSecret,
	}); err != nil {
		return fmt.Errorf("TS_AUTH_ISSUER: %w; development HTTP requires localhost or a loopback IP; use HTTPS for other hosts", err)
	}
	c.TSAuthOrigin = u.Scheme + "://" + u.Host
	if c.TSAuth.RedirectURL == "" && c.PublicURL != "" {
		c.TSAuth.RedirectURL = c.PublicURL + "/login/callback"
	}
	if u, err := url.Parse(c.TSAuth.RedirectURL); err != nil || !c.allowedScheme(u.Scheme) || u.Hostname() == "" || u.User != nil || u.EscapedPath() != "/login/callback" || strings.ContainsAny(c.TSAuth.RedirectURL, "?#") {
		return errors.New("TS_AUTH_REDIRECT_URL, or DASHBOARD_PUBLIC_URL to derive it, must be the dashboard's HTTPS /login/callback URL")
	}
	return nil
}

// allowedScheme reports whether a ts-auth URL may use scheme: HTTPS, or HTTP
// as well in local development.
func (c *Config) allowedScheme(scheme string) bool {
	return scheme == "https" || (c.Dev && scheme == "http")
}
