package backups

import (
	"errors"
	"net/http"
	"net/url"
	"slices"
	"strconv"
	"strings"
	"time"

	"github.com/a-h/templ"
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
	mux.HandleFunc("GET /backups", func(w http.ResponseWriter, r *http.Request) { renderPage(w, r, deps) })
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
		if !deps.Store.Can(p, core.Organization.CanManage) {
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
			writeError(w, err)
			return
		}
		if core.WantsHTML(r) {
			http.Redirect(w, r, "/backups?databaseId="+url.QueryEscape(db.ID), http.StatusSeeOther)
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
		if !deps.Store.Can(p, core.Organization.CanManage) {
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
		if core.WantsHTML(r) {
			if r.FormValue("returnTo") == "databaseDetail" {
				http.Redirect(w, r, "/databases/"+url.PathEscape(db.ID)+"?tab=backups", http.StatusSeeOther)
				return
			}
			http.Redirect(w, r, "/backups?databaseId="+url.QueryEscape(db.ID), http.StatusSeeOther)
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
		if !deps.Store.Can(p, core.Organization.CanManage) {
			writeError(w, core.ErrForbidden)
			return
		}
		name := strings.TrimSpace(r.FormValue("name"))
		if name == "" || len(name) > 63 || strings.ContainsAny(name, "/\\ \t\n") {
			http.Error(w, "invalid restore database name", http.StatusBadRequest)
			return
		}
		if !deps.Demo {
			if !deps.Store.Can(p, core.Organization.CanProvisionResources) {
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
			core.WriteJSON(w, http.StatusAccepted, struct {
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
			restored = core.Database{ID: core.NewID("db"), ProjectID: project.ID, Name: name, Region: current.Region, Status: "ready", CreatedAt: time.Now().UTC(), Documents: core.CloneDocuments(backup.Documents), Schemas: core.CloneSchemas(backup.Schemas), Indexes: core.CloneIndexes(backup.Indexes)}
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
		if core.WantsHTML(r) {
			http.Redirect(w, r, "/databases/"+url.PathEscape(restored.ID), http.StatusSeeOther)
			return
		}
		core.WriteJSON(w, http.StatusCreated, struct {
			ID   string `json:"id"`
			Name string `json:"name"`
		}{ID: restored.ID, Name: restored.Name})
	})
}

type pageData struct {
	ProjectID  string
	DatabaseID string
	Databases  []databaseChoice
	Backups    []backupView
	Policy     core.BackupPolicy
	Error      string
}

type databaseChoice struct{ ID, Name string }

func renderPage(w http.ResponseWriter, r *http.Request, deps core.Deps) {
	p, ok := deps.Sessions.Principal(r)
	if !ok || p.ProjectID == "" {
		writeError(w, core.ErrUnauthorized)
		return
	}
	a, err := deps.Store.SnapshotAs(p)
	if err != nil {
		writeError(w, err)
		return
	}
	project, err := a.ProjectFor(p, p.OrganizationID, p.ProjectID)
	if err != nil {
		writeError(w, err)
		return
	}
	data := pageData{ProjectID: project.ID, DatabaseID: r.URL.Query().Get("databaseId"), Error: r.URL.Query().Get("error")}
	for _, db := range project.Databases {
		data.Databases = append(data.Databases, databaseChoice{ID: db.ID, Name: db.Name})
	}
	slices.SortFunc(data.Databases, func(a, b databaseChoice) int { return strings.Compare(a.Name, b.Name) })
	if data.DatabaseID == "" && len(data.Databases) > 0 {
		data.DatabaseID = data.Databases[0].ID
	}
	if data.DatabaseID != "" {
		db, ok := project.Databases[data.DatabaseID]
		if !ok {
			writeError(w, core.ErrNotFound)
			return
		}
		data.Policy = project.BackupPolicies[db.ID]
		for _, backup := range db.Backups {
			data.Backups = append(data.Backups, backupView{ID: backup.ID, Status: backup.Status, CreatedAt: backup.CreatedAt, SizeBytes: backup.SizeBytes})
		}
	}
	page := core.Page{Layout: "shell", Title: "Backups · TerraScale", Principal: p, View: func(page core.Page) templ.Component { return backupsContent(page, data) }, CSRFToken: deps.CSRF.Token(w, r)}
	if err := deps.Render.Page(w, r, page); err != nil {
		http.Error(w, "could not render backups", http.StatusInternalServerError)
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
	case errors.Is(err, core.ErrConflict):
		http.Error(w, "conflict", http.StatusConflict)
	default:
		http.Error(w, "service unavailable", http.StatusServiceUnavailable)
	}
}
