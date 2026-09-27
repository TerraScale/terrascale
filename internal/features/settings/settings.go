// Package settings owns project security, backup policy, and audit retention settings.
package settings

import (
	"errors"
	"maps"
	"net/http"
	"net/url"
	"regexp"
	"slices"
	"strconv"
	"strings"
	"time"

	"github.com/a-h/templ"

	"terrascale.tech/dashboard/internal/core"
)

var cronPattern = regexp.MustCompile(`^[0-9*/,-]+(?:\s+[0-9*/,-]+){4}$`)

type databaseRow struct {
	ID      string
	Name    string
	Region  string
	Backups []core.Backup
}

type identityProviderRow struct {
	Name     string
	Protocol string
	Issuer   string
	Status   string
}

type pageData struct {
	ProjectName          string
	ProjectID            string
	Tab                  string
	Authentication       bool
	UpdatedAt            string
	IdentityProviders    []identityProviderRow
	ActiveCredentials    int
	ActiveM2MClients     int
	ConfiguredBackups    int
	AuditRetention       int
	Databases            []databaseRow
	SelectedDatabaseID   string
	SelectedBackups      []core.Backup
	Policy               core.BackupPolicy
	PolicySaved          bool
	Credentials          []core.APIKey
	Toast                string
	Error                string
	Demo                 bool
	CanManage            bool
	ProjectCanBeDeleted  bool
	ConfirmationRequired bool
}

