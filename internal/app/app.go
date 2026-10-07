package app

import (
	"context"
	"errors"
	"net/http"
	"net/mail"
	"os"
	"strings"
	"sync"
	"time"

	"github.com/CAFxX/httpcompression"
	"terrascale.tech/dashboard/internal/adapters/terrabase"
	"terrascale.tech/dashboard/internal/adapters/tsgo"
	"terrascale.tech/dashboard/internal/core"
	"terrascale.tech/dashboard/internal/core/memstore"
	"terrascale.tech/dashboard/internal/features/access"
	"terrascale.tech/dashboard/internal/features/admin"
	"terrascale.tech/dashboard/internal/features/apikeys"
	"terrascale.tech/dashboard/internal/features/audit"
	"terrascale.tech/dashboard/internal/features/auth"
	"terrascale.tech/dashboard/internal/features/authentication"
	"terrascale.tech/dashboard/internal/features/backups"
	"terrascale.tech/dashboard/internal/features/data"
	"terrascale.tech/dashboard/internal/features/databases"
	"terrascale.tech/dashboard/internal/features/home"
	"terrascale.tech/dashboard/internal/features/notifications"
	"terrascale.tech/dashboard/internal/features/onboarding"
	"terrascale.tech/dashboard/internal/features/organization"
	"terrascale.tech/dashboard/internal/features/payments"
	"terrascale.tech/dashboard/internal/features/profile"
	"terrascale.tech/dashboard/internal/features/querysubscriptions"
	"terrascale.tech/dashboard/internal/features/settings"
	"terrascale.tech/dashboard/internal/features/status"
	"terrascale.tech/dashboard/internal/features/subscriptions"
	"terrascale.tech/dashboard/internal/features/usage"
	"terrascale.tech/dashboard/internal/platform/assets"
	"terrascale.tech/dashboard/internal/platform/config"
	"terrascale.tech/dashboard/internal/platform/csrf"
	"terrascale.tech/dashboard/internal/platform/datastar"
	"terrascale.tech/dashboard/internal/platform/errortracking"
	"terrascale.tech/dashboard/internal/platform/mcpserver"
	"terrascale.tech/dashboard/internal/platform/platformapi"
	"terrascale.tech/dashboard/internal/platform/render"
	"terrascale.tech/dashboard/internal/platform/session"
	"terrascale.tech/dashboard/internal/platform/statuspage"
	"terrascale.tech/dashboard/internal/platform/throttle"
	"terrascale.tech/sdk/v1"
)

type Application struct {
	Handler http.Handler
}

