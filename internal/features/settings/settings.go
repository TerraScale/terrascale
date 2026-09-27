// Package settings owns project security, backup policy, and audit retention settings.
package settings

import (
	"errors"
	"maps"
	"net/http"
	"net/url"
	"slices"
	"strconv"
	"strings"
	"time"

	"github.com/a-h/templ"

	"terrascale.tech/dashboard/internal/core"
	"terrascale.tech/dashboard/internal/features/backups"
)

type pageData struct {
	projectSettings
	Tab                string
	UpdatedAt          string
	SelectedDatabaseID string
	SelectedBackups    []core.Backup
	Policy             backupPolicyView
	Credentials        []core.APIKey
	Toast              string
	Error              string
	Demo               bool
}

// Mount registers settings pages, their project-scoped mutations, and tools.
func Mount(mux *http.ServeMux, deps core.Deps) {
	service := Service{deps: deps, backups: backups.NewService(deps)}
	mountTools(deps.Tools, service)
	mux.HandleFunc("GET /settings", func(w http.ResponseWriter, r *http.Request) {
		p, ok := signedIn(deps, w, r)
		if !ok {
			return
		}
		account, project, err := service.selected(p)
		if errors.Is(err, core.ErrNotFound) {
			// Without a project, Overview leads to the project setup.
			http.Redirect(w, r, "/", http.StatusSeeOther)
			return
		}
		if err != nil {
			writeError(w, err)
			return
		}
		settings := settingsFor(account, project, p)
		tab := r.URL.Query().Get("tab")
		if !slices.Contains([]string{"identity", "credentials", "webhooks", "backups", "audit", "danger"}, tab) {
			tab = "identity"
		}
		selectedID := r.URL.Query().Get("database")
		if _, exists := project.Databases[selectedID]; !exists && len(settings.Databases) > 0 {
			selectedID = settings.Databases[0].ID
		}
		credentials := slices.SortedFunc(maps.Values(project.APIKeys), func(a, b core.APIKey) int { return strings.Compare(a.Name, b.Name) })
		render(w, r, deps, p, pageData{
			projectSettings: settings, Tab: tab, UpdatedAt: changedAt(settings.SecuritySettingsUpdatedAt),
			SelectedDatabaseID: selectedID, SelectedBackups: project.Databases[selectedID].Backups,
			Policy: backupPolicy(project, selectedID), Credentials: credentials,
			Toast: r.URL.Query().Get("toast"), Error: r.URL.Query().Get("error"), Demo: deps.Demo,
		})
	})

	mux.HandleFunc("POST /api/v1/dashboard/settings/audit-retention", func(w http.ResponseWriter, r *http.Request) {
		p, ok := deps.FormPrincipal(w, r)
		if !ok {
			return
		}
		days, err := strconv.Atoi(r.FormValue("retentionDays"))
		if err != nil {
			days = 0
		}
		if err := service.SetAuditRetention(p, days); err != nil {
			redirectOrError(w, r, err, "audit", "")
			return
		}
		redirectSettings(w, r, "audit", "Audit retention saved.", "", "")
	})

	mux.HandleFunc("POST /api/v1/dashboard/settings/authentication", func(w http.ResponseWriter, r *http.Request) {
		p, ok := deps.FormPrincipal(w, r)
		if !ok {
			return
		}
		enabled := r.FormValue("enabled") == "true" || r.FormValue("enabled") == "on"
		if err := service.SetAuthentication(p, enabled); err != nil {
			writeError(w, err)
			return
		}
		redirectSettings(w, r, "identity", "Authentication setting saved.", "", "")
	})

	mux.HandleFunc("POST /api/v1/dashboard/settings/backup-policy", func(w http.ResponseWriter, r *http.Request) {
		p, ok := deps.FormPrincipal(w, r)
		if !ok {
			return
		}
		databaseID := r.FormValue("databaseId")
		retention, err := strconv.Atoi(r.FormValue("retentionDays"))
		if err != nil {
			retention = 0
		}
		_, err = service.backups.SetPolicy(p, databaseID, backups.PolicyInput{
			FullBackupCron: r.FormValue("fullBackupCron"), IncrementalBackupCron: r.FormValue("incrementalBackupCron"),
			RetentionDays: retention, Disabled: r.FormValue("disabled") == "on" || r.FormValue("disabled") == "true",
		})
		if err != nil {
			redirectOrError(w, r, err, "backups", databaseID)
			return
		}
		redirectSettings(w, r, "backups", "Backup policy saved.", databaseID, "")
	})

	mux.HandleFunc("POST /api/v1/dashboard/settings/backup-now", func(w http.ResponseWriter, r *http.Request) {
		p, ok := deps.FormPrincipal(w, r)
		if !ok {
			return
		}
		databaseID := r.FormValue("databaseId")
		if _, err := service.backups.Create(p, databaseID); err != nil {
			redirectOrError(w, r, err, "backups", databaseID)
			return
		}
		redirectSettings(w, r, "backups", "Demo backup completed.", databaseID, "")
	})

	mux.HandleFunc("POST /api/v1/dashboard/settings/restore", func(w http.ResponseWriter, r *http.Request) {
		p, ok := deps.FormPrincipal(w, r)
		if !ok {
			return
		}
		sourceID := r.FormValue("databaseId")
		if !deps.Demo {
			redirectSettings(w, r, "backups", "", sourceID, "The provider restore contract requires a point in time; this demo backup ID is not a provider restore point.")
			return
		}
		restored, err := service.backups.Restore(r.Context(), p, sourceID, backups.RestoreInput{BackupID: r.FormValue("backupId")})
		if err != nil {
			redirectOrError(w, r, err, "backups", sourceID)
			return
		}
		redirectSettings(w, r, "backups", "Side-by-side demo restore created "+restored.Name+".", sourceID, "")
	})

	mux.HandleFunc("POST /api/v1/dashboard/settings/delete-project", func(w http.ResponseWriter, r *http.Request) {
		p, ok := deps.FormPrincipal(w, r)
		if !ok {
			return
		}
		confirmName := r.FormValue("confirmName")
		if r.FormValue("projectId") != p.ProjectID {
			confirmName = ""
		}
		if err := service.DeleteProject(p, confirmName); err != nil {
			if errors.Is(err, core.ErrConflict) {
				redirectSettings(w, r, "danger", "", "", msgProtected)
				return
			}
			redirectOrError(w, r, err, "danger", "")
			return
		}
		if refreshed, ok := deps.Store.CurrentPrincipal(p.AccountID, p.UserID); ok && deps.Sessions != nil {
			if err := deps.Sessions.Start(w, r, refreshed); err != nil {
				http.Error(w, "project deleted, but session could not be refreshed", http.StatusInternalServerError)
				return
			}
		}
		http.Redirect(w, r, "/organization", http.StatusSeeOther)
	})
}

