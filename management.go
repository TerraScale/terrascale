// Management clients mirroring ts-typescript src/client.ts path templates
// (sdk-route-manifest.json, routeCount 60): onboarding, organizations,
// regions, saved queries, databases, branches, backups, access, usage,
// audit, notifications.
package sdk

import (
	"context"
	"fmt"
	"net/url"
	"strconv"
	"strings"
)

// Organization is an owning org.
type Organization struct {
	ID   string `json:"id"`
	Name string `json:"name"`
}

// Project is a data project under an organization.
type Project struct {
	ID             string       `json:"id"`
	OrganizationID string       `json:"organizationId,omitempty"`
	Name           string       `json:"name"`
	Regions        []RegionCode `json:"regions,omitempty"`
	BackupRegion   RegionCode   `json:"backupRegion,omitempty"`
}

// ManagedDatabase is a database under a project.
type ManagedDatabase struct {
	ID        string `json:"id"`
	ProjectID string `json:"projectId,omitempty"`
	Name      string `json:"name"`
}

// Region describes a serving region.
type Region struct {
	Code        RegionCode `json:"code"`
	DisplayName string     `json:"displayName,omitempty"`
	Available   bool       `json:"available"`
}

// SavedQuery is a named saved query.
type SavedQuery struct {
	ID       string `json:"id"`
	Name     string `json:"name"`
	Query    string `json:"query"`
	Revision string `json:"revision,omitempty"`
}

// BackupJob is a backup or restore job record.
type BackupJob struct {
	ID         string `json:"id"`
	DatabaseID string `json:"databaseId,omitempty"`
	Status     string `json:"status,omitempty"`
}

// TeamMember is a project team membership.
type TeamMember struct {
	UserID string `json:"userId"`
	Role   string `json:"role"`
}

// UsageSummary is per-project usage.
type UsageSummary struct {
	Writes       uint64 `json:"writes"`
	StorageBytes uint64 `json:"storageBytes"`
}

// AuditEvent is one audit record.
type AuditEvent struct {
	ID     string `json:"id"`
	Action string `json:"action,omitempty"`
}

// Notification is one user notification.
type Notification struct {
	ID    string `json:"id"`
	Read  bool   `json:"read,omitempty"`
	Title string `json:"title,omitempty"`
}

func pageQuery(skip, take int) url.Values {
	query := url.Values{}
	if skip > 0 {
		query.Set("skip", strconv.Itoa(skip))
	}
	if take > 0 {
		query.Set("take", strconv.Itoa(take))
	}
	return query
}

func requireID(name, value string) error {
	if strings.TrimSpace(value) == "" {
		return fmt.Errorf("sdk: %s cannot be empty", name)
	}
	return nil
}

// OnboardingClient mirrors onboarding.* routes.
type OnboardingClient struct{ c *Client }

// GetState returns onboarding state.
func (o *OnboardingClient) GetState(ctx context.Context, opts ...RequestOption) (map[string]any, error) {
	var out map[string]any
	if err := o.c.do(ctx, "GET", "/api/v1/onboarding", nil, nil, &out, "onboarding.getState", opts...); err != nil {
		return nil, err
	}
	return out, nil
}

// CreateOrganization creates the onboarding organization.
func (o *OnboardingClient) CreateOrganization(ctx context.Context, name string, opts ...RequestOption) (map[string]any, error) {
	if strings.TrimSpace(name) == "" {
		return nil, fmt.Errorf("sdk: organization name cannot be empty")
	}
	var out map[string]any
	if err := o.c.do(ctx, "POST", "/api/v1/onboarding/organization", nil, map[string]any{"name": name}, &out, "onboarding.organization.create", opts...); err != nil {
		return nil, err
	}
	return out, nil
}

// CreateProject creates the onboarding project.
func (o *OnboardingClient) CreateProject(ctx context.Context, name string, regions []RegionCode, backupRegion RegionCode, opts ...RequestOption) (map[string]any, error) {
	if strings.TrimSpace(name) == "" {
		return nil, fmt.Errorf("sdk: project name cannot be empty")
	}
	var out map[string]any
	body := map[string]any{"name": name, "regions": regions, "backupRegion": backupRegion}
	if err := o.c.do(ctx, "POST", "/api/v1/onboarding/project", nil, body, &out, "onboarding.project.create", opts...); err != nil {
		return nil, err
	}
	return out, nil
}

// OrganizationsClient mirrors organizations.* routes.
type OrganizationsClient struct{ c *Client }