// Mount registers settings pages and their project-scoped mutations.
func Mount(mux *http.ServeMux, deps core.Deps) {
	mux.HandleFunc("GET /settings", func(w http.ResponseWriter, r *http.Request) {
		p, account, project, ok := selected(deps, w, r)
		if !ok {
			return
		}
		org := account.Organizations[p.OrganizationID]
		tab := r.URL.Query().Get("tab")
		if !slices.Contains([]string{"identity", "credentials", "webhooks", "backups", "audit", "danger"}, tab) {
			tab = "identity"
		}
		databases := make([]databaseRow, 0, len(project.Databases))
		for _, db := range project.Databases {
			databases = append(databases, databaseRow{ID: db.ID, Name: db.Name, Region: db.Region, Backups: db.Backups})
		}
		slices.SortFunc(databases, func(a, b databaseRow) int { return strings.Compare(a.Name, b.Name) })
		selectedID := r.URL.Query().Get("database")
		if !containsDatabase(databases, selectedID) && len(databases) > 0 {
			selectedID = databases[0].ID
		}
		var selectedBackups []core.Backup
		if database, exists := project.Databases[selectedID]; exists {
			selectedBackups = database.Backups
		}
		policy, policySaved := project.BackupPolicies[selectedID]
		if !policySaved {
			policy = core.BackupPolicy{FullBackupCron: "0 2 * * *", IncrementalBackupCron: "0 */6 * * *", RetentionDays: 7}
		}
		credentials := slices.AppendSeq(make([]core.APIKey, 0, len(project.APIKeys)), maps.Values(project.APIKeys))
		slices.SortFunc(credentials, func(a, b core.APIKey) int { return strings.Compare(a.Name, b.Name) })
		now := time.Now().UTC()
		activeCredentials, activeM2MClients := 0, 0
		for _, key := range project.APIKeys {
			if !key.RevokedAt.IsZero() || (!key.ExpiresAt.IsZero() && !key.ExpiresAt.After(now)) {
				continue
			}
			activeCredentials++
			if key.Kind == "m2m" {
				activeM2MClients++
			}
		}
		identityProviders := make([]identityProviderRow, 0)
		for _, resource := range project.AuthInstance.Resources {
			if resource.Section != "connections" {
				continue
			}
			name := strings.TrimSpace(resource.Values["name"])
			if name == "" {
				name = "Unnamed provider configuration"
			}
			status := strings.TrimSpace(resource.Status)
			if status == "" {
				status = "Status not reported"
			}
			identityProviders = append(identityProviders, identityProviderRow{
				Name: name, Protocol: strings.TrimSpace(resource.Values["protocol"]),
				Issuer: strings.TrimSpace(resource.Values["issuer"]), Status: status,
			})
		}
		slices.SortFunc(identityProviders, func(a, b identityProviderRow) int { return strings.Compare(a.Name, b.Name) })
		data := pageData{
			ProjectName: project.Name, ProjectID: project.ID, Tab: tab,
			Authentication: project.AuthenticationEnabled, UpdatedAt: changedAt(project.SecuritySettingsUpdatedAt),
			IdentityProviders: identityProviders, ActiveCredentials: activeCredentials, ActiveM2MClients: activeM2MClients,
			ConfiguredBackups: len(project.BackupPolicies), AuditRetention: normalizeRetention(org.AuditRetentionDays),
			Databases: databases, SelectedDatabaseID: selectedID, SelectedBackups: selectedBackups,
			Policy: policy, PolicySaved: policySaved, Credentials: credentials,
			Toast: r.URL.Query().Get("toast"), Error: r.URL.Query().Get("error"), Demo: deps.Demo,
			CanManage:           canManage(account, p),
			ProjectCanBeDeleted: !project.IsDefault && len(org.Projects) > 1,
		}
		render(w, r, deps, p, data)
	})

	mux.HandleFunc("POST /api/v1/dashboard/settings/audit-retention", func(w http.ResponseWriter, r *http.Request) {
		if !deps.VerifyCSRF(w, r) {
			return
		}
		p, account, _, ok := selected(deps, w, r)
		if !ok {
			return
		}
		if !canManage(account, p) {
			http.Error(w, "forbidden", http.StatusForbidden)
			return
		}
		if err := r.ParseForm(); err != nil {
			http.Error(w, "invalid form", http.StatusBadRequest)
			return
		}
		days, err := strconv.Atoi(r.FormValue("retentionDays"))
		if err != nil || days < 1 || days > 90 {
			redirectSettings(w, r, "audit", "", "", "Audit retention must be from 1 to 90 days.")
			return
		}
		err = deps.Store.UpdateAs(p, func(account *core.Account) error {
			org, err := account.OrganizationFor(p, p.OrganizationID)
			if err != nil {
				return err
			}
			if err := requireManager(org, p.UserID); err != nil {
				return err
			}
			org.AuditRetentionDays = days
			account.Organizations[org.ID] = org
			record(account, p, "settings.audit_retention_updated", strconv.Itoa(days), time.Now().UTC())
			return nil
		})
		if err != nil {
			writeError(w, err)
			return
		}
		redirectSettings(w, r, "audit", "Audit retention saved.", "", "")
	})

	mux.HandleFunc("POST /api/v1/dashboard/settings/authentication", func(w http.ResponseWriter, r *http.Request) {
		if !deps.VerifyCSRF(w, r) {
			return
		}
		p, account, _, ok := selected(deps, w, r)
		if !ok {
			return
		}
		if !canManage(account, p) {
			http.Error(w, "forbidden", http.StatusForbidden)
			return
		}
		if err := r.ParseForm(); err != nil {
			http.Error(w, "invalid form", http.StatusBadRequest)
			return
		}
		enabled := r.FormValue("enabled") == "true" || r.FormValue("enabled") == "on"
		now := time.Now().UTC()
		err := deps.Store.UpdateAs(p, func(account *core.Account) error {
			project, err := account.ProjectFor(p, p.OrganizationID, p.ProjectID)
			if err != nil {
				return err
			}
			org := account.Organizations[p.OrganizationID]
			if err := requireManager(org, p.UserID); err != nil {
				return err
			}
			project.AuthenticationEnabled = enabled
			project.SecuritySettingsUpdatedAt = now
			org.Projects[project.ID] = project
			account.Organizations[org.ID] = org
			record(account, p, "settings.authentication_updated", project.Name, now)
			return nil
		})
		if err != nil {
			writeError(w, err)
			return
		}
		redirectSettings(w, r, "identity", "Authentication setting saved.", "", "")
	})

	mux.HandleFunc("POST /api/v1/dashboard/settings/backup-policy", func(w http.ResponseWriter, r *http.Request) {
		if !deps.VerifyCSRF(w, r) {
			return
		}
		p, account, project, ok := selected(deps, w, r)
		if !ok {
			return
		}
		if !canManage(account, p) {
			http.Error(w, "forbidden", http.StatusForbidden)
			return
		}
		if err := r.ParseForm(); err != nil {
			http.Error(w, "invalid form", http.StatusBadRequest)
			return
		}
		databaseID := r.FormValue("databaseId")
		if _, exists := project.Databases[databaseID]; !exists {
			http.Error(w, "database not found in selected project", http.StatusNotFound)
			return
		}
		full := strings.TrimSpace(r.FormValue("fullBackupCron"))
		incremental := strings.TrimSpace(r.FormValue("incrementalBackupCron"))
		retention, err := strconv.Atoi(r.FormValue("retentionDays"))
		if !cronPattern.MatchString(full) || !cronPattern.MatchString(incremental) || err != nil || retention < 1 || retention > 35 {
			redirectSettings(w, r, "backups", "", databaseID, "Enter five-field cron schedules and a retention window from 1 to 35 days.")
			return
		}
		policy := core.BackupPolicy{
			FullBackupCron: full, IncrementalBackupCron: incremental, RetentionDays: retention,
			Disabled: r.FormValue("disabled") == "on" || r.FormValue("disabled") == "true", UpdatedAt: time.Now().UTC(),
		}
		err = deps.Store.UpdateAs(p, func(account *core.Account) error {
			project, err := account.ProjectFor(p, p.OrganizationID, p.ProjectID)
			if err != nil {
				return err
			}
			org := account.Organizations[p.OrganizationID]
			if err := requireManager(org, p.UserID); err != nil {
				return err
			}
			if _, exists := project.Databases[databaseID]; !exists {
				return core.ErrNotFound
			}
			if project.BackupPolicies == nil {
				project.BackupPolicies = make(map[string]core.BackupPolicy)
			}
			project.BackupPolicies[databaseID] = policy
			org.Projects[project.ID] = project
			account.Organizations[org.ID] = org
			record(account, p, "settings.backup_policy_updated", databaseID, policy.UpdatedAt)
			return nil
		})
		if err != nil {
			writeError(w, err)
			return
		}
		redirectSettings(w, r, "backups", "Backup policy saved.", databaseID, "")
	})

	mux.HandleFunc("POST /api/v1/dashboard/settings/backup-now", func(w http.ResponseWriter, r *http.Request) {
		if !deps.VerifyCSRF(w, r) {
			return
		}
		p, account, project, ok := selected(deps, w, r)
		if !ok {
			return
		}
		if !canManage(account, p) {
			http.Error(w, "forbidden", http.StatusForbidden)
			return
		}
		if err := r.ParseForm(); err != nil {
			http.Error(w, "invalid form", http.StatusBadRequest)
			return
		}
		databaseID := r.FormValue("databaseId")
		database, exists := project.Databases[databaseID]
		if !exists {
			http.Error(w, "database not found in selected project", http.StatusNotFound)
			return
		}
		if !deps.Demo {
			redirectSettings(w, r, "backups", "", databaseID, "On-demand backup is not available through the current ts-go contract.")
			return
		}
		now := time.Now().UTC()
		backup := core.Backup{ID: core.NewID("bkp"), DatabaseID: databaseID, Status: "Succeeded", CreatedAt: now}
		err := deps.Store.UpdateAs(p, func(account *core.Account) error {
			project, err := account.ProjectFor(p, p.OrganizationID, p.ProjectID)
			if err != nil {
				return err
			}
			org := account.Organizations[p.OrganizationID]
			if err := requireManager(org, p.UserID); err != nil {
				return err
			}
			db, exists := project.Databases[databaseID]
			if !exists || db.Name != database.Name {
				return core.ErrNotFound
			}
			db.Backups = append(db.Backups, backup)
			project.Databases[databaseID] = db
			org = account.Organizations[p.OrganizationID]
			org.Projects[project.ID] = project
			account.Organizations[org.ID] = org
			record(account, p, "settings.backup_started", db.Name, now)
			return nil
		})
		if err != nil {
			writeError(w, err)
			return
		}
		redirectSettings(w, r, "backups", "Demo backup completed.", databaseID, "")
	})

	mux.HandleFunc("POST /api/v1/dashboard/settings/restore", func(w http.ResponseWriter, r *http.Request) {
		if !deps.VerifyCSRF(w, r) {
			return
		}
		p, account, project, ok := selected(deps, w, r)
		if !ok {
			return
		}
		if !canManage(account, p) {
			http.Error(w, "forbidden", http.StatusForbidden)
			return
		}
		if err := r.ParseForm(); err != nil {
			http.Error(w, "invalid form", http.StatusBadRequest)
			return
		}
		generation := r.FormValue("generation")
		var source core.Database
		var backup core.Backup
		for _, db := range project.Databases {
			for _, item := range db.Backups {
				if item.ID == generation {
					source, backup = db, item
					break
				}
			}
		}
		if backup.ID == "" {
			http.Error(w, "restore point not found in selected project", http.StatusNotFound)
			return
		}
		if !deps.Demo {
			redirectSettings(w, r, "backups", "", source.ID, "The provider restore contract requires a point in time; this demo backup ID is not a provider restore point.")
			return
		}
		name := restoredName(project, source.Name)
		restored := source
		restored.ID = core.NewID("db")
		restored.Name = name
		restored.Status = "ready"
		restored.CreatedAt = time.Now().UTC()
		restored.Backups = nil
		if err := deps.Store.UpdateAs(p, func(account *core.Account) error {
			project, err := account.ProjectFor(p, p.OrganizationID, p.ProjectID)
			if err != nil {
				return err
			}
			org := account.Organizations[p.OrganizationID]
			if err := requireManager(org, p.UserID); err != nil {
				return err
			}
			if _, exists := project.Databases[restored.ID]; exists {
				return core.ErrConflict
			}
			if project.Databases == nil {
				project.Databases = make(map[string]core.Database)
			}
			project.Databases[restored.ID] = restored
			org.Projects[project.ID] = project
			account.Organizations[org.ID] = org
			record(account, p, "settings.restore_started", source.Name+" → "+restored.Name, time.Now().UTC())
			return nil
		}); err != nil {
			writeError(w, err)
			return
		}
		redirectSettings(w, r, "backups", "Side-by-side demo restore created "+name+".", source.ID, "")
	})

	mux.HandleFunc("POST /api/v1/dashboard/settings/delete-project", func(w http.ResponseWriter, r *http.Request) {
		if !deps.VerifyCSRF(w, r) {
			return
		}
		p, account, project, ok := selected(deps, w, r)
		if !ok {
			return
		}
		if !canManage(account, p) {
			http.Error(w, "forbidden", http.StatusForbidden)
			return
		}
		if err := r.ParseForm(); err != nil {
			http.Error(w, "invalid form", http.StatusBadRequest)
			return
		}
		if r.FormValue("projectId") != p.ProjectID || strings.TrimSpace(r.FormValue("confirmName")) != project.Name {
			redirectSettings(w, r, "danger", "", "", "Enter the selected project name exactly to confirm deletion.")
			return
		}
		var nextProject string
		err := deps.Store.UpdateAs(p, func(account *core.Account) error {
			org, err := account.OrganizationFor(p, p.OrganizationID)
			if err != nil {
				return err
			}
			if err := requireManager(org, p.UserID); err != nil {
				return err
			}
			current, exists := org.Projects[p.ProjectID]
			if !exists || current.Name != project.Name {
				return core.ErrNotFound
			}
			if current.IsDefault || len(org.Projects) <= 1 {
				return core.ErrConflict
			}
			delete(org.Projects, p.ProjectID)
			ids := make([]string, 0, len(org.Projects))
			for id := range org.Projects {
				ids = append(ids, id)
			}
			slices.Sort(ids)
			nextProject = ids[0]
			account.Organizations[org.ID] = org
			account.ActiveProjectID = nextProject
			record(account, p, "settings.project_deleted", project.Name, time.Now().UTC())
			return nil
		})
		if err != nil {
			if errors.Is(err, core.ErrConflict) {
				redirectSettings(w, r, "danger", "", "", "The default or last project cannot be deleted.")
				return
			}
			writeError(w, err)
			return
		}
		p.ProjectID = nextProject
		var refreshed core.Principal
		err = deps.Store.ViewAs(p, func(account core.Account) error {
			refreshed = account.Principal()
			return nil
		})
		if err == nil && deps.Sessions != nil {
			if err := deps.Sessions.Start(w, r, refreshed); err != nil {
				http.Error(w, "project deleted, but session could not be refreshed", http.StatusInternalServerError)
				return
			}
		}
		http.Redirect(w, r, "/organization", http.StatusSeeOther)
	})
}

