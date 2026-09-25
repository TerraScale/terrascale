package render

import (
	"context"
	"errors"
	"io"
	"net/http"
	"net/http/httptest"
	"strings"
	"testing"

	"github.com/a-h/templ"
	"terrascale.tech/dashboard/internal/core"
)

type fragmentCSRF struct{}

func (fragmentCSRF) Token(http.ResponseWriter, *http.Request) string { return "token&<" }
func (fragmentCSRF) Verify(*http.Request) error                      { return nil }
func (fragmentCSRF) Protect(handler http.Handler) http.Handler       { return handler }

func TestPageRendersBrandSuffixOnce(t *testing.T) {
	renderer := New(nil, nil)
	for _, example := range []struct{ input, want string }{
		{"Databases · TerraScale", "Databases · TerraScale"},
		{"Subscriptions | TerraScale", "Subscriptions · TerraScale"},
		{"TerraScale", "TerraScale"},
		{"Activate your TerraScale account", "Activate your TerraScale account · TerraScale"},
	} {
		response := httptest.NewRecorder()
		request := httptest.NewRequest(http.MethodGet, "/title", nil)
		if err := renderer.Page(response, request, core.Page{Layout: "auth", Title: example.input, View: func(core.Page) templ.Component { return templ.NopComponent }}); err != nil {
			t.Fatal(err)
		}
		if !strings.Contains(response.Body.String(), "<title>"+example.want+"</title>") {
			t.Fatalf("title %q rendered unexpected document title", example.input)
		}
	}
}

func TestTemplPageViewReceivesFinalRenderData(t *testing.T) {
	store := core.NewStore()
	account, err := store.CreateAccount(core.User{Name: "Avery Example", Email: "avery@example.test", PasswordHash: "hash"})
	if err != nil {
		t.Fatal(err)
	}
	renderer := New(store, fragmentCSRF{})
	var received core.Page
	response := httptest.NewRecorder()
	request := httptest.NewRequest(http.MethodGet, "/?tab=overview", nil)
	err = renderer.Page(response, request, core.Page{
		Layout: "shell", Title: "Overview · TerraScale", Principal: account.Principal(),
		Shell: core.Shell{UserName: "forged"},
		View: func(page core.Page) templ.Component {
			received = page
			return templ.ComponentFunc(func(ctx context.Context, w io.Writer) error {
				if templ.GetNonce(ctx) != page.Nonce {
					return errors.New("templ context nonce does not match page nonce")
				}
				_, err := io.WriteString(w, `<main id="feature-view">view rendered</main>`)
				return err
			})
		},
	})
	if err != nil {
		t.Fatal(err)
	}
	if received.CSRFToken != "token&<" || received.Nonce == "" || received.Title != "Overview" {
		t.Fatalf("view received incomplete render data: token=%q nonce=%q title=%q", received.CSRFToken, received.Nonce, received.Title)
	}
	if received.Shell.UserName != account.User.Name || received.Shell.ReturnURL != "/?tab=overview" {
		t.Fatalf("view received noncanonical shell: %+v", received.Shell)
	}
	body := response.Body.String()
	if !strings.Contains(body, `<main id="feature-view">view rendered</main>`) || !strings.Contains(body, `<title>Overview · TerraScale</title>`) {
		t.Fatalf("templ view or wrapper missing: %s", body)
	}
	if !strings.Contains(body, `data-nonce="`+received.Nonce+`"`) || !strings.Contains(response.Header().Get("Content-Security-Policy"), `'nonce-`+received.Nonce+`'`) {
		t.Fatal("HTML nonce and CSP nonce differ")
	}
	if strings.Contains(body, "forged") || response.Header().Get("Cache-Control") != "private, no-store" {
		t.Fatal("noncanonical shell or cache policy")
	}
}

func TestTemplFragmentUsesCSRFWithoutPageNonce(t *testing.T) {
	renderer := New(nil, fragmentCSRF{})
	response := httptest.NewRecorder()
	request := httptest.NewRequest(http.MethodGet, "/access", nil)
	err := renderer.Fragment(response, request, core.Fragment{
		View: func(page core.Page) templ.Component {
			return templ.ComponentFunc(func(ctx context.Context, w io.Writer) error {
				if templ.GetNonce(ctx) != "" || page.Nonce != "" {
					return errors.New("fragment minted a CSP nonce")
				}
				_, err := io.WriteString(w, `<form><input name="_csrf" value="`+templ.EscapeString(page.CSRFToken)+`"></form>`)
				return err
			})
		},
	})
	if err != nil {
		t.Fatal(err)
	}
	if got := response.Body.String(); got != `<form><input name="_csrf" value="token&amp;&lt;"></form>` {
		t.Fatalf("fragment = %q", got)
	}
	if response.Header().Get("Content-Security-Policy") != "" {
		t.Fatal("fragment unexpectedly set CSP")
	}
}