// List pages organizations.
func (o *OrganizationsClient) List(ctx context.Context, skip, take int, opts ...RequestOption) ([]Organization, error) {
	var out []Organization
	if err := o.c.do(ctx, "GET", "/api/v1/organizations", pageQuery(skip, take), nil, &out, "organizations.list", opts...); err != nil {
		return nil, err
	}
	return out, nil
}

// Create creates an organization.
func (o *OrganizationsClient) Create(ctx context.Context, name string, opts ...RequestOption) (*Organization, error) {
	if strings.TrimSpace(name) == "" {
		return nil, fmt.Errorf("sdk: organization name cannot be empty")
	}
	var out Organization
	if err := o.c.do(ctx, "POST", "/api/v1/organizations", nil, map[string]any{"name": name}, &out, "organizations.create", opts...); err != nil {
		return nil, err
	}
	return &out, nil
}

// Get fetches an organization.
func (o *OrganizationsClient) Get(ctx context.Context, organizationID string, opts ...RequestOption) (*Organization, error) {
	if err := requireID("organization id", organizationID); err != nil {
		return nil, err
	}
	var out Organization
	path := "/api/v1/organizations/" + url.PathEscape(organizationID)
	if err := o.c.do(ctx, "GET", path, nil, nil, &out, "organizations.get", opts...); err != nil {
		return nil, err
	}
	return &out, nil
}

// Update renames an organization.
func (o *OrganizationsClient) Update(ctx context.Context, organizationID, name string, opts ...RequestOption) (*Organization, error) {
	if err := requireID("organization id", organizationID); err != nil {
		return nil, err
	}
	var out Organization
	path := "/api/v1/organizations/" + url.PathEscape(organizationID)
	if err := o.c.do(ctx, "PATCH", path, nil, map[string]any{"name": name}, &out, "organizations.update", opts...); err != nil {
		return nil, err
	}
	return &out, nil
}

// Leave leaves an organization.
func (o *OrganizationsClient) Leave(ctx context.Context, organizationID string, opts ...RequestOption) error {
	if err := requireID("organization id", organizationID); err != nil {
		return err
	}
	path := "/api/v1/organizations/" + url.PathEscape(organizationID) + ":leave"
	return o.c.do(ctx, "POST", path, nil, nil, nil, "organizations.leave", opts...)
}

// ListProjects pages an organization's projects.
func (o *OrganizationsClient) ListProjects(ctx context.Context, organizationID string, skip, take int, opts ...RequestOption) ([]Project, error) {
	if err := requireID("organization id", organizationID); err != nil {
		return nil, err
	}
	var out []Project
	path := "/api/v1/organizations/" + url.PathEscape(organizationID) + "/projects"
	if err := o.c.do(ctx, "GET", path, pageQuery(skip, take), nil, &out, "organizations.projects.list", opts...); err != nil {
		return nil, err
	}
	return out, nil
}

// RegionsClient mirrors regions.* routes.
type RegionsClient struct{ c *Client }

// List returns serving regions.
func (r *RegionsClient) List(ctx context.Context, opts ...RequestOption) ([]Region, error) {
	var out struct {
		Regions []Region `json:"regions"`
	}
	if err := r.c.do(ctx, "GET", "/api/v1/regions", nil, nil, &out, "regions.list", opts...); err != nil {
		return nil, err
	}
	return out.Regions, nil
}

// Get returns one region.
func (r *RegionsClient) Get(ctx context.Context, code RegionCode, opts ...RequestOption) (*Region, error) {
	parsed, err := ParseRegionCode(string(code))
	if err != nil {
		return nil, err
	}
	var out Region
	if err := r.c.do(ctx, "GET", "/api/v1/regions/"+url.PathEscape(string(parsed)), nil, nil, &out, "regions.get", opts...); err != nil {
		return nil, err
	}
	return &out, nil
}

// SavedQueriesClient mirrors savedQueries.* routes.
type SavedQueriesClient struct{ c *Client }

func savedQueryBase(projectID string) (string, error) {
	if err := requireID("project id", projectID); err != nil {
		return "", err
	}
	return "/api/v1/projects/" + url.PathEscape(projectID) + "/saved-queries", nil
}

// List pages saved queries.
func (s *SavedQueriesClient) List(ctx context.Context, projectID string, skip, take int, opts ...RequestOption) ([]SavedQuery, error) {
	base, err := savedQueryBase(projectID)
	if err != nil {
		return nil, err
	}
	var out struct {
		Queries []SavedQuery `json:"queries"`
	}
	if err := s.c.do(ctx, "GET", base, pageQuery(skip, take), nil, &out, "savedQueries.list", opts...); err != nil {
		return nil, err
	}
	return out.Queries, nil
}