func selected(deps core.Deps, w http.ResponseWriter, r *http.Request) (core.Principal, core.Account, core.Project, bool) {
	if deps.Sessions == nil {
		http.Error(w, "authentication required", http.StatusUnauthorized)
		return core.Principal{}, core.Account{}, core.Project{}, false
	}
	p, ok := deps.Sessions.Principal(r)
	if !ok {
		core.RedirectToLogin(w, r)
		return core.Principal{}, core.Account{}, core.Project{}, false
	}
	account, err := deps.Store.SnapshotAs(p)
	if err != nil {
		writeError(w, err)
		return core.Principal{}, core.Account{}, core.Project{}, false
	}
	project, err := account.ProjectFor(p, p.OrganizationID, p.ProjectID)
	if errors.Is(err, core.ErrNotFound) && r.Method == http.MethodGet {
		// Without a project, Overview leads to the project setup.
		http.Redirect(w, r, "/", http.StatusSeeOther)
		return core.Principal{}, core.Account{}, core.Project{}, false
	}
	if err != nil {
		writeError(w, err)
		return core.Principal{}, core.Account{}, core.Project{}, false
	}
	return p, account, project, true
}

func render(w http.ResponseWriter, r *http.Request, deps core.Deps, p core.Principal, data pageData) {
	page := core.Page{
		Layout: "shell", Title: "Settings", Principal: p,
		View: func(view core.Page) templ.Component { return settingsView(data, view.CSRFToken) },
	}
	if err := deps.Render.Page(w, r, page); err != nil {
		http.Error(w, "could not render settings", http.StatusInternalServerError)
	}
}

