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

// ValidationError identifies an invalid management argument.  Management
// methods return this error before making an HTTP request, so callers can use
// errors.As instead of matching error strings.
type ValidationError struct {
	Field   string
	Message string
}

func (e *ValidationError) Error() string { return "sdk: " + e.Field + ": " + e.Message }

// PaginationError reports an invalid page request.
type PaginationError struct {
	Field string
	Value int
}

func (e *PaginationError) Error() string {
	return fmt.Sprintf("sdk: %s pagination value %d is invalid", e.Field, e.Value)
}

// IdempotencyError reports a mutation called without a usable idempotency
// key. The database management mutations are replayable operations and the
// public contract requires their key to be explicit.
type IdempotencyError struct {
	Reason string
}

func (e *IdempotencyError) Error() string {
	if e.Reason == "" {
		return "sdk: management mutations require a nonblank idempotency key"
	}
	return "sdk: management mutation idempotency key " + e.Reason
}

// PlacementLock is the residency constraint attached to a database.  It is
// immutable for a database and is checked by TerraBase for every derived
// artifact (branches, backups, restores, and exports).
type PlacementLock string

const (
	PlacementLockEUOnly PlacementLock = "eu-only"
	PlacementLockBROnly PlacementLock = "br-only"
)

// DatabaseRecord is the direct TerraBase database-management record.
type DatabaseRecord struct {
	Name      string        `json:"name"`
	Placement string        `json:"placement"`
	Residency PlacementLock `json:"residency"`
	Deleted   bool          `json:"deleted"`
	CreatedAt int64         `json:"created_at"`
	DeletedAt *int64        `json:"deleted_at,omitempty"`
	Parent    *string       `json:"parent,omitempty"`
}

// DatabasePage is the direct database-list response.
type DatabasePage struct {
	Databases  []DatabaseRecord `json:"databases"`
	NextCursor string           `json:"nextCursor,omitempty"`
}

// CreateDatabaseRequest is the direct TerraBase create contract. Placement
// is required; Residency is the optional immutable placement lock.
type CreateDatabaseRequest struct {
	Name      string        `json:"name"`
	Placement []RegionCode  `json:"placement"`
	Residency PlacementLock `json:"residency,omitempty"`
}

// CreateBranchRequest creates a branch/restore target from a database backup.
type CreateBranchRequest struct {
	Name string `json:"name"`
	At   *int64 `json:"at,omitempty"`
}

// BranchRecord is a direct TerraBase branch record.
type BranchRecord struct {
	Name      string        `json:"name"`
	Placement string        `json:"placement,omitempty"`
	Residency PlacementLock `json:"residency,omitempty"`
	Parent    *string       `json:"parent,omitempty"`
	Deleted   bool          `json:"deleted"`
}

// BranchPage is the direct branch-list response.
type BranchPage struct {
	Branches   []BranchRecord `json:"branches"`
	NextCursor string         `json:"nextCursor,omitempty"`
}

// CollectionPage is the direct collection-list response.
type CollectionPage struct {
	Collections []string `json:"collections"`
	NextCursor  string   `json:"nextCursor,omitempty"`
}

// IndexDescriptor is one extracted-column index and its backfill state.
type IndexDescriptor struct {
	Field             string `json:"field"`
	State             string `json:"state"`
	BackfillRemaining uint64 `json:"backfill_remaining"`
}

// IndexPage is the direct collection-index response.
type IndexPage struct {
	Indexes    []IndexDescriptor `json:"indexes"`
	NextCursor string            `json:"nextCursor,omitempty"`
}

// CreateIndexRequest registers one extracted field.
type CreateIndexRequest struct {
	Field string `json:"field"`
}

// BackupReadiness is the direct TerraBase backup status response.
type BackupReadiness struct {
	Ready     bool `json:"ready"`
	Streaming bool `json:"streaming"`
}