// Create saves a query.
func (s *SavedQueriesClient) Create(ctx context.Context, projectID, name, query string, opts ...RequestOption) (*SavedQuery, error) {
	base, err := savedQueryBase(projectID)
	if err != nil {
		return nil, err
	}
	var out SavedQuery
	if err := s.c.do(ctx, "POST", base, nil, map[string]any{"name": name, "query": query}, &out, "savedQueries.create", opts...); err != nil {
		return nil, err
	}
	return &out, nil
}

// Get fetches a saved query.
func (s *SavedQueriesClient) Get(ctx context.Context, projectID, queryID string, opts ...RequestOption) (*SavedQuery, error) {
	base, err := savedQueryBase(projectID)
	if err != nil {
		return nil, err
	}
	var out SavedQuery
	if err := s.c.do(ctx, "GET", base+"/"+url.PathEscape(queryID), nil, nil, &out, "savedQueries.get", opts...); err != nil {
		return nil, err
	}
	return &out, nil
}

// Update replaces a saved query.
func (s *SavedQueriesClient) Update(ctx context.Context, projectID, queryID, name, query, revision string, opts ...RequestOption) (*SavedQuery, error) {
	base, err := savedQueryBase(projectID)
	if err != nil {
		return nil, err
	}
	var out SavedQuery
	body := map[string]any{"name": name, "query": query, "revision": revision}
	if err := s.c.do(ctx, "PUT", base+"/"+url.PathEscape(queryID), nil, body, &out, "savedQueries.update", opts...); err != nil {
		return nil, err
	}
	return &out, nil
}

// Delete removes a saved query.
func (s *SavedQueriesClient) Delete(ctx context.Context, projectID, queryID string, opts ...RequestOption) error {
	base, err := savedQueryBase(projectID)
	if err != nil {
		return err
	}
	return s.c.do(ctx, "DELETE", base+"/"+url.PathEscape(queryID), nil, nil, nil, "savedQueries.delete", opts...)
}

// DatabasesClient mirrors databases.* routes.
type DatabasesClient struct{ c *Client }

func databaseBase(projectID string) (string, error) {
	if err := requireID("project id", projectID); err != nil {
		return "", err
	}
	return "/api/v1/projects/" + url.PathEscape(projectID) + "/databases", nil
}

// List pages databases.
func (d *DatabasesClient) List(ctx context.Context, projectID string, skip, take int, opts ...RequestOption) ([]ManagedDatabase, error) {
	base, err := databaseBase(projectID)
	if err != nil {
		return nil, err
	}
	var out []ManagedDatabase
	if err := d.c.do(ctx, "GET", base, pageQuery(skip, take), nil, &out, "databases.list", opts...); err != nil {
		return nil, err
	}
	return out, nil
}

// Create creates a database.
func (d *DatabasesClient) Create(ctx context.Context, projectID, name string, opts ...RequestOption) (*ManagedDatabase, error) {
	base, err := databaseBase(projectID)
	if err != nil {
		return nil, err
	}
	if strings.TrimSpace(name) == "" {
		return nil, fmt.Errorf("sdk: database name cannot be empty")
	}
	var out ManagedDatabase
	if err := d.c.do(ctx, "POST", base, nil, map[string]any{"name": name}, &out, "databases.create", opts...); err != nil {
		return nil, err
	}
	return &out, nil
}

// Get fetches a database.
func (d *DatabasesClient) Get(ctx context.Context, projectID, databaseID string, opts ...RequestOption) (*ManagedDatabase, error) {
	base, err := databaseBase(projectID)
	if err != nil {
		return nil, err
	}
	var out ManagedDatabase
	if err := d.c.do(ctx, "GET", base+"/"+url.PathEscape(databaseID), nil, nil, &out, "databases.get", opts...); err != nil {
		return nil, err
	}
	return &out, nil
}

// Delete removes a database; confirmation carries the delete identity header.
func (d *DatabasesClient) Delete(ctx context.Context, projectID, databaseID, confirmation string, opts ...RequestOption) error {
	base, err := databaseBase(projectID)
	if err != nil {
		return err
	}
	opts = append([]RequestOption{WithDeleteConfirmation(confirmation)}, opts...)
	return d.c.do(ctx, "DELETE", base+"/"+url.PathEscape(databaseID), nil, nil, nil, "databases.delete", opts...)
}