func New(cfg config.Config) (*Application, error) {
	if err := cfg.ValidateBackend(); err != nil {
		return nil, err
	}
	ctx := context.Background()
	var store core.Store
	var sessionStore session.Store
	var throttleStore throttle.Store
	grants := auth.DeviceGrants(auth.NewMemoryDeviceGrants())
	probe := status.Options{Backend: "memstore"}
	var db *v1.Database
	cfg.Demo = cfg.TerraBaseURL == ""
	if cfg.Demo {
		store = memstore.New()
		if err := seedAdmin(ctx, store, cfg.AdminEmail, cfg.AdminPassword); err != nil {
			return nil, err
		}
		if err := seedDemoAccount(ctx, store); err != nil {
			return nil, err
		}
	} else {
		if len(cfg.CSRFSecret) < 32 {
			return nil, errors.New("TS_DASHBOARD_CSRF_SECRET must be at least 32 bytes with TerraBase persistence")
		}
		client, err := v1.New(v1.Config{BaseURL: cfg.TerraBaseURL, Token: cfg.TerraBaseAPIKey, HTTPClient: cfg.TerraBaseHTTPClient})
		if err != nil {
			return nil, err
		}
		db = client.Database(cfg.TerraBaseProjectID, cfg.TerraBaseDatabase)
		store = terrabase.New(db)
		sessionStore = terrabase.NewSessions(db)
		throttleStore = terrabase.NewThrottle(db)
		grants = terrabase.NewDeviceGrants(db)
		probe = status.Options{Backend: "terrabase", Check: func(ctx context.Context) error {
			_, err := db.Collection("users").List(ctx, v1.ListOptions{Limit: 1})
			return err
		}}
	}
	sdkFactory := tsgo.Factory{APIBaseURL: cfg.SDKBaseURL, AccessToken: cfg.SDKAccessToken}
	billingLookup := func(name string) string {
		switch name {
		case "TS_BILLING_TENANT_ID":
			return "terrascale-internal"
		case "TS_BILLING_PROJECT_ID":
			return cfg.TerraBaseProjectID
		case "TS_BILLING_DATABASE_ID":
			return cfg.TerraBaseDatabase
		default:
			return os.Getenv(name)
		}
	}
	billing, err := payments.New(ctx, cfg.Demo, billingLookup, db)
	if err != nil {
		return nil, err
	}
	// Platform admins see every service's TerraBase database: each sign-in
	// mirrors the TerraBase organizations they belong to, and pages read
	// the databases through Management with their ts-auth access token.
	var management *v1.Management
	if cfg.TSAuth.Issuer != "" && db != nil {
		// A stalled TerraBase must not hold a page: every Management call
		// gets a deadline.
		client := cfg.TerraBaseHTTPClient
		if client == nil {
			client = &http.Client{Timeout: 15 * time.Second}
		}
		if management, err = v1.NewManagement(v1.Config{BaseURL: cfg.TerraBaseURL, HTTPClient: client}); err != nil {
			return nil, err
		}
	}
	// With TS_AUTH_ISSUER set, ts-auth holds every credential.
	var tsAuth *auth.TSAuth
	sessionOptions := session.Options{Secure: cfg.SecureCookies, Store: sessionStore}
	if cfg.TSAuth.Issuer != "" {
		tsAuth, err = auth.NewTSAuth(auth.TSAuthConfig{
			Issuer: cfg.TSAuth.Issuer, ClientID: cfg.TSAuth.ClientID, ClientSecret: cfg.TSAuth.ClientSecret,
			RedirectURL: cfg.TSAuth.RedirectURL, Secure: cfg.SecureCookies,
			AdminSignIn: func(ctx context.Context, account core.Account, accessToken string) error {
				if management == nil {
					return nil
				}
				return terrabase.MirrorOrganizations(ctx, store, management.WithToken(accessToken), account.User.Email)
			},
		})
		if err != nil {
			return nil, err
		}
		sessionOptions.Refresh, sessionOptions.Revoke = tsAuth.RefreshSession, tsAuth.RevokeSession
	}
	sessions := session.New(store, sessionOptions)
	guard := csrf.New(sessions, csrf.Options{Secure: cfg.SecureCookies, Secret: cfg.CSRFSecret})
	var notices render.StatusNotices
	if cfg.StatusURL != "" {
		client, err := statuspage.New(cfg.StatusURL)
		if err != nil {
			return nil, err
		}
		notices = client
	}
	renderer := render.New(store, guard, notices, sessions.NoticeScope, cfg.Dev).ReportCSPViolationsTo(cfg.CSPReportURL)
	deps := core.Deps{
		Store: store, Sessions: sessions, CSRF: guard, Render: renderer,
		Events: datastar.Events{}, Demo: cfg.Demo, Tools: core.NewTools(store),
		SDK: sdkFactory, Management: management, DocumentsOnly: db != nil,
		Throttle: throttle.New(throttle.Options{ClientHeader: cfg.ClientIPHeader, Store: throttleStore}),
	}
	mux := http.NewServeMux()
	if err := assets.Mount(mux, cfg.Dev); err != nil {
		return nil, err
	}
	status.Mount(mux, deps, probe)
	auth.Mount(mux, deps, tsAuth)
	deviceAuthorization := auth.NewDeviceAuthorization(deps, grants, cfg.SecureCookies, cfg.PublicURL)
	deviceAuthorization.MountBrowser(mux)
	admin.Mount(mux, deps)
	emails := authentication.NewEmailBackend(cfg.TSAuth.Issuer, cfg.TSAuth.ClientID, cfg.TSAuth.ClientSecret, cfg.Demo)
	authentication.Mount(mux, deps, cfg.TSAuthOrigin, emails)
	onboarding.Mount(mux, deps, billing, tsAuth)
	profile.Mount(mux, deps)
	settings.Mount(mux, deps)
	organization.Mount(mux, deps)
	access.Mount(mux, deps, billing)
	home.Mount(mux, deps)
	databases.Mount(mux, deps)
	apikeys.Mount(mux, deps)
	data.Mount(mux, deps)
	backups.Mount(mux, deps)
	subscriptions.Mount(mux, deps, billing)
	querysubscriptions.Mount(mux, deps)
	usage.Mount(mux, deps, billing)
	notifications.Mount(mux, deps)
	audit.Mount(mux, deps)
	mux.HandleFunc("GET /healthz", func(w http.ResponseWriter, r *http.Request) {
		http.Redirect(w, r, "/health/ready", http.StatusTemporaryRedirect)
	})
	compress, err := httpcompression.DefaultAdapter(httpcompression.ContentTypes([]string{
		// Datastar streams must flush every event, and these are already compressed.
		"text/event-stream", "font/woff2", "image/png", "image/webp",
	}, true))
	if err != nil {
		return nil, err
	}
	// The MCP server and the platform API authenticate every request with a
	// bearer token instead of cookies, so they sit outside the CSRF guard.
	mcp := mcpserver.New(store, deps.Tools)
	platform := platformapi.New(deps.Tools.Routes(), func(ctx context.Context, token string) (platformapi.Grant, error) {
		if tsAuth == nil {
			return platformapi.Grant{}, core.ErrUnauthorized
		}
		return tsAuth.ResourceToken(ctx, store, platformapi.Audience, token)
	})
	root := http.NewServeMux()
	root.Handle(core.MCPPath, mcp)
	root.Handle(mcpserver.Host+"/", mcp)
	root.Handle(platformapi.Host+"/", platform)
	root.Handle("/api/v1/dashboard/cli/", deviceAuthorization.PublicHandler())
	root.Handle("/", withUserToken(sessions, invalidatePrefetch(dropForeignMessages(guard.Protect(mux)))))
	var handler http.Handler = securityHeaders(cfg.SecureCookies, root)
	if cfg.SentryDSN != "" {
		handler = errortracking.Middleware(handler)
	}
	return &Application{Handler: compress(handler)}, nil
}