// RestoreBackupRequest restores a backup into a new database. At is Unix
// milliseconds, matching the TerraBase PITR contract.
type RestoreBackupRequest struct {
	Name string `json:"name"`
	At   *int64 `json:"at,omitempty"`
}

// DatabaseUsage is the direct database usage response. The server may add
// usage dimensions as the metering contract grows; these are the stable
// billed dimensions in the current draft.
type DatabaseUsage struct {
	Writes       uint64 `json:"writes"`
	StorageBytes uint64 `json:"storageBytes"`
}

type UsageMetric struct {
	Name     string  `json:"name"`
	Quantity float64 `json:"quantity"`
	Unit     string  `json:"unit"`
}

type UsageWorkloadPoint struct {
	Start string `json:"start"`
	End   string `json:"end"`
	Label string `json:"label"`
	Value int64  `json:"value"`
}

type UsageWorkload struct {
	ProjectID   string               `json:"projectId"`
	Range       string               `json:"range"`
	PeriodStart string               `json:"periodStart"`
	PeriodEnd   string               `json:"periodEnd"`
	BucketSize  string               `json:"bucketSize"`
	Points      []UsageWorkloadPoint `json:"points"`
}

type UsageHistoryPoint struct {
	ObservedAt     string `json:"observedAt"`
	StorageBytes   int64  `json:"storageBytes"`
	OperationUnits int64  `json:"operationUnits"`
	EgressBytes    int64  `json:"egressBytes"`
}

type UsageHistory struct {
	ProjectID          string              `json:"projectId"`
	Range              string              `json:"range"`
	RequestedStart     string              `json:"requestedStart"`
	AccessibleStart    string              `json:"accessibleStart"`
	End                string              `json:"end"`
	LimitedByRetention bool                `json:"limitedByRetention"`
	Points             []UsageHistoryPoint `json:"points"`
}

// APIKey is project API-key metadata. Secret material is never present in a
// listing and is returned only by CreateAPIKey and RotateAPIKey.
type APIKey struct {
	KeyID           string   `json:"keyId"`
	ProjectID       string   `json:"projectId"`
	Name            string   `json:"name"`
	Prefix          string   `json:"prefix"`
	LastFour        string   `json:"lastFour"`
	Scopes          []string `json:"scopes"`
	DatabaseID      *string  `json:"databaseId,omitempty"`
	CreatedByUserID *string  `json:"createdByUserId,omitempty"`
	CreatedByEmail  string   `json:"createdByEmail"`
	CreatedAt       string   `json:"createdAt"`
	ExpiresAt       *string  `json:"expiresAt,omitempty"`
	LastUsedAt      *string  `json:"lastUsedAt,omitempty"`
	RevokedAt       *string  `json:"revokedAt,omitempty"`
	IsActive        bool     `json:"isActive"`
}

// CreateAPIKeyRequest is the project API-key creation contract.
type CreateAPIKeyRequest struct {
	Name       string   `json:"name"`
	Scopes     []string `json:"scopes"`
	DatabaseID *string  `json:"databaseId,omitempty"`
	ExpiresAt  *string  `json:"expiresAt,omitempty"`
}

type APIKeyPage struct {
	APIKeys    []APIKey `json:"apiKeys"`
	NextCursor string   `json:"nextCursor,omitempty"`
}

type CreatedAPIKey struct {
	APIKey APIKey `json:"apiKey"`
	Secret string `json:"secret"`
}

type RotatedAPIKey struct {
	APIKey  APIKey `json:"apiKey"`
	Secret  string `json:"secret"`
	Rotated bool   `json:"rotated"`
}

type RevokedAPIKey struct {
	KeyID   string `json:"keyId"`
	Revoked bool   `json:"revoked"`
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
		return &ValidationError{Field: name, Message: "cannot be empty"}
	}
	return nil
}

func managementContext(ctx context.Context) error {
	if ctx == nil {
		return &ValidationError{Field: "context", Message: "cannot be nil"}
	}
	return ctx.Err()
}