func signedIn(deps core.Deps, w http.ResponseWriter, r *http.Request) (core.Principal, bool) {
	if deps.Sessions == nil {
		http.Error(w, "authentication required", http.StatusUnauthorized)
		return core.Principal{}, false
	}
	p, ok := deps.Sessions.Principal(r)
	if !ok {
		core.RedirectToLogin(w, r)
		return core.Principal{}, false
	}
	return p, true
}

// redirectOrError shows validation and provider-contract failures on the
// settings tab and answers every other error with its status.
func redirectOrError(w http.ResponseWriter, r *http.Request, err error, tab, database string) {
	reason, invalid := core.InvalidReason(err)
	switch {
	case invalid:
		redirectSettings(w, r, tab, "", database, reason)
	case errors.Is(err, backups.ErrUnavailable):
		redirectSettings(w, r, tab, "", database, msgBackupsUnavailable)
	default:
		writeError(w, err)
	}
}

func render(w http.ResponseWriter, r *http.Request, deps core.Deps, p core.Principal, data pageData) {
	page := core.Page{
		Layout: "shell", Title: "Project settings", Principal: p,
		View: func(view core.Page) templ.Component { return settingsView(data, view.CSRFToken) },
	}
	if err := deps.Render.Page(w, r, page); err != nil {
		http.Error(w, "could not render settings", http.StatusInternalServerError)
	}
}

func redirectSettings(w http.ResponseWriter, r *http.Request, tab, toast, database, errMsg string) {
	query := url.Values{"tab": {tab}}
	if toast != "" {
		query.Set("toast", toast)
	}
	if database != "" {
		query.Set("database", database)
	}
	if errMsg != "" {
		query.Set("error", errMsg)
	}
	http.Redirect(w, r, "/settings?"+query.Encode(), http.StatusSeeOther)
}

func writeError(w http.ResponseWriter, err error) {
	switch {
	case core.WriteAccessError(w, err):
	case errors.Is(err, core.ErrDatabaseLimit):
		http.Error(w, core.ErrDatabaseLimit.Error(), http.StatusConflict)
	case errors.Is(err, core.ErrConflict):
		http.Error(w, "conflict", http.StatusConflict)
	default:
		http.Error(w, "invalid request", http.StatusBadRequest)
	}
}

// changedAt formats a settings timestamp; a zero time means never changed.
func changedAt(t time.Time) string {
	if t.IsZero() {
		return ""
	}
	return t.UTC().Format("Jan 2, 2006 15:04 UTC")
}
