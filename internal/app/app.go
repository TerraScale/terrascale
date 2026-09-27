package app

import (
	"errors"
	"net/http"
	"net/mail"
	"strings"

	"github.com/CAFxX/httpcompression"
	"terrascale.tech/dashboard/internal/adapters/tsgo"
	"terrascale.tech/dashboard/internal/core"
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
	"terrascale.tech/dashboard/internal/platform/render"
	"terrascale.tech/dashboard/internal/platform/session"
	"terrascale.tech/dashboard/internal/platform/statuspage"
)

type Application struct {
	Handler http.Handler
}

func New(cfg config.Config) (*Application, error) {
	if !cfg.Demo {
		return nil, errors.New("live dashboard state is not configured")
	}
	store := core.NewStore()
	if err := seedAdmin(store, cfg.AdminEmail, cfg.AdminPassword); err != nil {
		return nil, err
	}
	if err := seedDemoAccount(store); err != nil {
		return nil, err
	}
	sessions := session.New(store, session.Options{Secure: cfg.SecureCookies})
	guard := csrf.New(sessions, csrf.Options{Secure: cfg.SecureCookies})
	var notices render.StatusNotices
	if cfg.StatusURL != "" {
		client, err := statuspage.New(cfg.StatusURL)
		if err != nil {
			return nil, err
		}
		notices = client
	}
	renderer := render.New(store, guard, notices)
	deps := core.Deps{
		Store: store, Sessions: sessions, CSRF: guard, Render: renderer,
		Events: datastar.Events{}, Demo: cfg.Demo,
		SDK: tsgo.Factory{APIBaseURL: cfg.SDKBaseURL, AccessToken: cfg.SDKAccessToken},
	}
	mux := http.NewServeMux()
	if err := assets.Mount(mux, cfg.Dev); err != nil {
		return nil, err
	}
	status.Mount(mux, deps)
	auth.Mount(mux, deps)
	admin.Mount(mux, deps)
	authentication.Mount(mux, deps)
	onboarding.Mount(mux, deps)
	profile.Mount(mux, deps)
	settings.Mount(mux, deps)
	organization.Mount(mux, deps)
	access.Mount(mux, deps)
	home.Mount(mux, deps)
	databases.Mount(mux, deps)
	apikeys.Mount(mux, deps)
	data.Mount(mux, deps)
	backups.Mount(mux, deps)
	subscriptions.Mount(mux, deps)
	querysubscriptions.Mount(mux, deps)
	usage.Mount(mux, deps)
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
	return &Application{Handler: compress(invalidatePrefetch(dropForeignMessages(guard.Protect(mux))))}, nil
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

func seedAdmin(store *core.Store, email, password string) error {
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
	a, err := store.CreateAccount(core.User{Name: "Demo administrator", Email: email, PasswordHash: hash, PlatformAdmin: true})
	if err != nil {
		return err
	}
	return store.Update(a.ID, func(a *core.Account) error {
		a.OnboardingCompleted = true
		a.OnboardingStep = "complete"
		return nil
	})
}

const (
	demoAccountEmail    = "mario@terrascale.tech"
	demoAccountPassword = "terrascale"
)

func seedDemoAccount(store *core.Store) error {
	if _, exists := store.FindByEmail(demoAccountEmail); exists {
		return nil
	}
	hash, err := auth.HashPassword(demoAccountPassword)
	if err != nil {
		return err
	}
	account, err := store.CreateAccount(core.User{
		ID: "usr_01JC9Q7T4N8Y2M3P5R7V9X1ZAB", Name: "Mario Kraus",
		Email: demoAccountEmail, PasswordHash: hash, Role: "owner", PlatformAdmin: true,
	})
	if err != nil {
		return err
	}
	return store.Update(account.ID, func(current *core.Account) error {
		seedMarioDemoFixture(current)
		return nil
	})
}