func validatePage(page PageRequest) error {
	if page.Skip < 0 {
		return &PaginationError{Field: "skip", Value: page.Skip}
	}
	if page.Take < 0 || page.Take > 1000 {
		return &PaginationError{Field: "take", Value: page.Take}
	}
	return nil
}

// PageRequest is the bounded offset pagination used by the published
// management contract. A zero Take means the server default.
type PageRequest struct {
	Skip int
	Take int
}

func managementMutation(opts []RequestOption) error {
	rc := resolveRequestOptions(opts)
	key := strings.TrimSpace(rc.idempotencyKey)
	if rc.noIdempotency || key == "" {
		return &IdempotencyError{Reason: "must be nonblank"}
	}
	if len(key) > 128 {
		return &IdempotencyError{Reason: "must be at most 128 characters"}
	}
	return nil
}

func validatePlacement(placement []RegionCode, lock PlacementLock) error {
	if len(placement) == 0 {
		return &ValidationError{Field: "placement", Message: "must contain at least one region"}
	}
	seen := make(map[RegionCode]struct{}, len(placement))
	for _, region := range placement {
		parsed, err := ParseRegionCode(string(region))
		if err != nil {
			return &ValidationError{Field: "placement", Message: err.Error()}
		}
		if _, ok := seen[parsed]; ok {
			return &ValidationError{Field: "placement", Message: "cannot contain duplicate regions"}
		}
		seen[parsed] = struct{}{}
	}
	if lock == "" {
		return nil
	}
	allowed := map[RegionCode]struct{}{}
	switch lock {
	case PlacementLockEUOnly:
		allowed[RegionFRA] = struct{}{}
	case PlacementLockBROnly:
		allowed[RegionGRU] = struct{}{}
	default:
		for _, text := range strings.Split(string(lock), ",") {
			parsed, err := ParseRegionCode(text)
			if err != nil {
				return &ValidationError{Field: "residency", Message: "must be eu-only, br-only, or a comma-separated region list"}
			}
			allowed[parsed] = struct{}{}
		}
	}
	for region := range seen {
		if _, ok := allowed[region]; !ok {
			return &ValidationError{Field: "residency", Message: fmt.Sprintf("placement region %s is outside the lock", region)}
		}
	}
	return nil
}

func databasePath(name string) (string, error) {
	if err := requireID("database name", name); err != nil {
		return "", err
	}
	return "/api/v1/databases/" + url.PathEscape(name), nil
}

