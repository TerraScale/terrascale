// Package querysubscriptions owns project-scoped demo shared-result configurations.
package querysubscriptions

import (
	"errors"
	"net/http"
	"net/url"
	"regexp"
	"slices"
	"strings"
	"time"

	"github.com/a-h/templ"
	"terrascale.tech/dashboard/internal/core"
	sdk "terrascale.tech/sdk"
)

var viewNamePattern = regexp.MustCompile(`^[a-z][a-z0-9_-]{0,63}$`)
var demoQueryPattern = regexp.MustCompile(`(?i)^select\s+\*\s+from\s+[a-z][a-z0-9_-]*(?:\s+limit\s+(?:[1-9]|[1-9][0-9]|100))?\s*;?$`)

type databaseChoice struct{ ID, Name string }

type pageData struct {
	ProjectID string
	Databases []databaseChoice
	Configs   []core.SharedResultConfig
}

// Mount registers the demo configuration page. It never starts a live stream.
func Mount(mux *http.ServeMux, deps core.Deps) {
	mux.HandleFunc("GET /subscriptions", func(w http.ResponseWriter, r *http.Request) {
		p, project, err := selected(deps, r)
		if err != nil {
			if errors.Is(err, core.ErrUnauthorized) {
				returnTo := r.URL.RequestURI()
				if len(returnTo) > 2048 {
					returnTo = "/subscriptions"
				}
				http.Redirect(w, r, "/login?returnTo="+url.QueryEscape(returnTo), http.StatusSeeOther)
				return
			}
			writeError(w, err)
			return
		}
		data := pageData{ProjectID: project.ID}
		for _, db := range project.Databases {
			data.Databases = append(data.Databases, databaseChoice{ID: db.ID, Name: db.Name})
		}
		for _, config := range project.SharedResultConfigs {
			data.Configs = append(data.Configs, config)
		}
		slices.SortFunc(data.Databases, func(a, b databaseChoice) int { return strings.Compare(a.Name, b.Name) })
		slices.SortFunc(data.Configs, func(a, b core.SharedResultConfig) int { return strings.Compare(a.Name, b.Name) })
		page := core.Page{Layout: "shell", Title: "Subscriptions", Principal: p, View: func(page core.Page) templ.Component { return subscriptionsContent(page, data) }}
		if err := deps.Render.Page(w, r, page); err != nil {
			http.Error(w, "could not render subscriptions", http.StatusInternalServerError)
		}
	})
	mux.HandleFunc("POST /api/v1/dashboard/subscriptions/attach", func(w http.ResponseWriter, r *http.Request) {
		if !verifyMutation(w, r, deps) {
			return
		}
		p, _, err := selected(deps, r)
		if err != nil {
			writeError(w, err)
			return
		}
		name := strings.TrimSpace(r.FormValue("name"))
		query := strings.TrimSpace(r.FormValue("query"))
		transport := r.FormValue("transport")
		databaseID := r.FormValue("databaseId")
		if !viewNamePattern.MatchString(name) || len(query) > 4096 || !demoQueryPattern.MatchString(query) || sdk.ValidateSQL(query, nil) != nil || (transport != "http" && transport != "websocket") {
			writeError(w, core.ErrInvalid)
			return
		}
		config := core.SharedResultConfig{ID: core.NewID("cfg"), DatabaseID: databaseID, Name: name, Query: query, Transport: transport, Status: "saved", CreatedAt: time.Now().UTC()}
		err = deps.Store.UpdateAs(p, func(account *core.Account) error {
			project, err := account.ProjectFor(p, p.OrganizationID, p.ProjectID)
			if err != nil {
				return err
			}
			if !account.Organizations[p.OrganizationID].CanWrite(p.UserID) {
				return core.ErrForbidden
			}
			if db, ok := project.Databases[databaseID]; !ok || db.ProjectID != project.ID {
				return core.ErrNotFound
			}
			for _, existing := range project.SharedResultConfigs {
				if existing.Name == name {
					return core.ErrConflict
				}
			}
			if project.SharedResultConfigs == nil {
				project.SharedResultConfigs = make(map[string]core.SharedResultConfig)
			}
			project.SharedResultConfigs[config.ID] = config
			org := account.Organizations[p.OrganizationID]
			org.Projects[p.ProjectID] = project
			account.Organizations[p.OrganizationID] = org
			return nil
		})
		if err != nil {
			writeError(w, err)
			return
		}
		http.Redirect(w, r, "/subscriptions", http.StatusSeeOther)
	})
	mux.HandleFunc("POST /api/v1/dashboard/subscriptions/pause", func(w http.ResponseWriter, r *http.Request) {
		changeConfig(w, r, deps, false)
	})
	mux.HandleFunc("POST /api/v1/dashboard/subscriptions/detach", func(w http.ResponseWriter, r *http.Request) {
		changeConfig(w, r, deps, true)
	})
}

func changeConfig(w http.ResponseWriter, r *http.Request, deps core.Deps, detach bool) {
	if !verifyMutation(w, r, deps) {
		return
	}
	p, _, err := selected(deps, r)
	if err != nil {
		writeError(w, err)
		return
	}
	id := r.FormValue("configId")
	err = deps.Store.UpdateAs(p, func(account *core.Account) error {
		project, err := account.ProjectFor(p, p.OrganizationID, p.ProjectID)
		if err != nil {
			return err
		}
		if !account.Organizations[p.OrganizationID].CanWrite(p.UserID) {
			return core.ErrForbidden
		}
		config, ok := project.SharedResultConfigs[id]
		if !ok {
			return core.ErrNotFound
		}
		if detach {
			delete(project.SharedResultConfigs, id)
		} else {
			config.Status = "paused"
			project.SharedResultConfigs[id] = config
		}
		org := account.Organizations[p.OrganizationID]
		org.Projects[p.ProjectID] = project
		account.Organizations[p.OrganizationID] = org
		return nil
	})
	if err != nil {
		writeError(w, err)
		return
	}
	http.Redirect(w, r, "/subscriptions", http.StatusSeeOther)
}

func verifyMutation(w http.ResponseWriter, r *http.Request, deps core.Deps) bool {
	if err := deps.CSRF.Verify(r); err != nil {
		http.Error(w, "invalid CSRF token", http.StatusForbidden)
		return false
	}
	r.Body = http.MaxBytesReader(w, r.Body, 16<<10)
	if err := r.ParseForm(); err != nil {
		http.Error(w, "invalid form", http.StatusBadRequest)
		return false
	}
	if !deps.Demo {
		http.Error(w, "live shared-result configuration is unavailable", http.StatusServiceUnavailable)
		return false
	}
	return true
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

func writeError(w http.ResponseWriter, err error) {
	switch {
	case errors.Is(err, core.ErrUnauthorized):
		http.Error(w, "authentication required", http.StatusUnauthorized)
	case errors.Is(err, core.ErrForbidden):
		http.Error(w, "forbidden", http.StatusForbidden)
	case errors.Is(err, core.ErrNotFound):
		http.Error(w, "not found", http.StatusNotFound)
	case errors.Is(err, core.ErrConflict):
		http.Error(w, "view name already exists", http.StatusConflict)
	default:
		http.Error(w, "invalid configuration", http.StatusBadRequest)
	}
}
