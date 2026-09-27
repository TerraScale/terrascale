// Package querysubscriptions owns project-scoped demo shared-result configurations.
package querysubscriptions

import (
	"errors"
	"maps"
	"net/http"
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

// Mount registers the demo configuration page. It never starts a live stream.
func Mount(mux *http.ServeMux, deps core.Deps) {
	mux.HandleFunc("GET /subscriptions", func(w http.ResponseWriter, r *http.Request) {
		render(w, r, deps, http.StatusOK, nil)
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
		form := configForm{
			DatabaseID: r.FormValue("databaseId"),
			Name:       strings.TrimSpace(r.FormValue("name")),
			Query:      strings.TrimSpace(r.FormValue("query")),
			Transport:  r.FormValue("transport"),
		}
		failed := func(status int, message string) {
			refused(w, r, deps, status, message, form)
		}
		switch {
		case !viewNamePattern.MatchString(form.Name):
			failed(http.StatusBadRequest, "Name the view with 1 to 64 lowercase letters, digits, underscores, and hyphens, starting with a letter.")
			return
		case len(form.Query) > 4096 || !demoQueryPattern.MatchString(form.Query) || sdk.ValidateSQL(form.Query, nil) != nil:
			failed(http.StatusBadRequest, "The demo shares SELECT * FROM collection with an optional LIMIT of 1 to 100.")
			return
		case form.Transport != "http" && form.Transport != "websocket":
			failed(http.StatusBadRequest, "Choose HTTP + CDN or WebSocket delivery.")
			return
		}
		config := core.SharedResultConfig{ID: core.NewID("cfg"), DatabaseID: form.DatabaseID, Name: form.Name, Query: form.Query, Transport: form.Transport, Status: "saved", CreatedAt: time.Now().UTC()}
		err = deps.Store.UpdateAs(p, func(account *core.Account) error {
			project, err := account.ProjectFor(p, p.OrganizationID, p.ProjectID)
			if err != nil {
				return err
			}
			if !account.Organizations[p.OrganizationID].CanWrite(p.UserID) {
				return core.ErrForbidden
			}
			if db, ok := project.Databases[form.DatabaseID]; !ok || db.ProjectID != project.ID {
				return errDatabaseMissing
			}
			for _, existing := range project.SharedResultConfigs {
				if existing.Name == form.Name {
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
			failed(failure(err))
			return
		}
		http.Redirect(w, r, "/subscriptions", http.StatusSeeOther)
	})
	mux.HandleFunc("POST /api/v1/dashboard/subscriptions/pause", func(w http.ResponseWriter, r *http.Request) {
		changeConfig(w, r, deps, func(project *core.Project, config core.SharedResultConfig) {
			config.Status = "paused"
			project.SharedResultConfigs[config.ID] = config
		})
	})
	mux.HandleFunc("POST /api/v1/dashboard/subscriptions/resume", func(w http.ResponseWriter, r *http.Request) {
		changeConfig(w, r, deps, func(project *core.Project, config core.SharedResultConfig) {
			config.Status = "saved"
			project.SharedResultConfigs[config.ID] = config
		})
	})
	mux.HandleFunc("POST /api/v1/dashboard/subscriptions/detach", func(w http.ResponseWriter, r *http.Request) {
		changeConfig(w, r, deps, func(project *core.Project, config core.SharedResultConfig) {
			delete(project.SharedResultConfigs, config.ID)
		})
	})
}

var errDatabaseMissing = errors.New("database not in project")

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
	page := core.Page{Layout: "shell", Title: "Subscriptions", Principal: p, Status: status, View: func(page core.Page) templ.Component { return subscriptionsContent(page, data) }}
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

// changeConfig applies change to the configuration the form names.
func changeConfig(w http.ResponseWriter, r *http.Request, deps core.Deps, change func(*core.Project, core.SharedResultConfig)) {
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
		change(&project, config)
		org := account.Organizations[p.OrganizationID]
		org.Projects[p.ProjectID] = project
		account.Organizations[p.OrganizationID] = org
		return nil
	})
	if err != nil {
		status, message := failure(err)
		refused(w, r, deps, status, message, configForm{})
		return
	}
	http.Redirect(w, r, "/subscriptions", http.StatusSeeOther)
}

func verifyMutation(w http.ResponseWriter, r *http.Request, deps core.Deps) bool {
	if !deps.VerifyCSRF(w, r) {
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

// failure maps an error from a configuration change to its status and the
// message the page shows.
func failure(err error) (int, string) {
	switch {
	case errors.Is(err, errDatabaseMissing):
		return http.StatusBadRequest, "Choose a database in this project."
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
	case core.WriteAccessError(w, err):
	case errors.Is(err, core.ErrConflict):
		http.Error(w, "view name already exists", http.StatusConflict)
	default:
		http.Error(w, "invalid configuration", http.StatusBadRequest)
	}
}