func collectionPath(database, collection string) (string, error) {
	base, err := databasePath(database)
	if err != nil {
		return "", err
	}
	if err := requireID("collection name", collection); err != nil {
		return "", err
	}
	return base + "/collections/" + url.PathEscape(collection), nil
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

// ListDatabases lists direct TerraBase database records, including
// soft-deleted records. The route has offset pagination even though older
// members may ignore the optional query parameters.
func (d *DatabasesClient) ListDatabases(ctx context.Context, page PageRequest, opts ...RequestOption) (*DatabasePage, error) {
	if err := managementContext(ctx); err != nil {
		return nil, err
	}
	if err := validatePage(page); err != nil {
		return nil, err
	}
	var out DatabasePage
	if err := d.c.do(ctx, "GET", "/api/v1/databases", pageQuery(page.Skip, page.Take), nil, &out, "list_databases", opts...); err != nil {
		return nil, err
	}
	return &out, nil
}

// CreateDatabase creates a direct TerraBase database with an immutable
// placement lock.
func (d *DatabasesClient) CreateDatabase(ctx context.Context, request CreateDatabaseRequest, opts ...RequestOption) (*DatabaseRecord, error) {
	if err := managementContext(ctx); err != nil {
		return nil, err
	}
	if strings.TrimSpace(request.Name) == "" {
		return nil, &ValidationError{Field: "name", Message: "cannot be empty"}
	}
	if err := validatePlacement(request.Placement, request.Residency); err != nil {
		return nil, err
	}
	if err := managementMutation(opts); err != nil {
		return nil, err
	}
	canonicalPlacement := make([]RegionCode, len(request.Placement))
	for i := range request.Placement {
		canonicalPlacement[i], _ = ParseRegionCode(string(request.Placement[i]))
	}
	request.Placement = canonicalPlacement
	var out DatabaseRecord
	if err := d.c.do(ctx, "POST", "/api/v1/databases", nil, request, &out, "create_database", opts...); err != nil {
		return nil, err
	}
	return &out, nil
}

// GetDatabase fetches a direct TerraBase database record.
func (d *DatabasesClient) GetDatabase(ctx context.Context, name string, opts ...RequestOption) (*DatabaseRecord, error) {
	if err := managementContext(ctx); err != nil {
		return nil, err
	}
	path, err := databasePath(name)
	if err != nil {
		return nil, err
	}
	var out DatabaseRecord
	if err := d.c.do(ctx, "GET", path, nil, nil, &out, "get_database", opts...); err != nil {
		return nil, err
	}
	return &out, nil
}

// DeleteDatabase soft-deletes a direct TerraBase database.
func (d *DatabasesClient) DeleteDatabase(ctx context.Context, name string, opts ...RequestOption) (*DatabaseRecord, error) {
	if err := managementContext(ctx); err != nil {
		return nil, err
	}
	path, err := databasePath(name)
	if err != nil {
		return nil, err
	}
	if err := managementMutation(opts); err != nil {
		return nil, err
	}
	var out DatabaseRecord
	if err := d.c.do(ctx, "DELETE", path, nil, nil, &out, "delete_database", opts...); err != nil {
		return nil, err
	}
	return &out, nil
}

// RestoreDatabase restores a soft-deleted database within its retention
// window.
func (d *DatabasesClient) RestoreDatabase(ctx context.Context, name string, opts ...RequestOption) (*DatabaseRecord, error) {
	if err := managementContext(ctx); err != nil {
		return nil, err
	}
	path, err := databasePath(name)
	if err != nil {
		return nil, err
	}
	if err := managementMutation(opts); err != nil {
		return nil, err
	}
	var out DatabaseRecord
	if err := d.c.do(ctx, "POST", path+"/restore", nil, nil, &out, "restore_database", opts...); err != nil {
		return nil, err
	}
	return &out, nil
}

// ListCollections lists the collections currently occupied by a database.
func (d *DatabasesClient) ListCollections(ctx context.Context, database string, page PageRequest, opts ...RequestOption) (*CollectionPage, error) {
	if err := managementContext(ctx); err != nil {
		return nil, err
	}
	if err := validatePage(page); err != nil {
		return nil, err
	}
	path, err := databasePath(database)
	if err != nil {
		return nil, err
	}
	var out CollectionPage
	if err := d.c.do(ctx, "GET", path+"/collections", pageQuery(page.Skip, page.Take), nil, &out, "list_collections", opts...); err != nil {
		return nil, err
	}
	return &out, nil
}

// ListIndexes lists the extracted indexes for one collection.
func (d *DatabasesClient) ListIndexes(ctx context.Context, database, collection string, page PageRequest, opts ...RequestOption) (*IndexPage, error) {
	if err := managementContext(ctx); err != nil {
		return nil, err
	}
	if err := validatePage(page); err != nil {
		return nil, err
	}
	path, err := collectionPath(database, collection)
	if err != nil {
		return nil, err
	}
	var out IndexPage
	if err := d.c.do(ctx, "GET", path+"/indexes", pageQuery(page.Skip, page.Take), nil, &out, "list_indexes", opts...); err != nil {
		return nil, err
	}
	return &out, nil
}

// CreateIndex registers one extracted field. Registering an identical field
// is idempotent server-side, while the request itself remains replay-safe.
func (d *DatabasesClient) CreateIndex(ctx context.Context, database, collection string, request CreateIndexRequest, opts ...RequestOption) (*IndexDescriptor, error) {
	if err := managementContext(ctx); err != nil {
		return nil, err
	}
	if strings.TrimSpace(request.Field) == "" {
		return nil, &ValidationError{Field: "field", Message: "cannot be empty"}
	}
	path, err := collectionPath(database, collection)
	if err != nil {
		return nil, err
	}
	if err := managementMutation(opts); err != nil {
		return nil, err
	}
	var out IndexDescriptor
	if err := d.c.do(ctx, "POST", path+"/indexes", nil, request, &out, "create_index", opts...); err != nil {
		return nil, err
	}
	return &out, nil
}

// DeleteIndex retires an extracted field index.
func (d *DatabasesClient) DeleteIndex(ctx context.Context, database, collection, field string, opts ...RequestOption) error {
	if err := managementContext(ctx); err != nil {
		return err
	}
	path, err := collectionPath(database, collection)
	if err != nil {
		return err
	}
	if err := requireID("index field", field); err != nil {
		return err
	}
	if err := managementMutation(opts); err != nil {
		return err
	}
	return d.c.do(ctx, "DELETE", path+"/indexes/"+url.PathEscape(field), nil, nil, nil, "delete_index", opts...)
}

// GetCollectionSchema fetches the declared schema for one collection.
func (d *DatabasesClient) GetCollectionSchema(ctx context.Context, database, collection string, opts ...RequestOption) (*CollectionSchema, error) {
	if err := managementContext(ctx); err != nil {
		return nil, err
	}
	path, err := collectionPath(database, collection)
	if err != nil {
		return nil, err
	}
	var out CollectionSchema
	if err := d.c.do(ctx, "GET", path+"/schema", nil, nil, &out, "get_schema", opts...); err != nil {
		return nil, err
	}
	return &out, nil
}

// PutCollectionSchema replaces a collection schema.
func (d *DatabasesClient) PutCollectionSchema(ctx context.Context, database, collection string, schema CollectionSchema, opts ...RequestOption) (*CollectionSchema, error) {
	if err := managementContext(ctx); err != nil {
		return nil, err
	}
	path, err := collectionPath(database, collection)
	if err != nil {
		return nil, err
	}
	if strings.TrimSpace(schema.Name) == "" {
		schema.Name = collection
	}
	if schema.Name != collection {
		return nil, &ValidationError{Field: "schema.name", Message: "must match collection"}
	}
	if err := managementMutation(opts); err != nil {
		return nil, err
	}
	var out CollectionSchema
	if err := d.c.do(ctx, "PUT", path+"/schema", nil, schema, &out, "put_schema", opts...); err != nil {
		return nil, err
	}
	return &out, nil
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

// ListBranches lists the branch records whose parent is database.
func (b *DatabaseBranchesClient) ListBranches(ctx context.Context, database string, page PageRequest, opts ...RequestOption) (*BranchPage, error) {
	if err := managementContext(ctx); err != nil {
		return nil, err
	}
	if err := validatePage(page); err != nil {
		return nil, err
	}
	path, err := databasePath(database)
	if err != nil {
		return nil, err
	}
	var out BranchPage
	if err := b.c.do(ctx, "GET", path+"/branches", pageQuery(page.Skip, page.Take), nil, &out, "list_branches", opts...); err != nil {
		return nil, err
	}
	return &out, nil
}

// CreateBranch creates a branch from the source database's retained backup
// stream. The target is always a new database and inherits the source's
// placement lock on the server.
func (b *DatabaseBranchesClient) CreateBranch(ctx context.Context, database string, request CreateBranchRequest, opts ...RequestOption) (*DatabaseRecord, error) {
	if err := managementContext(ctx); err != nil {
		return nil, err
	}
	if strings.TrimSpace(request.Name) == "" {
		return nil, &ValidationError{Field: "name", Message: "cannot be empty"}
	}
	path, err := databasePath(database)
	if err != nil {
		return nil, err
	}
	if err := managementMutation(opts); err != nil {
		return nil, err
	}
	var out DatabaseRecord
	if err := b.c.do(ctx, "POST", path+"/branches", nil, request, &out, "create_branch", opts...); err != nil {
		return nil, err
	}
	return &out, nil
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

// ListDatabaseBackups returns the direct TerraBase backup readiness fact.
func (b *BackupsClient) ListDatabaseBackups(ctx context.Context, database string, opts ...RequestOption) (*BackupReadiness, error) {
	if err := managementContext(ctx); err != nil {
		return nil, err
	}
	path, err := databasePath(database)
	if err != nil {
		return nil, err
	}
	var out BackupReadiness
	if err := b.c.do(ctx, "GET", path+"/backups", nil, nil, &out, "list_backups", opts...); err != nil {
		return nil, err
	}
	return &out, nil
}

// RestoreBackup restores a PITR point into a new database. At is Unix
// milliseconds and is deliberately not converted to local time.
func (b *BackupsClient) RestoreBackup(ctx context.Context, database string, request RestoreBackupRequest, opts ...RequestOption) (*DatabaseRecord, error) {
	if err := managementContext(ctx); err != nil {
		return nil, err
	}
	if strings.TrimSpace(request.Name) == "" {
		return nil, &ValidationError{Field: "name", Message: "cannot be empty"}
	}
	path, err := databasePath(database)
	if err != nil {
		return nil, err
	}
	if err := managementMutation(opts); err != nil {
		return nil, err
	}
	var out DatabaseRecord
	if err := b.c.do(ctx, "POST", path+"/backups/restore", nil, request, &out, "restore_backup", opts...); err != nil {
		return nil, err
	}
	return &out, nil
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

// ListAPIKeys lists project API-key metadata using cursor pagination. The
// route's cursor is opaque and is therefore not decoded or synthesized by
// the SDK.
func (a *AccessClient) ListAPIKeys(ctx context.Context, projectID, cursor string, take int, opts ...RequestOption) (*APIKeyPage, error) {
	if err := managementContext(ctx); err != nil {
		return nil, err
	}
	if err := requireID("project id", projectID); err != nil {
		return nil, err
	}
	if take < 0 || take > 1000 {
		return nil, &PaginationError{Field: "take", Value: take}
	}
	query := url.Values{}
	if strings.TrimSpace(cursor) != "" {
		query.Set("cursor", cursor)
	}
	if take > 0 {
		query.Set("limit", strconv.Itoa(take))
	}
	var out APIKeyPage
	path := "/api/v1/projects/" + url.PathEscape(projectID) + "/auth/api-keys"
	if err := a.c.do(ctx, "GET", path, query, nil, &out, "apiKeys.list", opts...); err != nil {
		return nil, err
	}
	return &out, nil
}

// CreateAPIKey creates a project API key. The secret is returned once.
func (a *AccessClient) CreateAPIKey(ctx context.Context, projectID string, request CreateAPIKeyRequest, opts ...RequestOption) (*CreatedAPIKey, error) {
	if err := managementContext(ctx); err != nil {
		return nil, err
	}
	if err := requireID("project id", projectID); err != nil {
		return nil, err
	}
	if strings.TrimSpace(request.Name) == "" {
		return nil, &ValidationError{Field: "name", Message: "cannot be empty"}
	}
	if err := managementMutation(opts); err != nil {
		return nil, err
	}
	var out CreatedAPIKey
	path := "/api/v1/projects/" + url.PathEscape(projectID) + "/auth/api-keys"
	if err := a.c.do(ctx, "POST", path, nil, request, &out, "apiKeys.create", opts...); err != nil {
		return nil, err
	}
	return &out, nil
}

// RotateAPIKey rotates a project API key. The replacement secret is returned
// once and the old key is invalidated by the service.
func (a *AccessClient) RotateAPIKey(ctx context.Context, projectID, keyID string, opts ...RequestOption) (*RotatedAPIKey, error) {
	if err := managementContext(ctx); err != nil {
		return nil, err
	}
	if err := requireID("project id", projectID); err != nil {
		return nil, err
	}
	if err := requireID("key id", keyID); err != nil {
		return nil, err
	}
	if err := managementMutation(opts); err != nil {
		return nil, err
	}
	var out RotatedAPIKey
	path := "/api/v1/projects/" + url.PathEscape(projectID) + "/auth/api-keys/" + url.PathEscape(keyID) + "/rotate"
	if err := a.c.do(ctx, "POST", path, nil, nil, &out, "apiKeys.rotate", opts...); err != nil {
		return nil, err
	}
	return &out, nil
}

// RevokeAPIKey revokes a project API key.
func (a *AccessClient) RevokeAPIKey(ctx context.Context, projectID, keyID string, opts ...RequestOption) (*RevokedAPIKey, error) {
	if err := managementContext(ctx); err != nil {
		return nil, err
	}
	if err := requireID("project id", projectID); err != nil {
		return nil, err
	}
	if err := requireID("key id", keyID); err != nil {
		return nil, err
	}
	if err := managementMutation(opts); err != nil {
		return nil, err
	}
	var out RevokedAPIKey
	path := "/api/v1/projects/" + url.PathEscape(projectID) + "/auth/api-keys/" + url.PathEscape(keyID)
	if err := a.c.do(ctx, "DELETE", path, nil, nil, &out, "apiKeys.revoke", opts...); err != nil {
		return nil, err
	}
	return &out, nil
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

// GetProjectUsageWorkload returns bounded workload buckets for a project.
func (u *UsageClient) GetProjectUsageWorkload(ctx context.Context, projectID, period string, opts ...RequestOption) (*UsageWorkload, error) {
	if err := managementContext(ctx); err != nil {
		return nil, err
	}
	if err := requireID("project id", projectID); err != nil {
		return nil, err
	}
	if period != "24h" && period != "7d" && period != "30d" {
		return nil, &ValidationError{Field: "range", Message: "must be 24h, 7d, or 30d"}
	}
	var out UsageWorkload
	path := "/api/v1/projects/" + url.PathEscape(projectID) + "/usage/workload"
	query := url.Values{"range": []string{period}}
	if err := u.c.do(ctx, "GET", path, query, nil, &out, "usage.project.workload", opts...); err != nil {
		return nil, err
	}
	return &out, nil
}

// GetProjectUsageHistory returns retained usage history for a project.
func (u *UsageClient) GetProjectUsageHistory(ctx context.Context, projectID, period string, opts ...RequestOption) (*UsageHistory, error) {
	if err := managementContext(ctx); err != nil {
		return nil, err
	}
	if err := requireID("project id", projectID); err != nil {
		return nil, err
	}
	switch period {
	case "24h", "7d", "30d", "90d", "365d":
	default:
		return nil, &ValidationError{Field: "range", Message: "must be 24h, 7d, 30d, 90d, or 365d"}
	}
	var out UsageHistory
	path := "/api/v1/projects/" + url.PathEscape(projectID) + "/usage/history"
	query := url.Values{"range": []string{period}}
	if err := u.c.do(ctx, "GET", path, query, nil, &out, "usage.project.history", opts...); err != nil {
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

// GetDatabaseUsage fetches usage for one direct TerraBase database.
func (u *UsageClient) GetDatabaseUsage(ctx context.Context, database string, opts ...RequestOption) (*DatabaseUsage, error) {
	if err := managementContext(ctx); err != nil {
		return nil, err
	}
	path, err := databasePath(database)
	if err != nil {
		return nil, err
	}
	var out DatabaseUsage
	if err := u.c.do(ctx, "GET", path+"/usage", nil, nil, &out, "database_usage", opts...); err != nil {
		return nil, err
	}
	return &out, nil
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
