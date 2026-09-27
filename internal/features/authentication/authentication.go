// Package authentication owns the project-scoped ts-auth console: people,
// sign-in configuration, developer integrations, and hosted-page branding.
// Every change is stored on the project's core.AuthInstance.
package authentication

import (
	"errors"
	"net/http"
	"net/url"
	"strings"

	"github.com/a-h/templ"

	"terrascale.tech/dashboard/internal/core"
)

// issuerOrigin is ts-auth's single global public origin; every instance's
// issuer, hosted pages, and Management API live under it.
const issuerOrigin = "https://auth.terrascale.tech"

// Mount registers the authentication console pages, their mutations, and
// the matching MCP tools.
func Mount(mux *http.ServeMux, deps core.Deps) {
	service := Service{deps: deps}
	mountTools(deps.Tools, service)
	mux.HandleFunc("GET /auth", func(w http.ResponseWriter, r *http.Request) { renderPage(w, r, deps, "overview") })
	mux.HandleFunc("GET /auth/{section}", func(w http.ResponseWriter, r *http.Request) { renderPage(w, r, deps, r.PathValue("section")) })
	mux.HandleFunc("POST /api/v1/auth-management/instance", func(w http.ResponseWriter, r *http.Request) {
		p, ok := authorize(deps, w, r)
		if !ok {
			return
		}
		_, err := service.SaveBranding(p, r.Form.Get("name"), r.Form.Get("logo_url"), r.Form.Get("theme_color"))
		finish(w, r, "branding", err)
	})
	mux.HandleFunc("POST /api/v1/auth-management/settings", func(w http.ResponseWriter, r *http.Request) {
		p, ok := authorize(deps, w, r)
		if !ok {
			return
		}
		key := r.Form.Get("setting")
		_, group, exists := findStrategy(key)
		if !exists {
			http.NotFound(w, r)
			return
		}
		_, err := service.SetStrategy(p, key, r.Form.Get("enabled") == "true")
		finish(w, r, group.Page, err)
	})
	mux.HandleFunc("POST /api/v1/auth-management/{section}", func(w http.ResponseWriter, r *http.Request) {
		key := r.PathValue("section")
		if _, err := listSection(key); err != nil {
			http.NotFound(w, r)
			return
		}
		p, ok := authorize(deps, w, r)
		if !ok {
			return
		}
		_, err := service.SaveResource(p, key, strings.TrimSpace(r.Form.Get("id")), r.Form)
		finish(w, r, key, err)
	})
	mux.HandleFunc("POST /api/v1/auth-management/{section}/{resourceID}/delete", func(w http.ResponseWriter, r *http.Request) {
		key := r.PathValue("section")
		if _, err := listSection(key); err != nil {
			http.NotFound(w, r)
			return
		}
		p, ok := authorize(deps, w, r)
		if !ok {
			return
		}
		_, err := service.DeleteResource(p, key, r.PathValue("resourceID"))
		finish(w, r, key, err)
	})
	mux.HandleFunc("POST /api/v1/auth-management/{section}/{resourceID}/actions/{action}", func(w http.ResponseWriter, r *http.Request) {
		key := r.PathValue("section")
		if _, exists := findSection(key); !exists {
			http.NotFound(w, r)
			return
		}
		p, ok := authorize(deps, w, r)
		if !ok {
			return
		}
		_, err := service.RunAction(p, key, r.PathValue("resourceID"), r.PathValue("action"))
		finish(w, r, key, err)
	})
}

func renderPage(w http.ResponseWriter, r *http.Request, deps core.Deps, key string) {
	current, exists := findSection(key)
	if !exists {
		http.NotFound(w, r)
		return
	}
	p, account, ok := signedIn(deps, w, r)
	if !ok {
		return
	}
	org, exists := account.Organizations[p.OrganizationID]
	if !exists {
		http.Error(w, "selected organization is unavailable", http.StatusForbidden)
		return
	}
	project, exists := org.Projects[p.ProjectID]
	if !exists || project.OrganizationID != org.ID {
		// Without a project, Overview leads to the project setup.
		http.Redirect(w, r, "/", http.StatusSeeOther)
		return
	}
	query := r.URL.Query()
	data := buildPage(current, project, org.CanManage(p.UserID), strings.TrimSpace(query.Get("q")), query.Get("page"))
	data.Saved, data.Error = query.Get("saved") == "1", query.Get("error")
	title := "Authentication"
	if key != "overview" {
		title += " · " + current.Title
	}
	page := core.Page{
		Layout: "shell", Title: title, Principal: p,
		View: func(view core.Page) templ.Component { return authenticationView(data, view.CSRFToken) },
	}
	if err := deps.Render.Page(w, r, page); err != nil {
		http.Error(w, "could not render authentication console", http.StatusInternalServerError)
	}
}

// authorize verifies CSRF, the session, and manage rights, and parses the form.
func authorize(deps core.Deps, w http.ResponseWriter, r *http.Request) (core.Principal, bool) {
	if !deps.VerifyCSRF(w, r) {
		return core.Principal{}, false
	}
	p, account, ok := signedIn(deps, w, r)
	if !ok {
		return core.Principal{}, false
	}
	if !account.Organizations[p.OrganizationID].CanManage(p.UserID) {
		http.Error(w, "forbidden", http.StatusForbidden)
		return core.Principal{}, false
	}
	if err := r.ParseForm(); err != nil {
		http.Error(w, "invalid form", http.StatusBadRequest)
		return core.Principal{}, false
	}
	return p, true
}

func signedIn(deps core.Deps, w http.ResponseWriter, r *http.Request) (core.Principal, core.Account, bool) {
	if deps.Sessions == nil {
		http.Error(w, "authentication required", http.StatusUnauthorized)
		return core.Principal{}, core.Account{}, false
	}
	p, ok := deps.Sessions.Principal(r)
	if !ok {
		core.RedirectToLogin(w, r)
		return core.Principal{}, core.Account{}, false
	}
	account, err := deps.Store.SnapshotAs(p)
	if err != nil {
		writeError(w, err)
		return core.Principal{}, core.Account{}, false
	}
	return p, account, true
}

// finish redirects back to the section with a saved flag or the form error.
func finish(w http.ResponseWriter, r *http.Request, key string, err error) {
	var message formError
	switch {
	case errors.As(err, &message):
		redirect(w, r, key, url.Values{"error": {string(message)}})
	case err != nil:
		writeError(w, err)
	default:
		redirect(w, r, key, url.Values{"saved": {"1"}})
	}
}

func redirect(w http.ResponseWriter, r *http.Request, key string, query url.Values) {
	http.Redirect(w, r, "/auth/"+url.PathEscape(key)+"?"+query.Encode(), http.StatusSeeOther)
}

func writeError(w http.ResponseWriter, err error) {
	switch {
	case core.WriteAccessError(w, err):
	case errors.Is(err, core.ErrConflict):
		http.Error(w, "conflict", http.StatusConflict)
	case errors.Is(err, core.ErrInvalid):
		http.Error(w, err.Error(), http.StatusBadRequest)
	default:
		http.Error(w, "invalid request", http.StatusBadRequest)
	}
}
