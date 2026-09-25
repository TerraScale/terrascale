package backups

import (
	json "encoding/json/v2"
	"errors"
	"net/http"
	"net/url"
	"slices"
	"strconv"
	"strings"
	"time"

	"terrascale.tech/dashboard/internal/core"
	sdk "terrascale.tech/sdk"
)

type backupView struct {
	ID        string    `json:"id"`
	Status    string    `json:"status"`
	CreatedAt time.Time `json:"createdAt"`
	SizeBytes int64     `json:"sizeBytes"`
}

func Mount(mux *http.ServeMux, deps core.Deps) {
	mux.HandleFunc("GET /api/v1/dashboard/backups", func(w http.ResponseWriter, r *http.Request) {
		p, project, db, err := selected(deps, r, r.URL.Query().Get("databaseId"))
		if err != nil {
			writeError(w, err)
			return
		}
		if !deps.Demo {
			client, err := liveClient(deps, p)
			if err != nil {
				writeError(w, err)
				return
			}
			readiness, err := client.Backups.ListDatabaseBackups(r.Context(), db.Name)
			if err != nil {
				http.Error(w, "backup readiness failed: "+err.Error(), http.StatusBadGateway)
				return
			}
			writeJSON(w, http.StatusOK, readiness)
			return
		}
		backups := make([]backupView, 0, len(db.Backups))
		for _, backup := range db.Backups {
			backups = append(backups, backupView{ID: backup.ID, Status: backup.Status, CreatedAt: backup.CreatedAt, SizeBytes: backup.SizeBytes})
		}
		writeJSON(w, http.StatusOK, struct {
			Policy  core.BackupPolicy `json:"policy"`
			Backups []backupView      `json:"backups"`
		}{Policy: project.BackupPolicies[db.ID], Backups: backups})
	})
	mux.HandleFunc("POST /api/v1/dashboard/backups", func(w http.ResponseWriter, r *http.Request) {
		if err := deps.CSRF.Verify(r); err != nil {
			http.Error(w, "invalid CSRF token", http.StatusForbidden)
			return
		}
		if err := r.ParseForm(); err != nil {
			http.Error(w, "invalid form", http.StatusBadRequest)
			return
		}
		p, _, db, err := selected(deps, r, r.FormValue("databaseId"))
		if err != nil {
			writeError(w, err)
			return
		}
		if !canManage(deps, p) {
			writeError(w, core.ErrForbidden)
			return
		}
		if !deps.Demo {
			http.Error(w, "on-demand backup is unavailable in the published SDK contract", http.StatusServiceUnavailable)
			return
		}
		var created core.Backup
		err = deps.Store.UpdateAs(p, func(a *core.Account) error {
			project, err := a.ProjectFor(p, p.OrganizationID, p.ProjectID)
			if err != nil {
				return err
			}
			if !a.Organizations[p.OrganizationID].CanManage(p.UserID) {
				return core.ErrForbidden
			}
			current, ok := project.Databases[db.ID]
			if !ok {
				return core.ErrNotFound
			}
			created = core.Backup{ID: core.NewID("bkp"), DatabaseID: db.ID, Status: "ready", CreatedAt: time.Now().UTC(), Documents: cloneDocuments(current.Documents), Schemas: cloneSchemas(current.Schemas), Indexes: cloneIndexes(current.Indexes)}
			for _, docs := range current.Documents {
				for _, body := range docs {
					created.SizeBytes += int64(len(body))
				}
			}
			current.Backups = append(current.Backups, created)
			project.Databases[db.ID] = current
			org := a.Organizations[p.OrganizationID]
			org.Projects[p.ProjectID] = project
			a.Organizations[p.OrganizationID] = org
			return nil
		})
		if err != nil {
			writeError(w, err)
			return
		}
		if wantsHTML(r) {
			redirectToDatabase(w, r, db.ID)
			return
		}
		writeJSON(w, http.StatusCreated, backupView{ID: created.ID, Status: created.Status, CreatedAt: created.CreatedAt, SizeBytes: created.SizeBytes})
	})
	mux.HandleFunc("POST /api/v1/dashboard/backups/policy", func(w http.ResponseWriter, r *http.Request) {
		if err := deps.CSRF.Verify(r); err != nil {
			http.Error(w, "invalid CSRF token", http.StatusForbidden)
			return
		}
		if err := r.ParseForm(); err != nil {
			http.Error(w, "invalid form", http.StatusBadRequest)
			return
		}
		p, _, db, err := selected(deps, r, r.FormValue("databaseId"))
		if err != nil {
			writeError(w, err)
			return
		}
		if !canManage(deps, p) {
			writeError(w, core.ErrForbidden)
			return
		}
		if !deps.Demo {
			http.Error(w, "backup scheduling is unavailable in the published SDK contract", http.StatusServiceUnavailable)
			return
		}
		retention, err := strconv.Atoi(r.FormValue("retentionDays"))
		if err != nil || retention < 1 || retention > 365 {
			http.Error(w, "retention must be 1 to 365 days", http.StatusBadRequest)
			return
		}
		policy := core.BackupPolicy{FullBackupCron: strings.TrimSpace(r.FormValue("fullBackupCron")), IncrementalBackupCron: strings.TrimSpace(r.FormValue("incrementalBackupCron")), RetentionDays: retention, Disabled: r.FormValue("disabled") == "1", UpdatedAt: time.Now().UTC()}
		err = deps.Store.UpdateAs(p, func(a *core.Account) error {
			project, err := a.ProjectFor(p, p.OrganizationID, p.ProjectID)
			if err != nil {
				return err
			}
			if !a.Organizations[p.OrganizationID].CanManage(p.UserID) {
				return core.ErrForbidden
			}
			if _, ok := project.Databases[db.ID]; !ok {
				return core.ErrNotFound
			}
			if project.BackupPolicies == nil {
				project.BackupPolicies = make(map[string]core.BackupPolicy)
			}
			project.BackupPolicies[db.ID] = policy
			org := a.Organizations[p.OrganizationID]
			org.Projects[p.ProjectID] = project
			a.Organizations[p.OrganizationID] = org
			return nil
		})
		if err != nil {
			writeError(w, err)
			return
		}
		if wantsHTML(r) {
			redirectToDatabase(w, r, db.ID)
			return
		}
		writeJSON(w, http.StatusOK, policy)
	})
	mux.HandleFunc("POST /api/v1/dashboard/backups/restore", func(w http.ResponseWriter, r *http.Request) {
		if err := deps.CSRF.Verify(r); err != nil {
			http.Error(w, "invalid CSRF token", http.StatusForbidden)
			return
		}
		if err := r.ParseForm(); err != nil {
			http.Error(w, "invalid form", http.StatusBadRequest)
			return
		}
		p, _, db, err := selected(deps, r, r.FormValue("databaseId"))
		if err != nil {
			writeError(w, err)
			return
		}
		if !canManage(deps, p) {
			writeError(w, core.ErrForbidden)
			return
		}
		name := strings.TrimSpace(r.FormValue("name"))
		if name == "" || len(name) > 63 || strings.ContainsAny(name, "/\\ \t\n") {
			http.Error(w, "invalid restore database name", http.StatusBadRequest)
			return
		}
		if !deps.Demo {
			if !canProvision(deps, p) {
				writeError(w, core.ErrForbidden)
				return
			}
			var at *int64
			if text := r.FormValue("atMillis"); text != "" {
				value, parseErr := strconv.ParseInt(text, 10, 64)
				if parseErr != nil || value < 0 {
					http.Error(w, "invalid restore timestamp", http.StatusBadRequest)
					return
				}
				at = &value
			}
			target := core.Database{ID: core.NewID("db"), ProjectID: p.ProjectID, Name: name, Region: db.Region, Status: "provisioning", CreatedAt: time.Now().UTC()}
			err := deps.Store.UpdateAs(p, func(a *core.Account) error {
				project, err := a.ProjectFor(p, p.OrganizationID, p.ProjectID)
				if err != nil {
					return err
				}
				org := a.Organizations[p.OrganizationID]
				if !org.CanProvisionResources(p.UserID) {
					return core.ErrForbidden
				}
				if _, ok := project.Databases[db.ID]; !ok {
					return core.ErrNotFound
				}
				for _, existing := range project.Databases {
					if existing.Name == name {
						return core.ErrConflict
					}
				}
				project.Databases[target.ID] = target
				org.Projects[p.ProjectID] = project
				a.Organizations[p.OrganizationID] = org
				return nil
			})
			if err != nil {
				writeError(w, err)
				return
			}
			client, err := liveClient(deps, p)
			if err != nil {
				_ = setStatus(deps, p, target.ID, "failed")
				writeError(w, err)
				return
			}
			opID := target.ID
			result, err := client.Backups.RestoreBackup(r.Context(), db.Name, sdk.RestoreBackupRequest{Name: name, At: at, OperationID: opID}, sdk.WithIdempotencyKey(opID))
			if err != nil {
				_ = setStatus(deps, p, target.ID, "failed")
				http.Error(w, "restore failed: "+err.Error(), http.StatusBadGateway)
				return
			}
			if err := setStatus(deps, p, target.ID, result.State); err != nil {
				writeError(w, err)
				return
			}
			writeJSON(w, http.StatusAccepted, struct {
				DatabaseID string                     `json:"dashboardDatabaseId"`
				Operation  *sdk.ProvisioningOperation `json:"operation"`
			}{DatabaseID: target.ID, Operation: result})
			return
		}
		backupID := r.FormValue("backupId")
		var restored core.Database
		err = deps.Store.UpdateAs(p, func(a *core.Account) error {
			project, err := a.ProjectFor(p, p.OrganizationID, p.ProjectID)
			if err != nil {
				return err
			}
			if !a.Organizations[p.OrganizationID].CanManage(p.UserID) {
				return core.ErrForbidden
			}
			if !a.Organizations[p.OrganizationID].CanProvisionResources(p.UserID) {
				return core.ErrForbidden
			}
			current, ok := project.Databases[db.ID]
			if !ok {
				return core.ErrNotFound
			}
			for _, existing := range project.Databases {
				if existing.Name == name {
					return core.ErrConflict
				}
			}
			index := slices.IndexFunc(current.Backups, func(b core.Backup) bool { return b.ID == backupID && b.Status == "ready" })
			if index < 0 {
				return core.ErrNotFound
			}
			backup := current.Backups[index]
			restored = core.Database{ID: core.NewID("db"), ProjectID: project.ID, Name: name, Region: current.Region, Status: "ready", CreatedAt: time.Now().UTC(), Documents: cloneDocuments(backup.Documents), Schemas: cloneSchemas(backup.Schemas), Indexes: cloneIndexes(backup.Indexes)}
			project.Databases[restored.ID] = restored
			org := a.Organizations[p.OrganizationID]
			org.Projects[p.ProjectID] = project
			a.Organizations[p.OrganizationID] = org
			return nil
		})
		if err != nil {
			writeError(w, err)
			return
		}
		if wantsHTML(r) {
			http.Redirect(w, r, "/databases/"+url.PathEscape(restored.ID), http.StatusSeeOther)
			return
		}
		writeJSON(w, http.StatusCreated, struct {
			ID   string `json:"id"`
			Name string `json:"name"`
		}{ID: restored.ID, Name: restored.Name})
	})
}