// DatabaseBranchesClient mirrors databaseBranches.* routes.
type DatabaseBranchesClient struct{ c *Client }

// GetCapability returns branch capability for a project.
func (b *DatabaseBranchesClient) GetCapability(ctx context.Context, projectID string, opts ...RequestOption) (map[string]any, error) {
	if err := requireID("project id", projectID); err != nil {
		return nil, err
	}
	var out map[string]any
	path := "/api/v1/projects/" + url.PathEscape(projectID) + "/database-branches/capability"
	if err := b.c.do(ctx, "GET", path, nil, nil, &out, "databaseBranches.getCapability", opts...); err != nil {
		return nil, err
	}
	return out, nil
}

// RequestClone requests a branch clone of a database.
func (b *DatabaseBranchesClient) RequestClone(ctx context.Context, projectID, sourceDatabaseID string, opts ...RequestOption) error {
	if err := requireID("project id", projectID); err != nil {
		return err
	}
	path := "/api/v1/projects/" + url.PathEscape(projectID) + "/database-branches"
	return b.c.do(ctx, "POST", path, nil, map[string]any{"sourceDatabaseId": sourceDatabaseID}, nil, "databaseBranches.requestClone", opts...)
}

// BackupsClient mirrors backups.* routes.
type BackupsClient struct{ c *Client }

func backupBase(projectID, databaseID string) (string, error) {
	if err := requireID("project id", projectID); err != nil {
		return "", err
	}
	if err := requireID("database id", databaseID); err != nil {
		return "", err
	}
	return "/api/v1/projects/" + url.PathEscape(projectID) + "/databases/" + url.PathEscape(databaseID), nil
}

// ListBackups pages backup jobs.
func (b *BackupsClient) ListBackups(ctx context.Context, projectID, databaseID string, skip, take int, opts ...RequestOption) ([]BackupJob, error) {
	base, err := backupBase(projectID, databaseID)
	if err != nil {
		return nil, err
	}
	var out []BackupJob
	if err := b.c.do(ctx, "GET", base+"/backups", pageQuery(skip, take), nil, &out, "backups.list", opts...); err != nil {
		return nil, err
	}
	return out, nil
}

// GetBackup fetches one backup job.
func (b *BackupsClient) GetBackup(ctx context.Context, projectID, databaseID, backupID string, opts ...RequestOption) (*BackupJob, error) {
	base, err := backupBase(projectID, databaseID)
	if err != nil {
		return nil, err
	}
	var out BackupJob
	if err := b.c.do(ctx, "GET", base+"/backups/"+url.PathEscape(backupID), nil, nil, &out, "backups.get", opts...); err != nil {
		return nil, err
	}
	return &out, nil
}

// StartRestore starts a restore job.
func (b *BackupsClient) StartRestore(ctx context.Context, projectID, databaseID, backupID, idempotencyKey string, opts ...RequestOption) (*BackupJob, error) {
	base, err := backupBase(projectID, databaseID)
	if err != nil {
		return nil, err
	}
	var out BackupJob
	body := map[string]any{"backupId": backupID}
	opts = append([]RequestOption{WithIdempotencyKey(idempotencyKey)}, opts...)
	if err := b.c.do(ctx, "POST", base+"/restores", nil, body, &out, "backups.restores.start", opts...); err != nil {
		return nil, err
	}
	return &out, nil
}

// ListRestorePoints returns restore points.
func (b *BackupsClient) ListRestorePoints(ctx context.Context, projectID, databaseID string, opts ...RequestOption) ([]map[string]any, error) {
	base, err := backupBase(projectID, databaseID)
	if err != nil {
		return nil, err
	}
	var out []map[string]any
	if err := b.c.do(ctx, "GET", base+"/restore-points", nil, nil, &out, "backups.restorePoints.list", opts...); err != nil {
		return nil, err
	}
	return out, nil
}

// AccessClient mirrors access.* team-member routes.
type AccessClient struct{ c *Client }

// ListTeamMembers pages project team members.
func (a *AccessClient) ListTeamMembers(ctx context.Context, projectID string, skip, take int, opts ...RequestOption) ([]TeamMember, error) {
	if err := requireID("project id", projectID); err != nil {
		return nil, err
	}
	var out struct {
		Members []TeamMember `json:"members"`
	}
	path := "/api/v1/projects/" + url.PathEscape(projectID) + "/access/team-members"
	if err := a.c.do(ctx, "GET", path, pageQuery(skip, take), nil, &out, "access.teamMembers.list", opts...); err != nil {
		return nil, err
	}
	return out.Members, nil
}

