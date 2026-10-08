// Package settings owns project security, backup policy, and audit retention settings.
package settings

import (
	"errors"
	"net/http"
	"strconv"

	"terrascale.tech/dashboard/internal/core"
	"terrascale.tech/dashboard/internal/features/backups"
)

// Mount registers project-scoped settings mutations and tools.
func Mount(mux *http.ServeMux, deps core.Deps) {
	service := Service{deps: deps, backups: backups.NewService(deps)}
	mountTools(deps.Tools, service)
	mux.HandleFunc("POST /api/v1/dashboard/settings/audit-retention", func(w http.ResponseWriter, r *http.Request) {
		p, ok := deps.FormPrincipal(w, r)
		if !ok {
			return
		}
		days, err := strconv.Atoi(r.FormValue("retentionDays"))
		if err != nil {
			days = 0
		}
		if err := service.SetAuditRetention(r.Context(), p, days); err != nil {
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
		if err := service.SetAuthentication(r.Context(), p, enabled); err != nil {
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
		_, err = service.backups.SetPolicy(r.Context(), p, databaseID, backups.PolicyInput{
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
		if _, err := service.backups.Create(r.Context(), p, databaseID); err != nil {
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
		ctx := r.Context()
		if err := service.DeleteProject(ctx, p, confirmName); err != nil {
			if errors.Is(err, core.ErrConflict) {
				http.Error(w, msgProtected, http.StatusConflict)
				return
			}
			redirectOrError(w, r, err, "danger", "")
			return
		}
		refreshed, err := deps.Store.CurrentPrincipal(ctx, p.AccountID, p.UserID)
		if err != nil && !errors.Is(err, core.ErrNotFound) {
			http.Error(w, "project deleted, but session could not be refreshed", http.StatusInternalServerError)
			return
		}
		if err == nil && deps.Sessions != nil {
			if err := deps.Sessions.Start(w, r, refreshed); err != nil {
				http.Error(w, "project deleted, but session could not be refreshed", http.StatusInternalServerError)
				return
			}
		}
		http.Redirect(w, r, "/organization", http.StatusSeeOther)
	})
}

// redirectOrError returns validation and provider-contract failures explicitly.
func redirectOrError(w http.ResponseWriter, r *http.Request, err error, tab, database string) {
	reason, invalid := core.InvalidReason(err)
	switch {
	case invalid:
		redirectSettings(w, r, tab, "", database, reason)
	case errors.Is(err, backups.ErrUnavailable):
		http.Error(w, msgBackupsUnavailable, http.StatusServiceUnavailable)
	default:
		writeError(w, err)
	}
}

func redirectSettings(w http.ResponseWriter, r *http.Request, tab, toast, database, errMsg string) {
	if errMsg != "" {
		http.Error(w, errMsg, http.StatusBadRequest)
		return
	}
	http.Redirect(w, r, "/", http.StatusSeeOther)
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
