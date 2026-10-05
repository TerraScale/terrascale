package settings

import (
	"context"
	"fmt"
	"maps"
	"slices"
	"strconv"
	"strings"
	"time"

	"terrascale.tech/dashboard/internal/core"
	"terrascale.tech/dashboard/internal/features/backups"
)

const (
	msgAuditRetention = "Audit retention must be from 1 to 90 days."
	msgConfirmDelete  = "Enter the selected project name exactly to confirm deletion."
	msgProtected      = "The default or last project cannot be deleted."

	msgBackupsUnavailable = "On-demand backups and backup scheduling are not available through the current ts-go contract."
)

// defaultBackupPolicy is shown until a database's policy is first saved.
var defaultBackupPolicy = core.BackupPolicy{FullBackupCron: "0 2 * * *", IncrementalBackupCron: "0 */6 * * *", RetentionDays: 7}

type identityProvider struct {
	Name     string `json:"name"`
	Protocol string `json:"protocol"`
	Issuer   string `json:"issuer"`
	Status   string `json:"status"`
}

type backupPolicyView struct {
	FullBackupCron        string    `json:"fullBackupCron"`
	IncrementalBackupCron string    `json:"incrementalBackupCron"`
	RetentionDays         int       `json:"retentionDays"`
	Disabled              bool      `json:"disabled"`
	Saved                 bool      `json:"saved" jsonschema:"False when the database still uses the default schedule shown here."`
	UpdatedAt             time.Time `json:"updatedAt,omitzero"`
}

type settingsDatabase struct {
	ID           string           `json:"id"`
	Name         string           `json:"name"`
	Region       string           `json:"region"`
	BackupCount  int              `json:"backupCount"`
	BackupPolicy backupPolicyView `json:"backupPolicy"`
}

// projectSettings is the project's security, credential, backup, audit, and
// deletion state that the settings page shows.
type projectSettings struct {
	ProjectID                 string             `json:"projectId"`
	ProjectName               string             `json:"projectName"`
	AuthenticationEnabled     bool               `json:"authenticationEnabled"`
	SecuritySettingsUpdatedAt time.Time          `json:"securitySettingsUpdatedAt,omitzero"`
	IdentityProviders         []identityProvider `json:"identityProviders" jsonschema:"Saved identity provider connection records; they do not verify a live provider."`
	ActiveCredentials         int                `json:"activeCredentials"`
	ActiveM2MClients          int                `json:"activeM2MClients"`
	SavedBackupPolicies       int                `json:"savedBackupPolicies"`
	AuditRetentionDays        int                `json:"auditRetentionDays"`
	Databases                 []settingsDatabase `json:"databases"`
	CanManage                 bool               `json:"canManage" jsonschema:"Whether the caller may change these settings."`
	ProjectCanBeDeleted       bool               `json:"projectCanBeDeleted" jsonschema:"False for the organization's default or last project."`
}

// Service owns project settings in a principal's selected project. HTTP
// handlers and MCP tools both call it; backups belong to the backups slice.
type Service struct {
	deps    core.Deps
	backups backups.Service
}

func (s Service) selected(ctx context.Context, p core.Principal) (core.Account, core.Project, error) {
	if p.ProjectID == "" {
		return core.Account{}, core.Project{}, core.ErrNotFound
	}
	account, err := s.deps.Store.SnapshotAs(ctx, p)
	if err != nil {
		return core.Account{}, core.Project{}, err
	}
	project, err := account.ProjectFor(p, p.OrganizationID, p.ProjectID)
	return account, project, err
}

// Settings returns the selected project's settings.
func (s Service) Settings(ctx context.Context, p core.Principal) (projectSettings, error) {
	account, project, err := s.selected(ctx, p)
	if err != nil {
		return projectSettings{}, err
	}
	return settingsFor(account, project, p), nil
}

// settingsFor summarizes the settings of project, read from one snapshot.
func settingsFor(account core.Account, project core.Project, p core.Principal) projectSettings {
	org := account.Organizations[p.OrganizationID]
	settings := projectSettings{
		ProjectID: project.ID, ProjectName: project.Name,
		AuthenticationEnabled: project.AuthenticationEnabled, SecuritySettingsUpdatedAt: project.SecuritySettingsUpdatedAt,
		IdentityProviders: make([]identityProvider, 0), SavedBackupPolicies: len(project.BackupPolicies),
		AuditRetentionDays: normalizeRetention(org.AuditRetentionDays), Databases: make([]settingsDatabase, 0, len(project.Databases)),
		CanManage: org.CanManage(p.UserID), ProjectCanBeDeleted: !project.IsDefault && len(org.Projects) > 1,
	}
	now := time.Now().UTC()
	for _, key := range project.APIKeys {
		if !key.RevokedAt.IsZero() || (!key.ExpiresAt.IsZero() && !key.ExpiresAt.After(now)) {
			continue
		}
		settings.ActiveCredentials++
		if key.Kind == "m2m" {
			settings.ActiveM2MClients++
		}
	}
	for _, resource := range project.AuthInstance.Resources {
		if resource.Section != "connections" {
			continue
		}
		provider := identityProvider{
			Name: strings.TrimSpace(resource.Values["name"]), Protocol: strings.TrimSpace(resource.Values["protocol"]),
			Issuer: strings.TrimSpace(resource.Values["issuer"]), Status: strings.TrimSpace(resource.Status),
		}
		if provider.Name == "" {
			provider.Name = "Unnamed provider configuration"
		}
		if provider.Status == "" {
			provider.Status = "Status not reported"
		}
		settings.IdentityProviders = append(settings.IdentityProviders, provider)
	}
	slices.SortFunc(settings.IdentityProviders, func(a, b identityProvider) int { return strings.Compare(a.Name, b.Name) })
	for _, db := range project.Databases {
		settings.Databases = append(settings.Databases, settingsDatabase{
			ID: db.ID, Name: db.Name, Region: db.Region, BackupCount: len(db.Backups), BackupPolicy: backupPolicy(project, db.ID),
		})
	}
	slices.SortFunc(settings.Databases, func(a, b settingsDatabase) int { return strings.Compare(a.Name, b.Name) })
	return settings
}