// AddTeamMember adds a member.
func (a *AccessClient) AddTeamMember(ctx context.Context, projectID, userID, role string, opts ...RequestOption) (*TeamMember, error) {
	if err := requireID("project id", projectID); err != nil {
		return nil, err
	}
	var out TeamMember
	path := "/api/v1/projects/" + url.PathEscape(projectID) + "/access/team-members"
	if err := a.c.do(ctx, "POST", path, nil, map[string]any{"userId": userID, "role": role}, &out, "access.teamMembers.add", opts...); err != nil {
		return nil, err
	}
	return &out, nil
}

// RemoveTeamMember removes a member.
func (a *AccessClient) RemoveTeamMember(ctx context.Context, projectID, userID string, opts ...RequestOption) error {
	if err := requireID("project id", projectID); err != nil {
		return err
	}
	path := "/api/v1/projects/" + url.PathEscape(projectID) + "/access/team-members/" + url.PathEscape(userID)
	return a.c.do(ctx, "DELETE", path, nil, nil, nil, "access.teamMembers.remove", opts...)
}

// UsageClient mirrors usage.* routes.
type UsageClient struct{ c *Client }

// GetProjectUsage returns project usage.
func (u *UsageClient) GetProjectUsage(ctx context.Context, projectID string, opts ...RequestOption) (*UsageSummary, error) {
	if err := requireID("project id", projectID); err != nil {
		return nil, err
	}
	var out UsageSummary
	path := "/api/v1/projects/" + url.PathEscape(projectID) + "/usage"
	if err := u.c.do(ctx, "GET", path, nil, nil, &out, "usage.project.get", opts...); err != nil {
		return nil, err
	}
	return &out, nil
}

// GetOrganizationUsage returns organization usage.
func (u *UsageClient) GetOrganizationUsage(ctx context.Context, organizationID string, opts ...RequestOption) (map[string]any, error) {
	if err := requireID("organization id", organizationID); err != nil {
		return nil, err
	}
	var out map[string]any
	path := "/api/v1/organizations/" + url.PathEscape(organizationID) + "/usage"
	if err := u.c.do(ctx, "GET", path, nil, nil, &out, "usage.organization.get", opts...); err != nil {
		return nil, err
	}
	return out, nil
}

// AuditClient mirrors audit.* routes.
type AuditClient struct{ c *Client }

// List pages audit events.
func (a *AuditClient) List(ctx context.Context, projectID string, skip, take int, opts ...RequestOption) ([]AuditEvent, error) {
	if err := requireID("project id", projectID); err != nil {
		return nil, err
	}
	var out struct {
		Events []AuditEvent `json:"events"`
	}
	path := "/api/v1/projects/" + url.PathEscape(projectID) + "/audit"
	if err := a.c.do(ctx, "GET", path, pageQuery(skip, take), nil, &out, "audit.list", opts...); err != nil {
		return nil, err
	}
	return out.Events, nil
}

// Get fetches one audit event.
func (a *AuditClient) Get(ctx context.Context, projectID, eventID string, opts ...RequestOption) (*AuditEvent, error) {
	if err := requireID("project id", projectID); err != nil {
		return nil, err
	}
	var out AuditEvent
	path := "/api/v1/projects/" + url.PathEscape(projectID) + "/audit/" + url.PathEscape(eventID)
	if err := a.c.do(ctx, "GET", path, nil, nil, &out, "audit.get", opts...); err != nil {
		return nil, err
	}
	return &out, nil
}

// NotificationsClient mirrors notifications.* routes.
type NotificationsClient struct{ c *Client }

// List pages notifications.
func (n *NotificationsClient) List(ctx context.Context, opts ...RequestOption) ([]Notification, error) {
	var out []Notification
	if err := n.c.do(ctx, "GET", "/api/v1/users/me/notifications", nil, nil, &out, "notifications.list", opts...); err != nil {
		return nil, err
	}
	return out, nil
}

// MarkRead marks one notification read.
func (n *NotificationsClient) MarkRead(ctx context.Context, notificationID string, opts ...RequestOption) (*Notification, error) {
	if err := requireID("notification id", notificationID); err != nil {
		return nil, err
	}
	var out Notification
	path := "/api/v1/users/me/notifications/" + url.PathEscape(notificationID) + ":mark-read"
	if err := n.c.do(ctx, "POST", path, nil, nil, &out, "notifications.markRead", opts...); err != nil {
		return nil, err
	}
	return &out, nil
}