func wantsHTML(r *http.Request) bool { return strings.Contains(r.Header.Get("Accept"), "text/html") }

// redirectToDatabase returns form submissions to the database's backups tab.
func redirectToDatabase(w http.ResponseWriter, r *http.Request, databaseID string) {
	http.Redirect(w, r, "/databases/"+url.PathEscape(databaseID)+"?tab=backups", http.StatusSeeOther)
}

func selected(deps core.Deps, r *http.Request, databaseID string) (core.Principal, core.Project, core.Database, error) {
	p, ok := deps.Sessions.Principal(r)
	if !ok || p.ProjectID == "" {
		return core.Principal{}, core.Project{}, core.Database{}, core.ErrUnauthorized
	}
	a, err := deps.Store.SnapshotAs(p)
	if err != nil {
		return core.Principal{}, core.Project{}, core.Database{}, err
	}
	project, err := a.ProjectFor(p, p.OrganizationID, p.ProjectID)
	if err != nil {
		return core.Principal{}, core.Project{}, core.Database{}, err
	}
	db, ok := project.Databases[databaseID]
	if !ok || db.ProjectID != project.ID {
		return core.Principal{}, core.Project{}, core.Database{}, core.ErrNotFound
	}
	return p, project, db, nil
}

func liveClient(deps core.Deps, p core.Principal) (*sdk.Client, error) {
	if deps.SDK == nil {
		return nil, errors.New("live data plane unavailable")
	}
	return deps.SDK.ForScope(p.OrganizationID, p.ProjectID)
}

