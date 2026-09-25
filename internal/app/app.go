package app

import (
	"errors"
	"net/http"
	"net/mail"
	"strings"

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
	"terrascale.tech/dashboard/internal/features/engine"
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
)

type Application struct {
	Handler http.Handler
	Store   *core.Store
	Deps    core.Deps
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
	renderer := render.New(store, guard)
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
	engine.Mount(mux, deps)
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
	return &Application{Handler: guard.Protect(mux), Store: store, Deps: deps}, nil
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