func TestTemplRenderErrorDoesNotWriteResponse(t *testing.T) {
	renderer := New(nil, nil)
	failure := errors.New("render failed")
	view := func(core.Page) templ.Component {
		return templ.ComponentFunc(func(_ context.Context, w io.Writer) error {
			_, _ = io.WriteString(w, "partial")
			return failure
		})
	}
	for _, render := range []func(http.ResponseWriter, *http.Request) error{
		func(w http.ResponseWriter, r *http.Request) error {
			return renderer.Page(w, r, core.Page{Layout: "auth", View: view})
		},
		func(w http.ResponseWriter, r *http.Request) error {
			return renderer.Fragment(w, r, core.Fragment{View: view})
		},
	} {
		response := httptest.NewRecorder()
		err := render(response, httptest.NewRequest(http.MethodGet, "/", nil))
		if !errors.Is(err, failure) || response.Body.Len() != 0 || response.Header().Get("Content-Type") != "" {
			t.Fatalf("render error=%v, response body=%q, headers=%v", err, response.Body.String(), response.Header())
		}
	}
}

func TestMissingViewDoesNotWriteResponse(t *testing.T) {
	renderer := New(nil, nil)
	for _, render := range []func(http.ResponseWriter, *http.Request) error{
		func(w http.ResponseWriter, r *http.Request) error {
			return renderer.Page(w, r, core.Page{Layout: "auth"})
		},
		func(w http.ResponseWriter, r *http.Request) error {
			return renderer.Fragment(w, r, core.Fragment{})
		},
	} {
		response := httptest.NewRecorder()
		err := render(response, httptest.NewRequest(http.MethodGet, "/", nil))
		if !errors.Is(err, core.ErrInvalid) || response.Body.Len() != 0 || len(response.Header()) != 0 {
			t.Fatalf("missing view error=%v, response body=%q, headers=%v", err, response.Body.String(), response.Header())
		}
	}
}

func TestShellNavigationMatchesDashboardRoutes(t *testing.T) {
	store := core.NewStore()
	account, err := store.CreateAccount(core.User{Name: "Avery Example", Email: "avery@example.test", PasswordHash: "hash"})
	if err != nil {
		t.Fatal(err)
	}
	shell := shellFor(account, account.Principal(), "shell", httptest.NewRequest(http.MethodGet, "/billing/portal", nil))
	for i, want := range []core.NavItem{
		{Label: "Overview", URL: "/"}, {Label: "Databases", URL: "/databases"},
		{Label: "Tables", URL: "/data"}, {Label: "API keys", URL: "/api-keys"},
		{Label: "Team", URL: "/access"}, {Label: "Authentication", URL: "/auth"},
		{Label: "Usage", URL: "/usage"}, {Label: "Audit", URL: "/audit"},
		{Label: "Organization", URL: "/organization", BadgeCount: 1},
	} {
		if len(shell.Navigation) <= i || shell.Navigation[i] != want {
			t.Fatalf("primary navigation[%d] = %+v, want %+v", i, shell.Navigation, want)
		}
	}
	if len(shell.Navigation) != 9 {
		t.Fatalf("primary navigation count = %d, want 9", len(shell.Navigation))
	}
	for i, want := range []core.NavItem{
		{Label: "Backups", URL: "/backups"}, {Label: "Engine", URL: "/engine"},
		{Label: "Subscriptions", URL: "/subscriptions"},
		{Label: "Billing", URL: "/billing", Active: true}, {Label: "Settings", URL: "/settings"},
	} {
		if len(shell.SecondaryNavigation) <= i || shell.SecondaryNavigation[i] != want {
			t.Fatalf("secondary navigation[%d] = %+v, want %+v", i, shell.SecondaryNavigation, want)
		}
	}
	if len(shell.SecondaryNavigation) != 5 {
		t.Fatalf("secondary navigation count = %d, want 5", len(shell.SecondaryNavigation))
	}
}