func canManage(deps core.Deps, p core.Principal) bool {
	a, err := deps.Store.SnapshotAs(p)
	return err == nil && a.Organizations[p.OrganizationID].CanManage(p.UserID)
}

func canProvision(deps core.Deps, p core.Principal) bool {
	a, err := deps.Store.SnapshotAs(p)
	return err == nil && a.Organizations[p.OrganizationID].CanProvisionResources(p.UserID)
}

func setStatus(deps core.Deps, p core.Principal, databaseID, status string) error {
	return deps.Store.UpdateAs(p, func(a *core.Account) error {
		project, err := a.ProjectFor(p, p.OrganizationID, p.ProjectID)
		if err != nil {
			return err
		}
		db, ok := project.Databases[databaseID]
		if !ok {
			return core.ErrNotFound
		}
		db.Status = status
		project.Databases[databaseID] = db
		org := a.Organizations[p.OrganizationID]
		org.Projects[p.ProjectID] = project
		a.Organizations[p.OrganizationID] = org
		return nil
	})
}

func cloneDocuments(input map[string]map[string][]byte) map[string]map[string][]byte {
	out := make(map[string]map[string][]byte, len(input))
	for collection, docs := range input {
		out[collection] = make(map[string][]byte, len(docs))
		for id, body := range docs {
			out[collection][id] = slices.Clone(body)
		}
	}
	return out
}

func cloneSchemas(input map[string][]byte) map[string][]byte {
	out := make(map[string][]byte, len(input))
	for collection, body := range input {
		out[collection] = slices.Clone(body)
	}
	return out
}

func cloneIndexes(input map[string][]string) map[string][]string {
	out := make(map[string][]string, len(input))
	for collection, fields := range input {
		out[collection] = slices.Clone(fields)
	}
	return out
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
		http.Error(w, "conflict", http.StatusConflict)
	default:
		http.Error(w, "service unavailable", http.StatusServiceUnavailable)
	}
}

func writeJSON(w http.ResponseWriter, status int, data any) {
	encoded, err := json.Marshal(data)
	if err != nil {
		http.Error(w, "encode response failed", http.StatusInternalServerError)
		return
	}
	w.Header().Set("Content-Type", "application/json")
	w.WriteHeader(status)
	_, _ = w.Write(encoded)
}
