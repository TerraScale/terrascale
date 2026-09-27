package backups

import (
	"errors"
	"net/http"
	"net/url"
	"slices"
	"strconv"
	"strings"
	"time"

	"terrascale.tech/dashboard/internal/core"
	"terrascale.tech/dashboard/internal/features/databases"
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
			core.WriteJSON(w, http.StatusOK, readiness)
			return
		}
		backups := make([]backupView, 0, len(db.Backups))
		for _, backup := range db.Backups {
			backups = append(backups, backupView{ID: backup.ID, Status: backup.Status, CreatedAt: backup.CreatedAt, SizeBytes: backup.SizeBytes})
		}
		core.WriteJSON(w, http.StatusOK, struct {
			Policy  core.BackupPolicy `json:"policy"`
			Backups []backupView      `json:"backups"`
		}{Policy: project.BackupPolicies[db.ID], Backups: backups})
	})
	mux.HandleFunc("POST /api/v1/dashboard/backups", func(w http.ResponseWriter, r *http.Request) {
		if !deps.VerifyCSRF(w, r) {
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
		failed := func(status int, code string) {
			databases.FormFailed(w, r, status, backupsTab(db.ID), code, nil)
		}
		if !deps.Store.Can(p, core.Organization.CanManage) {
			failed(http.StatusForbidden, databases.ErrForbidden)
			return
		}
		if !deps.Demo {
			failed(http.StatusServiceUnavailable, databases.ErrBackupsOffline)
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
			created = core.Backup{ID: core.NewID("bkp"), DatabaseID: db.ID, Status: "ready", CreatedAt: time.Now().UTC(), Documents: core.CloneDocuments(current.Documents), Schemas: core.CloneSchemas(current.Schemas), Indexes: core.CloneIndexes(current.Indexes)}
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
			failed(failure(err))
			return
		}
		if core.WantsHTML(r) {
			http.Redirect(w, r, backupsTab(db.ID), http.StatusSeeOther)
			return
		}
		core.WriteJSON(w, http.StatusCreated, backupView{ID: created.ID, Status: created.Status, CreatedAt: created.CreatedAt, SizeBytes: created.SizeBytes})
	})
	mux.HandleFunc("POST /api/v1/dashboard/backups/policy", func(w http.ResponseWriter, r *http.Request) {
		if !deps.VerifyCSRF(w, r) {
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
		full, incremental := strings.Join(strings.Fields(r.FormValue("fullBackupCron")), " "), strings.Join(strings.Fields(r.FormValue("incrementalBackupCron")), " ")
		failed := func(status int, code string) {
			databases.FormFailed(w, r, status, backupsTab(db.ID), code, url.Values{
				"fullBackupCron": {full}, "incrementalBackupCron": {incremental}, "retentionDays": {r.FormValue("retentionDays")},
			})
		}
		if !deps.Store.Can(p, core.Organization.CanManage) {
			failed(http.StatusForbidden, databases.ErrForbidden)
			return
		}
		if !deps.Demo {
			failed(http.StatusServiceUnavailable, databases.ErrBackupsOffline)
			return
		}
		retention, err := strconv.Atoi(strings.TrimSpace(r.FormValue("retentionDays")))
		if err != nil || retention < 1 || retention > 365 {
			failed(http.StatusBadRequest, databases.ErrRetention)
			return
		}
		if !validCron(full) || !validCron(incremental) {
			failed(http.StatusBadRequest, databases.ErrSchedule)
			return
		}
		policy := core.BackupPolicy{FullBackupCron: full, IncrementalBackupCron: incremental, RetentionDays: retention, Disabled: r.FormValue("disabled") == "1", UpdatedAt: time.Now().UTC()}
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
			failed(failure(err))
			return
		}
		if core.WantsHTML(r) {
			http.Redirect(w, r, backupsTab(db.ID), http.StatusSeeOther)
			return
		}
		core.WriteJSON(w, http.StatusOK, policy)
	})
	mux.HandleFunc("POST /api/v1/dashboard/backups/restore", func(w http.ResponseWriter, r *http.Request) {
		if !deps.VerifyCSRF(w, r) {
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
		name, backupID := strings.TrimSpace(r.FormValue("name")), r.FormValue("backupId")
		// failed refills the name in the restore form of the chosen backup.
		failed := func(status int, code string) {
			databases.FormFailed(w, r, status, backupsTab(db.ID), code, url.Values{"restore": {backupID}, "name": {name}})
		}
		if !deps.Store.Can(p, core.Organization.CanManage) {
			failed(http.StatusForbidden, databases.ErrForbidden)
			return
		}
		if !core.ValidDatabaseName(name) {
			failed(http.StatusBadRequest, databases.ErrInvalidName)
			return
		}
		if !deps.Demo {
			if !deps.Store.Can(p, core.Organization.CanProvisionResources) {
				failed(http.StatusForbidden, databases.ErrForbidden)
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
				failed(failure(err))
				return
			}
			client, err := liveClient(deps, p)
			if err != nil {
				_ = setStatus(deps, p, target.ID, "failed")
				failed(http.StatusServiceUnavailable, databases.ErrUnavailable)
				return
			}
			opID := target.ID
			result, err := client.Backups.RestoreBackup(r.Context(), db.Name, sdk.RestoreBackupRequest{Name: name, At: at, OperationID: opID}, sdk.WithIdempotencyKey(opID))
			if err != nil {
				_ = setStatus(deps, p, target.ID, "failed")
				failed(http.StatusBadGateway, databases.ErrProvider)
				return
			}
			if err := setStatus(deps, p, target.ID, result.State); err != nil {
				failed(failure(err))
				return
			}
			if core.WantsHTML(r) {
				http.Redirect(w, r, databases.TabPath(target.ID, "overview", nil), http.StatusSeeOther)
				return
			}
			core.WriteJSON(w, http.StatusAccepted, struct {
				DatabaseID string                     `json:"dashboardDatabaseId"`
				Operation  *sdk.ProvisioningOperation `json:"operation"`
			}{DatabaseID: target.ID, Operation: result})
			return
		}
		var restored core.Database
		err = deps.Store.UpdateAs(p, func(a *core.Account) error {
			project, err := a.ProjectFor(p, p.OrganizationID, p.ProjectID)
			if err != nil {
				return err
			}
			org := a.Organizations[p.OrganizationID]
			if !org.CanManage(p.UserID) || !org.CanProvisionResources(p.UserID) {
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
				return errBackupMissing
			}
			backup := current.Backups[index]
			restored = core.Database{ID: core.NewID("db"), ProjectID: project.ID, Name: name, Region: current.Region, Status: "ready", CreatedAt: time.Now().UTC(), Documents: core.CloneDocuments(backup.Documents), Schemas: core.CloneSchemas(backup.Schemas), Indexes: core.CloneIndexes(backup.Indexes)}
			project.Databases[restored.ID] = restored
			org.Projects[p.ProjectID] = project
			a.Organizations[p.OrganizationID] = org
			return nil
		})
		if err != nil {
			failed(failure(err))
			return
		}
		if core.WantsHTML(r) {
			http.Redirect(w, r, databases.TabPath(restored.ID, "overview", nil), http.StatusSeeOther)
			return
		}
		core.WriteJSON(w, http.StatusCreated, struct {
			ID   string `json:"id"`
			Name string `json:"name"`
		}{ID: restored.ID, Name: restored.Name})
	})
}

var errBackupMissing = errors.New("backup is not available")

// backupsTab is the database page that holds the backup forms.
func backupsTab(databaseID string) string {
	return databases.TabPath(databaseID, "backups", nil)
}

// validCron accepts an empty schedule or five cron fields made of digits,
// ranges, lists, steps, and wildcards.
func validCron(schedule string) bool {
	if schedule == "" {
		return true
	}
	fields := strings.Fields(schedule)
	return len(fields) == 5 && !slices.ContainsFunc(fields, func(field string) bool {
		return strings.Trim(field, "0123456789*,-/") != "" || !strings.ContainsAny(field, "0123456789*")
	})
}

// failure maps an error from a backup change to its status and error code.
func failure(err error) (int, string) {
	switch {
	case errors.Is(err, errBackupMissing):
		return http.StatusNotFound, databases.ErrBackupMissing
	case errors.Is(err, core.ErrDatabaseLimit):
		return http.StatusConflict, databases.ErrPlanLimit
	case errors.Is(err, core.ErrConflict):
		return http.StatusConflict, databases.ErrNameTaken
	case errors.Is(err, core.ErrForbidden), errors.Is(err, core.ErrUnauthorized):
		return http.StatusForbidden, databases.ErrForbidden
	case errors.Is(err, core.ErrNotFound):
		return http.StatusNotFound, databases.ErrMissing
	default:
		return http.StatusServiceUnavailable, databases.ErrUnavailable
	}
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

func writeError(w http.ResponseWriter, err error) {
	switch {
	case core.WriteAccessError(w, err):
	case errors.Is(err, core.ErrDatabaseLimit):
		http.Error(w, core.ErrDatabaseLimit.Error(), http.StatusConflict)
	case errors.Is(err, core.ErrConflict):
		http.Error(w, "conflict", http.StatusConflict)
	default:
		http.Error(w, "service unavailable", http.StatusServiceUnavailable)
	}
}
