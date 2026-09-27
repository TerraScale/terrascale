// Package querysubscriptions owns project-scoped demo shared-result configurations.
package querysubscriptions

import (
	"errors"
	"maps"
	"net/http"
	"slices"
	"strings"

	"github.com/a-h/templ"
	"terrascale.tech/dashboard/internal/core"
)

type databaseChoice struct{ ID, Name string }

type pageData struct {
	ProjectID string
	CanWrite  bool
	Databases []databaseChoice
	Configs   []core.SharedResultConfig
	// Error explains why a pause, resume, or detach was refused; FormError
	// why a new configuration was, with Form refilled from the post.
	Error     string
	FormError string
	Form      configForm
}

// configForm is what the configuration form submits.
type configForm struct {
	DatabaseID, Name, Query, Transport string
}

// Mount registers the demo configuration page, routes, and tools. It never
// starts a live stream.
func Mount(mux *http.ServeMux, deps core.Deps) {
	service := Service{deps: deps}
	mountTools(deps.Tools, service)
	mux.HandleFunc("GET /subscriptions", func(w http.ResponseWriter, r *http.Request) {
		render(w, r, deps, http.StatusOK, nil)
	})
	mux.HandleFunc("POST /api/v1/dashboard/subscriptions/attach", func(w http.ResponseWriter, r *http.Request) {
		p, ok := formPrincipal(w, r, deps)
		if !ok {
			return
		}
		form := configForm{
			DatabaseID: r.FormValue("databaseId"),
			Name:       strings.TrimSpace(r.FormValue("name")),
			Query:      strings.TrimSpace(r.FormValue("query")),
			Transport:  r.FormValue("transport"),
		}
		_, err := service.Attach(p, attachInput{DatabaseID: form.DatabaseID, Name: form.Name, Query: form.Query, Transport: form.Transport})
		redirectAfter(w, r, deps, err, form)
	})
	mux.HandleFunc("POST /api/v1/dashboard/subscriptions/pause", func(w http.ResponseWriter, r *http.Request) {
		if p, ok := formPrincipal(w, r, deps); ok {
			redirectAfter(w, r, deps, service.Pause(p, r.FormValue("configId")), configForm{})
		}
	})
	mux.HandleFunc("POST /api/v1/dashboard/subscriptions/resume", func(w http.ResponseWriter, r *http.Request) {
		if p, ok := formPrincipal(w, r, deps); ok {
			redirectAfter(w, r, deps, service.Resume(p, r.FormValue("configId")), configForm{})
		}
	})
	mux.HandleFunc("POST /api/v1/dashboard/subscriptions/detach", func(w http.ResponseWriter, r *http.Request) {
		if p, ok := formPrincipal(w, r, deps); ok {
			redirectAfter(w, r, deps, service.Detach(p, r.FormValue("configId")), configForm{})
		}
	})
}

// redirectAfter returns a successful form post to the subscriptions page and
// explains a refused one, refilling form when it was a new configuration.
func redirectAfter(w http.ResponseWriter, r *http.Request, deps core.Deps, err error, form configForm) {
	if err != nil {
		status, message := failure(err)
		refused(w, r, deps, status, message, form)
		return
	}
	http.Redirect(w, r, "/subscriptions", http.StatusSeeOther)
}

// render shows the configurations page with status; a refused post passes
// edit to explain it and refill its form.
func render(w http.ResponseWriter, r *http.Request, deps core.Deps, status int, edit func(*pageData)) {
	p, project, err := selected(deps, r)
	if err != nil {
		if errors.Is(err, core.ErrUnauthorized) {
			core.RedirectToLogin(w, r)
			return
		}
		writeError(w, err)
		return
	}
	data := pageData{ProjectID: project.ID, CanWrite: deps.Store.Can(p, core.Organization.CanWrite), Form: configForm{Query: "SELECT * FROM documents LIMIT 100", Transport: "http"}}
	for _, db := range project.Databases {
		data.Databases = append(data.Databases, databaseChoice{ID: db.ID, Name: db.Name})
	}
	data.Configs = slices.Collect(maps.Values(project.SharedResultConfigs))
	slices.SortFunc(data.Databases, func(a, b databaseChoice) int { return strings.Compare(a.Name, b.Name) })
	slices.SortFunc(data.Configs, func(a, b core.SharedResultConfig) int { return strings.Compare(a.Name, b.Name) })
	if edit != nil {
		edit(&data)
	}
	page := core.Page{Layout: "shell", Title: "Query subscriptions", Principal: p, Status: status, View: func(page core.Page) templ.Component { return subscriptionsContent(page, data) }}
	if err := deps.Render.Page(w, r, page); err != nil {
		http.Error(w, "could not render subscriptions", http.StatusInternalServerError)
	}
}

// refused answers a post that was not applied: API clients get status and
// message as plain text, and the page shows the message beside the form,
// refilled with form.
func refused(w http.ResponseWriter, r *http.Request, deps core.Deps, status int, message string, form configForm) {
	if !core.WantsHTML(r) {
		http.Error(w, message, status)
		return
	}
	render(w, r, deps, status, func(d *pageData) {
		if form == (configForm{}) {
			d.Error = message
			return
		}
		d.FormError, d.Form = message, form
	})
}

// formPrincipal resolves a form post through core.FormPrincipal with a
// bounded body, then refuses live mutations and principals without a project.
func formPrincipal(w http.ResponseWriter, r *http.Request, deps core.Deps) (core.Principal, bool) {
	r.Body = http.MaxBytesReader(w, r.Body, 16<<10)
	p, ok := deps.FormPrincipal(w, r)
	if !ok {
		return core.Principal{}, false
	}
	if !deps.Demo {
		writeError(w, errLive)
		return core.Principal{}, false
	}
	if p.ProjectID == "" {
		writeError(w, core.ErrUnauthorized)
		return core.Principal{}, false
	}
	return p, true
}

func selected(deps core.Deps, r *http.Request) (core.Principal, core.Project, error) {
	p, ok := deps.Sessions.Principal(r)
	if !ok || p.ProjectID == "" {
		return core.Principal{}, core.Project{}, core.ErrUnauthorized
	}
	account, err := deps.Store.SnapshotAs(p)
	if err != nil {
		return core.Principal{}, core.Project{}, err
	}
	project, err := account.ProjectFor(p, p.OrganizationID, p.ProjectID)
	return p, project, err
}

// failure maps an error from a configuration change to its status and the
// message the page shows.
func failure(err error) (int, string) {
	reason, invalid := core.InvalidReason(err)
	switch {
	case invalid:
		return http.StatusBadRequest, reason
	case errors.Is(err, core.ErrConflict):
		return http.StatusConflict, "Another configuration already uses that view name."
	case errors.Is(err, core.ErrForbidden):
		return http.StatusForbidden, "Your role can view shared results but not change them."
	case errors.Is(err, core.ErrNotFound):
		return http.StatusNotFound, "That configuration no longer exists."
	default:
		return http.StatusServiceUnavailable, "The configuration could not be saved. Try again."
	}
}

func writeError(w http.ResponseWriter, err error) {
	switch {
	case errors.Is(err, errLive):
		http.Error(w, err.Error(), http.StatusServiceUnavailable)
	case core.WriteAccessError(w, err):
	case errors.Is(err, core.ErrConflict):
		http.Error(w, "view name already exists", http.StatusConflict)
	default:
		http.Error(w, "invalid configuration", http.StatusBadRequest)
	}
}