// SetAuditRetention keeps the organization's audit events for days.
func (s Service) SetAuditRetention(ctx context.Context, p core.Principal, days int) error {
	if _, err := s.managed(ctx, p); err != nil {
		return err
	}
	if days < 1 || days > 90 {
		return core.Invalid(msgAuditRetention)
	}
	return s.deps.Store.UpdateAs(ctx, p, func(account *core.Account) error {
		org, err := account.OrganizationFor(p, p.OrganizationID)
		if err != nil {
			return err
		}
		if !org.CanManage(p.UserID) {
			return core.ErrForbidden
		}
		org.AuditRetentionDays = days
		account.Organizations[org.ID] = org
		account.RecordAudit(p, "settings.audit_retention_updated", strconv.Itoa(days), "setting", time.Now().UTC())
		return nil
	})
}

// SetAuthentication turns the project's authentication integration on or off.
func (s Service) SetAuthentication(ctx context.Context, p core.Principal, enabled bool) error {
	if _, err := s.managed(ctx, p); err != nil {
		return err
	}
	now := time.Now().UTC()
	return s.update(ctx, p, func(account *core.Account, project *core.Project) error {
		project.AuthenticationEnabled = enabled
		project.SecuritySettingsUpdatedAt = now
		account.RecordAudit(p, "settings.authentication_updated", project.Name, "setting", now)
		return nil
	})
}

// DeleteProject deletes the selected project once confirmName repeats its
// name. If it was the account's saved project, another project in the
// organization is selected instead.
func (s Service) DeleteProject(ctx context.Context, p core.Principal, confirmName string) error {
	project, err := s.managed(ctx, p)
	if err != nil {
		return err
	}
	if strings.TrimSpace(confirmName) != project.Name {
		return core.Invalid(msgConfirmDelete)
	}
	return s.deps.Store.UpdateAs(ctx, p, func(account *core.Account) error {
		org, err := account.OrganizationFor(p, p.OrganizationID)
		if err != nil {
			return err
		}
		if !org.CanManage(p.UserID) {
			return core.ErrForbidden
		}
		current, exists := org.Projects[p.ProjectID]
		if !exists || current.Name != project.Name {
			return core.ErrNotFound
		}
		if current.IsDefault || len(org.Projects) <= 1 {
			return fmt.Errorf("%w: %s", core.ErrConflict, msgProtected)
		}
		delete(org.Projects, p.ProjectID)
		account.Organizations[org.ID] = org
		if account.ActiveOrganizationID == org.ID && account.ActiveProjectID == p.ProjectID {
			account.ActiveProjectID = slices.Sorted(maps.Keys(org.Projects))[0]
		}
		account.RecordAudit(p, "settings.project_deleted", project.Name, "setting", time.Now().UTC())
		return nil
	})
}

// managed returns the selected project once the principal is confirmed to
// manage its organization.
func (s Service) managed(ctx context.Context, p core.Principal) (core.Project, error) {
	account, project, err := s.selected(ctx, p)
	if err != nil {
		return core.Project{}, err
	}
	if !account.Organizations[p.OrganizationID].CanManage(p.UserID) {
		return core.Project{}, core.ErrForbidden
	}
	return project, nil
}

// update applies mutate to the selected project after rechecking that the
// principal manages its organization.
func (s Service) update(ctx context.Context, p core.Principal, mutate func(*core.Account, *core.Project) error) error {
	return s.deps.Store.UpdateAs(ctx, p, func(account *core.Account) error {
		project, err := account.ProjectFor(p, p.OrganizationID, p.ProjectID)
		if err != nil {
			return err
		}
		if !account.Organizations[p.OrganizationID].CanManage(p.UserID) {
			return core.ErrForbidden
		}
		if err := mutate(account, &project); err != nil {
			return err
		}
		org := account.Organizations[p.OrganizationID]
		org.Projects[project.ID] = project
		account.Organizations[org.ID] = org
		return nil
	})
}

func backupPolicy(project core.Project, databaseID string) backupPolicyView {
	policy, saved := project.BackupPolicies[databaseID]
	if !saved {
		policy = defaultBackupPolicy
	}
	return backupPolicyView{
		FullBackupCron: policy.FullBackupCron, IncrementalBackupCron: policy.IncrementalBackupCron,
		RetentionDays: policy.RetentionDays, Disabled: policy.Disabled, Saved: saved, UpdatedAt: policy.UpdatedAt,
	}
}

func normalizeRetention(days int) int {
	if days < 1 || days > 90 {
		return 90
	}
	return days
}
