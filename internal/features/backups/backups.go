package backups

import (
	"errors"
	"net/http"
	"net/url"
	"strconv"
	"strings"

	"terrascale.tech/dashboard/internal/core"
	"terrascale.tech/dashboard/internal/features/databases"
)

// Mount owns the backup routes and tools of the selected project's databases.
func Mount(mux *http.ServeMux, deps core.Deps) {
	service := NewService(deps)
	mountTools(deps.Tools, service)
	mux.HandleFunc("GET /api/v1/dashboard/backups", func(w http.ResponseWriter, r *http.Request) {
		p, ok := deps.Sessions.Principal(r)
		if !ok || p.ProjectID == "" {
			writeError(w, core.ErrUnauthorized)
			return
		}
		listing, err := service.List(r.Context(), p, r.URL.Query().Get("databaseId"))
		if err != nil {
			writeError(w, err)
			return
		}
		core.WriteJSON(w, http.StatusOK, struct {
			Policy  core.BackupPolicy `json:"policy"`
			Backups []backupView      `json:"backups"`
		}{Policy: listing.Policy, Backups: listing.Backups})
	})
	mux.HandleFunc("POST /api/v1/dashboard/backups", func(w http.ResponseWriter, r *http.Request) {
		p, ok := deps.FormPrincipal(w, r)
		if !ok {
			return
		}
		databaseID := r.FormValue("databaseId")
		created, err := service.Create(r.Context(), p, databaseID)
		if err != nil {
			formFailed(w, r, err, databaseID, nil)
			return
		}
		if core.WantsHTML(r) {
			http.Redirect(w, r, backupsTab(databaseID), http.StatusSeeOther)
			return
		}
		core.WriteJSON(w, http.StatusCreated, created)
	})
	mux.HandleFunc("POST /api/v1/dashboard/backups/policy", func(w http.ResponseWriter, r *http.Request) {
		p, ok := deps.FormPrincipal(w, r)
		if !ok {
			return
		}
		databaseID := r.FormValue("databaseId")
		in := PolicyInput{
			FullBackupCron: normalizeCron(r.FormValue("fullBackupCron")), IncrementalBackupCron: normalizeCron(r.FormValue("incrementalBackupCron")),
			Disabled: r.FormValue("disabled") == "1",
		}
		if retention, err := strconv.Atoi(strings.TrimSpace(r.FormValue("retentionDays"))); err == nil {
			in.RetentionDays = retention
		}
		saved, err := service.SetPolicy(r.Context(), p, databaseID, in)
		if err != nil {
			formFailed(w, r, err, databaseID, url.Values{
				"fullBackupCron": {in.FullBackupCron}, "incrementalBackupCron": {in.IncrementalBackupCron}, "retentionDays": {r.FormValue("retentionDays")},
			})
			return
		}
		if core.WantsHTML(r) {
			http.Redirect(w, r, backupsTab(databaseID), http.StatusSeeOther)
			return
		}
		core.WriteJSON(w, http.StatusOK, saved)
	})
	mux.HandleFunc("POST /api/v1/dashboard/backups/restore", func(w http.ResponseWriter, r *http.Request) {
		p, ok := deps.FormPrincipal(w, r)
		if !ok {
			return
		}
		databaseID := r.FormValue("databaseId")
		if r.FormValue("replace") == "1" {
			backupID := r.FormValue("backupId")
			overwritten, err := service.Overwrite(r.Context(), p, databaseID, backupID, r.FormValue("confirmName"))
			if err != nil {
				formFailed(w, r, err, databaseID, url.Values{"restore": {backupID}})
				return
			}
			if core.WantsHTML(r) {
				http.Redirect(w, r, databases.TabPath(databaseID, "overview", nil), http.StatusSeeOther)
				return
			}
			core.WriteJSON(w, http.StatusOK, overwritten)
			return
		}
		in := RestoreInput{Name: strings.TrimSpace(r.FormValue("name")), BackupID: r.FormValue("backupId")}
		restored, err := service.Restore(r.Context(), p, databaseID, in)
		if err != nil {
			// Refill the name in the restore form of the chosen backup.
			formFailed(w, r, err, databaseID, url.Values{"restore": {in.BackupID}, "name": {in.Name}})
			return
		}
		if core.WantsHTML(r) {
			http.Redirect(w, r, databases.TabPath(restored.DatabaseID, "overview", nil), http.StatusSeeOther)
			return
		}
		core.WriteJSON(w, http.StatusCreated, struct {
			ID   string `json:"id"`
			Name string `json:"name"`
		}{ID: restored.DatabaseID, Name: restored.Name})
	})
}

// backupsTab is the database page that holds the backup forms.
func backupsTab(databaseID string) string {
	return databases.TabPath(databaseID, "backups", nil)
}

// formFailed answers a refused backup form. A database that is gone has no
// backups tab to return to; every other refusal goes back to it with the
// error code and extra values that refill the form.
func formFailed(w http.ResponseWriter, r *http.Request, err error, databaseID string, extra url.Values) {
	if errors.Is(err, core.ErrNotFound) && !errors.Is(err, errBackupMissing) {
		writeError(w, err)
		return
	}
	status, code := failure(err)
	databases.FormFailed(w, r, status, backupsTab(databaseID), code, extra)
}

// failure maps an error from a backup change to its status and error code.
func failure(err error) (int, string) {
	var provider core.ProviderError
	switch {
	case errors.Is(err, errRetention):
		return http.StatusBadRequest, databases.ErrRetention
	case errors.Is(err, errSchedule):
		return http.StatusBadRequest, databases.ErrSchedule
	case errors.Is(err, errConfirmName):
		return http.StatusBadRequest, databases.ErrConfirmRestore
	case errors.Is(err, core.ErrInvalid):
		return http.StatusBadRequest, databases.ErrInvalidName
	case errors.Is(err, errBackupMissing):
		return http.StatusNotFound, databases.ErrBackupMissing
	case errors.Is(err, ErrUnavailable):
		return http.StatusServiceUnavailable, databases.ErrBackupsOffline
	case errors.Is(err, core.ErrDatabaseLimit):
		return http.StatusConflict, databases.ErrPlanLimit
	case errors.Is(err, core.ErrConflict):
		return http.StatusConflict, databases.ErrNameTaken
	case errors.Is(err, core.ErrForbidden), errors.Is(err, core.ErrUnauthorized):
		return http.StatusForbidden, databases.ErrForbidden
	case errors.Is(err, core.ErrNotFound):
		return http.StatusNotFound, databases.ErrMissing
	case errors.As(err, &provider):
		return http.StatusBadGateway, databases.ErrProvider
	default:
		return http.StatusServiceUnavailable, databases.ErrUnavailable
	}
}

func writeError(w http.ResponseWriter, err error) {
	var provider core.ProviderError
	switch {
	case errors.Is(err, core.ErrDataPlane), errors.Is(err, ErrUnavailable):
		http.Error(w, err.Error(), http.StatusServiceUnavailable)
	case errors.As(err, &provider):
		http.Error(w, err.Error(), http.StatusBadGateway)
	case core.WriteAccessError(w, err):
	case errors.Is(err, core.ErrConflict):
		http.Error(w, "conflict", http.StatusConflict)
	case errors.Is(err, core.ErrInvalid):
		http.Error(w, err.Error(), http.StatusBadRequest)
	default:
		http.Error(w, "service unavailable", http.StatusServiceUnavailable)
	}
}