// withUserToken lets features read TerraBase as the signed-in user with the
// ts-auth access token of the request's session.
func withUserToken(sessions *session.Manager, next http.Handler) http.Handler {
	return http.HandlerFunc(func(w http.ResponseWriter, r *http.Request) {
		// Read the session once per request, and only when a feature asks.
		token := sync.OnceValues(func() (string, bool) { return auth.AccessToken(sessions.RefreshState(r)) })
		next.ServeHTTP(w, r.WithContext(core.WithUserToken(r.Context(), token)))
	})
}

// securityHeaders sets the headers every response shares; rendered pages add
// their own nonce-based Content-Security-Policy. HSTS is sent only when the
// dashboard is served over HTTPS, which secure cookies already require.
func securityHeaders(https bool, next http.Handler) http.Handler {
	return http.HandlerFunc(func(w http.ResponseWriter, r *http.Request) {
		w.Header().Set("X-Content-Type-Options", "nosniff")
		if https {
			w.Header().Set("Strict-Transport-Security", "max-age=63072000")
		}
		next.ServeHTTP(w, r)
	})
}

// invalidatePrefetch drops pages the browser prefetched through the layout's
// speculation rules whenever a request may have changed state, so a later
// navigation never shows data from before the change.
func invalidatePrefetch(next http.Handler) http.Handler {
	return http.HandlerFunc(func(w http.ResponseWriter, r *http.Request) {
		if r.Method != http.MethodGet && r.Method != http.MethodHead {
			w.Header().Set("Clear-Site-Data", `"prefetchCache"`)
		}
		next.ServeHTTP(w, r)
	})
}

func seedAdmin(ctx context.Context, store core.Store, email, password string) error {
	if email == "" && password == "" {
		return nil
	}
	parsed, err := mail.ParseAddress(email)
	if err != nil || parsed.Address != email || len(password) < 12 || strings.ContainsAny(password, "\r\n") {
		return errors.New("invalid demo admin credentials configuration")
	}
	hash, err := auth.HashPassword(password)
	if err != nil {
		return err
	}
	a, err := store.CreateAccount(ctx, core.User{Name: "Demo administrator", Email: email, PasswordHash: hash, PlatformAdmin: true})
	if err != nil {
		return err
	}
	return store.Update(ctx, a.ID, func(a *core.Account) error {
		a.OnboardingCompleted = true
		a.OnboardingStep = "complete"
		return nil
	})
}

const (
	demoAccountEmail    = "mario@terrascale.tech"
	demoAccountPassword = "terrascale"
)

func seedDemoAccount(ctx context.Context, store core.Store) error {
	if _, err := store.FindByEmail(ctx, demoAccountEmail); err == nil {
		return nil
	} else if !errors.Is(err, core.ErrNotFound) {
		return err
	}
	hash, err := auth.HashPassword(demoAccountPassword)
	if err != nil {
		return err
	}
	account, err := store.CreateAccount(ctx, core.User{
		ID: "usr_01JC9Q7T4N8Y2M3P5R7V9X1ZAB", Name: "Mario Kraus",
		Email: demoAccountEmail, PasswordHash: hash, Role: "owner", PlatformAdmin: true,
	})
	if err != nil {
		return err
	}
	return store.Update(ctx, account.ID, func(current *core.Account) error {
		seedMarioDemoFixture(current)
		return nil
	})
}