func canManage(account core.Account, p core.Principal) bool {
	org, ok := account.Organizations[p.OrganizationID]
	if !ok {
		return false
	}
	if org.OwnerUserID == p.UserID {
		return true
	}
	member, ok := org.Members[p.UserID]
	return ok && member.Status == "active" && (member.Role == "owner" || member.Role == "admin")
}

func requireManager(org core.Organization, userID string) error {
	if member, ok := org.Members[userID]; ok {
		if member.Role != "owner" && member.Role != "admin" {
			return core.ErrForbidden
		}
		return nil
	}
	if org.OwnerUserID == userID {
		return nil
	}
	return core.ErrForbidden
}

func record(account *core.Account, p core.Principal, action, target string, now time.Time) {
	account.AuditEvents = append(account.AuditEvents, core.AuditEvent{
		ID: core.NewID("evt"), OrganizationID: p.OrganizationID, ProjectID: p.ProjectID,
		Actor: account.User.Email, Action: action, Target: target, TargetType: "setting", Risk: "low",
		Source: "dashboard", OccurredAt: now,
	})
}

func normalizeRetention(days int) int {
	if days < 1 || days > 90 {
		return 90
	}
	return days
}

func containsDatabase(databases []databaseRow, id string) bool {
	return slices.ContainsFunc(databases, func(database databaseRow) bool { return database.ID == id })
}

func restoredName(project core.Project, source string) string {
	base := source + "-restore"
	if len(base) > 63 {
		base = strings.TrimRight(base[:54], "-") + "-restore"
	}
	for candidate, suffix := base, 2; ; suffix++ {
		duplicate := false
		for _, db := range project.Databases {
			if db.Name == candidate {
				duplicate = true
				break
			}
		}
		if !duplicate {
			return candidate
		}
		candidate = base + "-" + strconv.Itoa(suffix)
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
