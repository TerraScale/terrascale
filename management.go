// Management clients for the published TerraBase /api/v1 contract. The
// direct database, region-status, backup, branch, and index routes below are
// contract-backed. Compatibility clients for dashboard/project operations
// remain typed but fail closed with ContractGapError until those routes are
// published; they never send invented requests.
package sdk

import (
	"context"
	"fmt"
	"net/url"
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
	Healthy     bool       `json:"healthy,omitempty"`
}

// HealthResponse is the generated response model for GET /api/v1/regions
// and GET /api/v1/health. It identifies the member that served the request.
type HealthResponse struct {
	Region  RegionCode `json:"region"`
	Healthy bool       `json:"healthy"`
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

// DeleteConfirmationError reports a destructive operation that did not carry
// the resource identity as an explicit confirmation.  The confirmation is a
// separate option so a caller cannot accidentally turn a read into a delete
// by reusing a generic request option set.
type DeleteConfirmationError struct {
	Resource string
}

func (e *DeleteConfirmationError) Error() string {
	if e == nil || e.Resource == "" {
		return "sdk: delete confirmation is required"
	}
	return fmt.Sprintf("sdk: delete confirmation is required for %q (use WithDeleteConfirmation)", e.Resource)
}

// Unwrap keeps generic validation handling useful while exposing the more
// specific destructive-operation error through errors.As.
func (e *DeleteConfirmationError) Unwrap() error {
	return &ValidationError{Field: "delete confirmation", Message: "must match the resource identity"}
}

// ContractGapError identifies an SDK operation whose route is not usable in
// the pinned TerraBase contract.  Method and Path are contract metadata, not
// a best-effort URL: a gap is returned before any HTTP request is attempted.
type ContractGapError struct {
	Operation string
	Method    string
	Path      string
	Code      string
	Reason    string
	Fix       string
}

func (e *ContractGapError) Error() string {
	if e == nil {
		return "sdk: contract gap"
	}
	route := e.Operation
	if e.Method != "" || e.Path != "" {
		route = fmt.Sprintf("%s %s", e.Method, e.Path)
		if e.Operation != "" {
			route += " (" + e.Operation + ")"
		}
	}
	message := fmt.Sprintf("sdk: contract gap: %s", route)
	if e.Reason != "" {
		message += ": " + e.Reason
	}
	if e.Fix != "" {
		message += "; fix: " + e.Fix
	}
	return message
}

func contractGap(ctx context.Context, operation, method, path, reason, fix string) error {
	if err := managementContext(ctx); err != nil {
		return err
	}
	return &ContractGapError{
		Operation: operation,
		Method:    method,
		Path:      path,
		Code:      "contract_gap",
		Reason:    reason,
		Fix:       fix,
	}
}

// PlacementLock is the residency constraint attached to a database. It is
// immutable for a database and is checked by TerraBase on the published
// branch, restore and export paths.
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
	Parent    *string       `json:"parent,omitempty"`
}

// DatabasePage is the direct database-list response.
type DatabasePage struct {
	Databases  []DatabaseRecord `json:"databases"`
	NextCursor string           `json:"nextCursor,omitempty"`
}

// DatabaseListResponse is the generated contract name for DatabasePage.
type DatabaseListResponse = DatabasePage

// CreateDatabaseRequest is the direct TerraBase create contract. Placement
// and a stable OperationID are required. Residency may be the empty string
// when no additional immutable lock is requested.
type CreateDatabaseRequest struct {
	Name        string        `json:"name"`
	Placement   []RegionCode  `json:"placement"`
	Residency   PlacementLock `json:"residency"`
	OperationID string        `json:"operation_id"`
}

// CreateBranchRequest creates a branch/restore target from a database backup.
type CreateBranchRequest struct {
	Name        string `json:"name"`
	OperationID string `json:"operation_id"`
}

// BranchRecord is a direct TerraBase branch record. TerraBase returns the
// same metadata shape for branches and databases, including creation time and
// the immutable placement lock.
type BranchRecord = DatabaseRecord

// BranchPage is the direct branch-list response.
type BranchPage struct {
	Branches   []BranchRecord `json:"branches"`
	NextCursor string         `json:"nextCursor,omitempty"`
}

// BranchListResponse is the generated contract name for BranchPage.
type BranchListResponse = BranchPage

// CollectionPage is the direct collection-list response.
type CollectionPage struct {
	Collections []string `json:"collections"`
	NextCursor  string   `json:"nextCursor,omitempty"`
}

// CollectionsResponse is the generated contract name for CollectionPage.
type CollectionsResponse = CollectionPage

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

// IndexListResponse is the generated contract name for IndexPage.
type IndexListResponse = IndexPage

// CreateIndexRequest registers one extracted field.
type CreateIndexRequest struct {
	Field string `json:"field"`
}

// IndexCreatedResponse is the response returned by create_index. Creation
// reports only the field and lifecycle state; backfill_remaining belongs to
// the list/recommendation descriptor.
type IndexCreatedResponse struct {
	Field string `json:"field"`
	State string `json:"state"`
}

// BackupReadiness is the direct TerraBase backup status response.
type BackupReadiness struct {
	Ready     bool `json:"ready"`
	Streaming bool `json:"streaming"`
}

// BackupStatusResponse is the generated contract name for BackupReadiness.
type BackupStatusResponse = BackupReadiness

// RestoreBackupRequest restores a backup into a new database. At is Unix
// milliseconds, matching the TerraBase PITR contract.
type RestoreBackupRequest struct {
	Name        string `json:"name"`
	At          *int64 `json:"at,omitempty"`
	OperationID string `json:"operation_id"`
}

// DatabaseUsage contains authoritative totals at one durable ledger high-water.
// Meter units are decimal strings; storage is byte-seconds, not current bytes.
type DatabaseUsage struct {
	// StorageCompleteThroughMillis is nil until an authoritative storage interval closes.
	StorageCompleteThroughMillis *uint64         `json:"storage_complete_through_millis"`
	TenantID                     string          `json:"tenant_id"`
	DatabaseID                   string          `json:"database_id"`
	SnapshotSequence             uint64          `json:"snapshot_sequence"`
	Meters                       []MeterTotal    `json:"meters"`
	Reads                        ReadAttribution `json:"reads"`
}

type BillableMeter string

const (
	MeterDocumentWriteUnits BillableMeter = "document_write_units"
	MeterStorageByteSeconds BillableMeter = "storage_byte_seconds"
)

type MeterTotal struct {
	Meter BillableMeter `json:"meter"`
	Units string        `json:"units"`
}

// ReadAttribution is unbilled aggregate read attribution.
type ReadAttribution struct {
	TotalReads uint64 `json:"total_reads"`
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

// PageRequest is retained for source compatibility with the pre-contract
// SDK. The current TerraBase management responses do not publish pagination;
// callers must use the complete bounded response.
type PageRequest struct {
	Skip int
	Take int
}

func managementMutation(opts []RequestOption) error {
	rc := resolveRequestOptions(opts)
	key := strings.TrimSpace(rc.idempotencyKey)
	if rc.noIdempotency || key == "" {
		// The generated contract marks idempotency-key optional. A caller that
		// supplies no key still gets one request; Client.do retries only when a
		// usable key is present.
		return nil
	}
	if len([]byte(key)) > 128 {
		return &IdempotencyError{Reason: "must be at most 128 characters"}
	}
	for _, char := range []byte(key) {
		if char < 0x20 || char == 0x7f {
			return &IdempotencyError{Reason: "must not contain control characters"}
		}
	}
	return nil
}

func requiredManagementMutation(opts []RequestOption) error {
	rc := resolveRequestOptions(opts)
	if rc.noIdempotency || strings.TrimSpace(rc.idempotencyKey) == "" {
		return &IdempotencyError{Reason: "must be nonblank"}
	}
	return managementMutation(opts)
}

func deleteConfirmation(opts []RequestOption, resource string) error {
	provided := strings.TrimSpace(resolveRequestOptions(opts).deleteConfirmation)
	if provided == "" || provided != resource {
		return &DeleteConfirmationError{Resource: resource}
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
	if _, err := normalizePlacementLock(lock); err != nil {
		return err
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

func normalizePlacementLock(lock PlacementLock) (PlacementLock, error) {
	value := strings.TrimSpace(string(lock))
	if value == "" {
		return "", nil
	}
	if value == string(PlacementLockEUOnly) || value == string(PlacementLockBROnly) {
		return PlacementLock(value), nil
	}
	seen := map[RegionCode]struct{}{}
	regions := make([]string, 0, 3)
	for _, item := range strings.Split(value, ",") {
		region, err := ParseRegionCode(item)
		if err != nil {
			return "", &ValidationError{Field: "residency", Message: "must be eu-only, br-only, or a comma-separated region list"}
		}
		if _, ok := seen[region]; ok {
			continue
		}
		seen[region] = struct{}{}
		regions = append(regions, string(region))
	}
	if len(regions) == 0 {
		return "", &ValidationError{Field: "residency", Message: "must name at least one region"}
	}
	return PlacementLock(strings.Join(regions, ",")), nil
}

func databasePath(name string) (string, error) {
	if err := validateDatabaseName(name); err != nil {
		return "", err
	}
	return "/api/v1/databases/" + url.PathEscape(name), nil
}

// validateDatabaseName mirrors the TerraBase control-plane identifier bound:
// 1-63 lowercase ASCII letters, digits, or dashes, with an alphanumeric edge.
func validateDatabaseName(name string) error {
	if strings.TrimSpace(name) == "" {
		return &ValidationError{Field: "database name", Message: "cannot be empty"}
	}
	if len([]byte(name)) > 63 {
		return &ValidationError{Field: "database name", Message: "must be at most 63 bytes"}
	}
	for index, char := range []byte(name) {
		letter := char >= 'a' && char <= 'z'
		digit := char >= '0' && char <= '9'
		if !(letter || digit || char == '-') {
			return &ValidationError{Field: "database name", Message: "must use lowercase letters, digits, or dashes"}
		}
		if (index == 0 || index == len(name)-1) && char == '-' {
			return &ValidationError{Field: "database name", Message: "must start and end with a letter or digit"}
		}
	}
	return nil
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
	return nil, contractGap(ctx, "onboarding.getState", "GET", "/api/v1/onboarding",
		"the TerraBase public contract does not publish onboarding routes",
		"create databases through the published /api/v1/databases route")
}

// CreateOrganization creates the onboarding organization.
func (o *OnboardingClient) CreateOrganization(ctx context.Context, name string, opts ...RequestOption) (map[string]any, error) {
	_ = name
	return nil, contractGap(ctx, "onboarding.organization.create", "POST", "/api/v1/onboarding/organization",
		"the TerraBase public contract does not publish onboarding routes", "use the dashboard control plane")
}

// CreateProject creates the onboarding project.
func (o *OnboardingClient) CreateProject(ctx context.Context, name string, regions []RegionCode, backupRegion RegionCode, opts ...RequestOption) (map[string]any, error) {
	_, _, _ = name, regions, backupRegion
	return nil, contractGap(ctx, "onboarding.project.create", "POST", "/api/v1/onboarding/project",
		"the TerraBase public contract does not publish onboarding routes", "use the dashboard control plane")
}

// OrganizationsClient mirrors organizations.* routes.
type OrganizationsClient struct{ c *Client }

// List pages organizations.
func (o *OrganizationsClient) List(ctx context.Context, skip, take int, opts ...RequestOption) ([]Organization, error) {
	_, _ = skip, take
	return nil, contractGap(ctx, "organizations.list", "GET", "/api/v1/organizations",
		"the TerraBase public contract does not publish organization routes", "use the dashboard control plane")
}

// Create creates an organization.
func (o *OrganizationsClient) Create(ctx context.Context, name string, opts ...RequestOption) (*Organization, error) {
	_ = name
	return nil, contractGap(ctx, "organizations.create", "POST", "/api/v1/organizations",
		"the TerraBase public contract does not publish organization routes", "use the dashboard control plane")
}

// Get fetches an organization.
func (o *OrganizationsClient) Get(ctx context.Context, organizationID string, opts ...RequestOption) (*Organization, error) {
	_ = organizationID
	return nil, contractGap(ctx, "organizations.get", "GET", "/api/v1/organizations/{organizationId}",
		"the TerraBase public contract does not publish organization routes", "use the dashboard control plane")
}

// Update renames an organization.
func (o *OrganizationsClient) Update(ctx context.Context, organizationID, name string, opts ...RequestOption) (*Organization, error) {
	_, _ = organizationID, name
	return nil, contractGap(ctx, "organizations.update", "PATCH", "/api/v1/organizations/{organizationId}",
		"the TerraBase public contract does not publish organization routes", "use the dashboard control plane")
}

// Leave leaves an organization.
func (o *OrganizationsClient) Leave(ctx context.Context, organizationID string, opts ...RequestOption) error {
	_ = organizationID
	return contractGap(ctx, "organizations.leave", "POST", "/api/v1/organizations/{organizationId}:leave",
		"the TerraBase public contract does not publish organization routes", "use the dashboard control plane")
}

// ListProjects pages an organization's projects.
func (o *OrganizationsClient) ListProjects(ctx context.Context, organizationID string, skip, take int, opts ...RequestOption) ([]Project, error) {
	_, _, _ = organizationID, skip, take
	return nil, contractGap(ctx, "organizations.projects.list", "GET", "/api/v1/organizations/{organizationId}/projects",
		"the TerraBase public contract does not publish organization routes", "use the dashboard control plane")
}

// RegionsClient mirrors regions.* routes.
type RegionsClient struct{ c *Client }

// Status returns the generated HealthResponse without adapting it to the
// legacy []Region shape returned by List.
func (r *RegionsClient) Status(ctx context.Context, opts ...RequestOption) (*HealthResponse, error) {
	var out HealthResponse
	if err := r.c.do(ctx, "GET", "/api/v1/regions", nil, nil, &out, "regions", opts...); err != nil {
		return nil, err
	}
	if _, err := ParseRegionCode(string(out.Region)); err != nil {
		return nil, err
	}
	return &out, nil
}

// Health performs the generated health operation.
func (r *RegionsClient) Health(ctx context.Context, opts ...RequestOption) (*HealthResponse, error) {
	var out HealthResponse
	if err := r.c.do(ctx, "GET", "/api/v1/health", nil, nil, &out, "health", opts...); err != nil {
		return nil, err
	}
	if _, err := ParseRegionCode(string(out.Region)); err != nil {
		return nil, err
	}
	return &out, nil
}

// List returns serving regions.
func (r *RegionsClient) List(ctx context.Context, opts ...RequestOption) ([]Region, error) {
	out, err := r.Status(ctx, opts...)
	if err != nil {
		return nil, err
	}
	return []Region{{Code: out.Region, Available: out.Healthy, Healthy: out.Healthy}}, nil
}

// Get returns one region.
func (r *RegionsClient) Get(ctx context.Context, code RegionCode, opts ...RequestOption) (*Region, error) {
	parsed, err := ParseRegionCode(string(code))
	if err != nil {
		return nil, err
	}
	_ = parsed
	return nil, contractGap(ctx, "regions.get", "GET", "/api/v1/regions/{code}",
		"TerraBase publishes member region status only; it does not publish a per-region route",
		"use Regions.List and inspect the returned member status")
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
	_, _, _ = projectID, skip, take
	return nil, contractGap(ctx, "savedQueries.list", "GET", "/api/v1/projects/{projectId}/saved-queries",
		"the TerraBase public contract does not publish saved-query routes", "execute SQL through the published database route")
}

// Create saves a query.
func (s *SavedQueriesClient) Create(ctx context.Context, projectID, name, query string, opts ...RequestOption) (*SavedQuery, error) {
	_, _, _ = projectID, name, query
	return nil, contractGap(ctx, "savedQueries.create", "POST", "/api/v1/projects/{projectId}/saved-queries",
		"the TerraBase public contract does not publish saved-query routes", "execute SQL through the published database route")
}

// Get fetches a saved query.
func (s *SavedQueriesClient) Get(ctx context.Context, projectID, queryID string, opts ...RequestOption) (*SavedQuery, error) {
	_, _ = projectID, queryID
	return nil, contractGap(ctx, "savedQueries.get", "GET", "/api/v1/projects/{projectId}/saved-queries/{queryId}",
		"the TerraBase public contract does not publish saved-query routes", "execute SQL through the published database route")
}

// Update replaces a saved query.
func (s *SavedQueriesClient) Update(ctx context.Context, projectID, queryID, name, query, revision string, opts ...RequestOption) (*SavedQuery, error) {
	_, _, _, _, _ = projectID, queryID, name, query, revision
	return nil, contractGap(ctx, "savedQueries.update", "PUT", "/api/v1/projects/{projectId}/saved-queries/{queryId}",
		"the TerraBase public contract does not publish saved-query routes", "execute SQL through the published database route")
}

// Delete removes a saved query.
func (s *SavedQueriesClient) Delete(ctx context.Context, projectID, queryID string, opts ...RequestOption) error {
	_, _ = projectID, queryID
	return contractGap(ctx, "savedQueries.delete", "DELETE", "/api/v1/projects/{projectId}/saved-queries/{queryId}",
		"the TerraBase public contract does not publish saved-query routes", "execute SQL through the published database route")
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
	_, _, _ = projectID, skip, take
	return nil, contractGap(ctx, "databases.list", "GET", "/api/v1/projects/{projectId}/databases",
		"the TerraBase public contract publishes databases by name, not project-management routes",
		"use Databases.ListDatabases")
}

// Create creates a database.
func (d *DatabasesClient) Create(ctx context.Context, projectID, name string, opts ...RequestOption) (*ManagedDatabase, error) {
	_, _ = projectID, name
	return nil, contractGap(ctx, "databases.create", "POST", "/api/v1/projects/{projectId}/databases",
		"the TerraBase public contract publishes databases by name, not project-management routes",
		"use CreateDatabase with explicit placement")
}

// Get fetches a database.
func (d *DatabasesClient) Get(ctx context.Context, projectID, databaseID string, opts ...RequestOption) (*ManagedDatabase, error) {
	_, _ = projectID, databaseID
	return nil, contractGap(ctx, "databases.get", "GET", "/api/v1/projects/{projectId}/databases/{databaseId}",
		"the TerraBase public contract publishes databases by name, not project-management routes",
		"use GetDatabase")
}

// Delete removes a database; confirmation carries the delete identity header.
func (d *DatabasesClient) Delete(ctx context.Context, projectID, databaseID, confirmation string, opts ...RequestOption) error {
	_, _, _ = projectID, databaseID, confirmation
	return contractGap(ctx, "databases.delete", "DELETE", "/api/v1/projects/{projectId}/databases/{databaseId}",
		"the TerraBase public contract publishes databases by name, not project-management routes",
		"use DeleteDatabase with explicit confirmation")
}

// ListDatabases lists direct TerraBase database records, including
// soft-deleted records. The published route returns the complete bounded
// response and does not define offset query parameters.
func (d *DatabasesClient) ListDatabases(ctx context.Context, page PageRequest, opts ...RequestOption) (*DatabasePage, error) {
	if err := managementContext(ctx); err != nil {
		return nil, err
	}
	if err := validatePage(page); err != nil {
		return nil, err
	}
	var out DatabasePage
	if err := d.c.do(ctx, "GET", "/api/v1/databases", nil, nil, &out, "list_databases", opts...); err != nil {
		return nil, err
	}
	return &out, nil
}

// CreateDatabase submits a database with immutable placement. A 202 result
// contains a pending operation; only a 201 result contains a created record.
func (d *DatabasesClient) CreateDatabase(ctx context.Context, request CreateDatabaseRequest, opts ...RequestOption) (*CreateDatabaseResult, error) {
	if err := managementContext(ctx); err != nil {
		return nil, err
	}
	if err := validateDatabaseName(request.Name); err != nil {
		return nil, err
	}
	if strings.TrimSpace(request.OperationID) == "" || len(request.OperationID) > 128 {
		return nil, &ValidationError{Field: "operation_id", Message: "must be nonblank and at most 128 bytes"}
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
	if request.Residency != "" {
		request.Residency, _ = normalizePlacementLock(request.Residency)
	}
	var out CreateDatabaseResult
	if err := d.c.do(ctx, "POST", "/api/v1/databases", nil, request, &out, "create_database", opts...); err != nil {
		return nil, err
	}
	if out.Operation != nil && (out.Operation.OperationID != request.OperationID || out.Operation.Name != request.Name) {
		return nil, fmt.Errorf("sdk: provisioning response identity does not match the request")
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
func (d *DatabasesClient) DeleteDatabase(ctx context.Context, name string, opts ...RequestOption) (*DeletionOperation, error) {
	if err := managementContext(ctx); err != nil {
		return nil, err
	}
	path, err := databasePath(name)
	if err != nil {
		return nil, err
	}
	if err := requiredManagementMutation(opts); err != nil {
		return nil, err
	}
	if err := deleteConfirmation(opts, name); err != nil {
		return nil, err
	}
	var out DeletionOperation
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
	if err := d.c.do(ctx, "GET", path+"/collections", nil, nil, &out, "list_collections", opts...); err != nil {
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
	if err := d.c.do(ctx, "GET", path+"/indexes", nil, nil, &out, "list_indexes", opts...); err != nil {
		return nil, err
	}
	return &out, nil
}

// CreateIndex registers one extracted field. Registering an identical field
// is idempotent server-side, while the request itself remains replay-safe.
func (d *DatabasesClient) CreateIndex(ctx context.Context, database, collection string, request CreateIndexRequest, opts ...RequestOption) (*IndexCreatedResponse, error) {
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
	var out IndexCreatedResponse
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

// IndexRecommendations returns observed candidates, bounded to at most 64.
func (d *DatabasesClient) IndexRecommendations(ctx context.Context, database, collection string, limit int, opts ...RequestOption) (*IndexRecommendations, error) {
	if err := managementContext(ctx); err != nil {
		return nil, err
	}
	if limit < 0 || limit > 64 {
		return nil, &ValidationError{Field: "limit", Message: "must be between 1 and 64, or zero for the server default"}
	}
	path, err := collectionPath(database, collection)
	if err != nil {
		return nil, err
	}
	query := url.Values{}
	if limit > 0 {
		query.Set("limit", fmt.Sprint(limit))
	}
	var out IndexRecommendations
	if err := d.c.do(ctx, "GET", path+"/indexes/recommendations", query, nil, &out, "index_recommendations", opts...); err != nil {
		return nil, err
	}
	return &out, nil
}

// GetCollectionSchema fetches the declared schema for one collection.
func (d *DatabasesClient) GetCollectionSchema(ctx context.Context, database, collection string, opts ...RequestOption) (*SchemaDocument, error) {
	if err := managementContext(ctx); err != nil {
		return nil, err
	}
	path, err := collectionPath(database, collection)
	if err != nil {
		return nil, err
	}
	var out SchemaDocument
	if err := d.c.do(ctx, "GET", path+"/schema", nil, nil, &out, "get_schema", opts...); err != nil {
		return nil, err
	}
	return &out, nil
}

// PutCollectionSchema replaces a collection schema.
func (d *DatabasesClient) PutCollectionSchema(ctx context.Context, database, collection string, schema CollectionSchema, opts ...RequestOption) (*SchemaDocument, error) {
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
	if err := requiredManagementMutation(opts); err != nil {
		return nil, err
	}
	var out SchemaDocument
	if err := d.c.do(ctx, "PUT", path+"/schema", nil, schema, &out, "put_schema", opts...); err != nil {
		return nil, err
	}
	return &out, nil
}

// DatabaseBranchesClient mirrors databaseBranches.* routes.
type DatabaseBranchesClient struct{ c *Client }

// GetCapability returns branch capability for a project.
func (b *DatabaseBranchesClient) GetCapability(ctx context.Context, projectID string, opts ...RequestOption) (map[string]any, error) {
	_ = projectID
	return nil, contractGap(ctx, "databaseBranches.getCapability", "GET", "/api/v1/projects/{projectId}/database-branches/capability",
		"the TerraBase public contract does not publish project branch routes", "use ListBranches and CreateBranch")
}

// RequestClone requests a branch clone of a database.
func (b *DatabaseBranchesClient) RequestClone(ctx context.Context, projectID, sourceDatabaseID string, opts ...RequestOption) error {
	_, _ = projectID, sourceDatabaseID
	return contractGap(ctx, "databaseBranches.requestClone", "POST", "/api/v1/projects/{projectId}/database-branches",
		"the TerraBase public contract does not publish project branch routes", "use CreateBranch")
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
	if err := b.c.do(ctx, "GET", path+"/branches", nil, nil, &out, "list_branches", opts...); err != nil {
		return nil, err
	}
	return &out, nil
}

// CreateBranch creates a branch from the source database's retained backup
// stream. The target is always a new database and inherits the source's
// placement lock on the server.
func (b *DatabaseBranchesClient) CreateBranch(ctx context.Context, database string, request CreateBranchRequest, opts ...RequestOption) (*ProvisioningOperation, error) {
	if err := managementContext(ctx); err != nil {
		return nil, err
	}
	if err := validateDatabaseName(request.Name); err != nil {
		return nil, err
	}
	path, err := databasePath(database)
	if err != nil {
		return nil, err
	}
	if err := managementMutation(opts); err != nil {
		return nil, err
	}
	request.OperationID = strings.TrimSpace(request.OperationID)
	if request.OperationID == "" {
		request.OperationID = resolveRequestOptions(opts).idempotencyKey
	}
	if request.OperationID == "" {
		return nil, &ValidationError{Field: "operation_id", Message: "must be nonblank or supplied by the idempotency key"}
	}
	var response acceptedProvisioningOperation
	if err := b.c.do(ctx, "POST", path+"/branches", nil, request, &response, "create_branch", opts...); err != nil {
		return nil, err
	}
	if response.OperationID != request.OperationID {
		return nil, fmt.Errorf("sdk: branch operation response identity does not match the request")
	}
	return &response.ProvisioningOperation, nil
}

// BackupsClient mirrors backups.* routes.
type BackupsClient struct{ c *Client }

// ListBackups pages backup jobs.
func (b *BackupsClient) ListBackups(ctx context.Context, projectID, databaseID string, skip, take int, opts ...RequestOption) ([]BackupJob, error) {
	_, _, _ = projectID, databaseID, skip
	_ = take
	return nil, contractGap(ctx, "backups.list", "GET", "/api/v1/projects/{projectId}/databases/{databaseId}/backups",
		"the TerraBase public contract exposes backup readiness by database name only", "use ListDatabaseBackups")
}

// GetBackup fetches one backup job.
func (b *BackupsClient) GetBackup(ctx context.Context, projectID, databaseID, backupID string, opts ...RequestOption) (*BackupJob, error) {
	_, _, _ = projectID, databaseID, backupID
	return nil, contractGap(ctx, "backups.get", "GET", "/api/v1/projects/{projectId}/databases/{databaseId}/backups/{backupId}",
		"the TerraBase public contract exposes backup readiness by database name only", "use ListDatabaseBackups")
}

// StartRestore starts a restore job.
func (b *BackupsClient) StartRestore(ctx context.Context, projectID, databaseID, backupID, idempotencyKey string, opts ...RequestOption) (*BackupJob, error) {
	_, _, _, _ = projectID, databaseID, backupID, idempotencyKey
	return nil, contractGap(ctx, "backups.restores.start", "POST", "/api/v1/projects/{projectId}/databases/{databaseId}/restores",
		"the TerraBase public contract exposes restore-to-new-database by database name only", "use RestoreBackup")
}

// ListRestorePoints returns restore points.
func (b *BackupsClient) ListRestorePoints(ctx context.Context, projectID, databaseID string, opts ...RequestOption) ([]map[string]any, error) {
	_, _ = projectID, databaseID
	return nil, contractGap(ctx, "backups.restorePoints.list", "GET", "/api/v1/projects/{projectId}/databases/{databaseId}/restore-points",
		"the TerraBase public contract does not publish project restore-point routes", "use RestoreBackup with an optional Unix-millisecond at value")
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
func (b *BackupsClient) RestoreBackup(ctx context.Context, database string, request RestoreBackupRequest, opts ...RequestOption) (*ProvisioningOperation, error) {
	if err := managementContext(ctx); err != nil {
		return nil, err
	}
	if err := validateDatabaseName(request.Name); err != nil {
		return nil, err
	}
	if request.At != nil && *request.At < 0 {
		return nil, &ValidationError{Field: "at", Message: "must be a non-negative Unix-millisecond timestamp"}
	}
	path, err := databasePath(database)
	if err != nil {
		return nil, err
	}
	if err := managementMutation(opts); err != nil {
		return nil, err
	}
	request.OperationID = strings.TrimSpace(request.OperationID)
	if request.OperationID == "" {
		request.OperationID = strings.TrimSpace(resolveRequestOptions(opts).idempotencyKey)
	}
	var response acceptedProvisioningOperation
	if request.OperationID == "" {
		return nil, &ValidationError{Field: "operation_id", Message: "must be nonblank or supplied by the idempotency key"}
	}
	if err := b.c.do(ctx, "POST", path+"/backups/restore", nil, request, &response, "restore_backup", opts...); err != nil {
		return nil, err
	}
	if response.OperationID != request.OperationID {
		return nil, fmt.Errorf("sdk: restore operation response identity does not match the request")
	}
	return &response.ProvisioningOperation, nil
}

// AccessClient mirrors access.* team-member routes.
type AccessClient struct{ c *Client }

// ListTeamMembers pages project team members.
func (a *AccessClient) ListTeamMembers(ctx context.Context, projectID string, skip, take int, opts ...RequestOption) ([]TeamMember, error) {
	_, _, _ = projectID, skip, take
	return nil, contractGap(ctx, "access.teamMembers.list", "GET", "/api/v1/projects/{projectId}/access/team-members",
		"the TerraBase public contract does not publish project access routes", "use the dashboard control plane")
}

// AddTeamMember adds a member.
func (a *AccessClient) AddTeamMember(ctx context.Context, projectID, userID, role string, opts ...RequestOption) (*TeamMember, error) {
	_, _, _ = projectID, userID, role
	return nil, contractGap(ctx, "access.teamMembers.add", "POST", "/api/v1/projects/{projectId}/access/team-members",
		"the TerraBase public contract does not publish project access routes", "use the dashboard control plane")
}

// RemoveTeamMember removes a member.
func (a *AccessClient) RemoveTeamMember(ctx context.Context, projectID, userID string, opts ...RequestOption) error {
	_, _ = projectID, userID
	return contractGap(ctx, "access.teamMembers.remove", "DELETE", "/api/v1/projects/{projectId}/access/team-members/{userId}",
		"the TerraBase public contract does not publish project access routes", "use the dashboard control plane")
}

// ListAPIKeys lists project API-key metadata using cursor pagination. The
// route's cursor is opaque and is therefore not decoded or synthesized by
// the SDK.
func (a *AccessClient) ListAPIKeys(ctx context.Context, projectID, cursor string, take int, opts ...RequestOption) (*APIKeyPage, error) {
	_, _, _ = projectID, cursor, take
	return nil, contractGap(ctx, "apiKeys.list", "GET", "/api/v1/projects/{projectId}/auth/api-keys",
		"the TerraBase public contract does not publish project API-key routes", "use a key issued by the control plane")
}

// CreateAPIKey creates a project API key. The secret is returned once.
func (a *AccessClient) CreateAPIKey(ctx context.Context, projectID string, request CreateAPIKeyRequest, opts ...RequestOption) (*CreatedAPIKey, error) {
	_ = projectID
	return nil, contractGap(ctx, "apiKeys.create", "POST", "/api/v1/projects/{projectId}/auth/api-keys",
		"the TerraBase public contract does not publish project API-key routes", "use a key issued by the control plane")
}

// RotateAPIKey rotates a project API key. The replacement secret is returned
// once and the old key is invalidated by the service.
func (a *AccessClient) RotateAPIKey(ctx context.Context, projectID, keyID string, opts ...RequestOption) (*RotatedAPIKey, error) {
	_, _ = projectID, keyID
	return nil, contractGap(ctx, "apiKeys.rotate", "POST", "/api/v1/projects/{projectId}/auth/api-keys/{keyId}/rotate",
		"the TerraBase public contract does not publish project API-key routes", "use a key issued by the control plane")
}

// RevokeAPIKey revokes a project API key.
func (a *AccessClient) RevokeAPIKey(ctx context.Context, projectID, keyID string, opts ...RequestOption) (*RevokedAPIKey, error) {
	_, _ = projectID, keyID
	return nil, contractGap(ctx, "apiKeys.revoke", "DELETE", "/api/v1/projects/{projectId}/auth/api-keys/{keyId}",
		"the TerraBase public contract does not publish project API-key routes", "use a key issued by the control plane")
}

// UsageClient mirrors usage.* routes.
type UsageClient struct{ c *Client }

// GetProjectUsage returns project usage.
func (u *UsageClient) GetProjectUsage(ctx context.Context, projectID string, opts ...RequestOption) (*UsageSummary, error) {
	_ = projectID
	return nil, contractGap(ctx, "usage.project.get", "GET", "/api/v1/projects/{projectId}/usage",
		"the TerraBase public contract does not publish project usage routes", "retry after usage export is published")
}

// GetProjectUsageWorkload returns bounded workload buckets for a project.
func (u *UsageClient) GetProjectUsageWorkload(ctx context.Context, projectID, period string, opts ...RequestOption) (*UsageWorkload, error) {
	_, _ = projectID, period
	return nil, contractGap(ctx, "usage.project.workload", "GET", "/api/v1/projects/{projectId}/usage/workload",
		"the TerraBase public contract does not publish project usage routes", "retry after usage export is published")
}

// GetProjectUsageHistory returns retained usage history for a project.
func (u *UsageClient) GetProjectUsageHistory(ctx context.Context, projectID, period string, opts ...RequestOption) (*UsageHistory, error) {
	_, _ = projectID, period
	return nil, contractGap(ctx, "usage.project.history", "GET", "/api/v1/projects/{projectId}/usage/history",
		"the TerraBase public contract does not publish project usage routes", "retry after usage export is published")
}

// GetOrganizationUsage returns organization usage.
func (u *UsageClient) GetOrganizationUsage(ctx context.Context, organizationID string, opts ...RequestOption) (map[string]any, error) {
	_ = organizationID
	return nil, contractGap(ctx, "usage.organization.get", "GET", "/api/v1/organizations/{organizationId}/usage",
		"the TerraBase public contract does not publish organization usage routes", "retry after usage export is published")
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
	_, _, _ = projectID, skip, take
	return nil, contractGap(ctx, "audit.list", "GET", "/api/v1/projects/{projectId}/audit",
		"the TerraBase public contract does not publish project audit routes", "use the control-plane audit surface")
}

// Get fetches one audit event.
func (a *AuditClient) Get(ctx context.Context, projectID, eventID string, opts ...RequestOption) (*AuditEvent, error) {
	_, _ = projectID, eventID
	return nil, contractGap(ctx, "audit.get", "GET", "/api/v1/projects/{projectId}/audit/{eventId}",
		"the TerraBase public contract does not publish project audit routes", "use the control-plane audit surface")
}

// NotificationsClient mirrors notifications.* routes.
type NotificationsClient struct{ c *Client }

// List pages notifications.
func (n *NotificationsClient) List(ctx context.Context, opts ...RequestOption) ([]Notification, error) {
	return nil, contractGap(ctx, "notifications.list", "GET", "/api/v1/users/me/notifications",
		"the TerraBase public contract does not publish notification routes", "use the dashboard control plane")
}

// MarkRead marks one notification read.
func (n *NotificationsClient) MarkRead(ctx context.Context, notificationID string, opts ...RequestOption) (*Notification, error) {
	_ = notificationID
	return nil, contractGap(ctx, "notifications.markRead", "POST", "/api/v1/users/me/notifications/{notificationId}:mark-read",
		"the TerraBase public contract does not publish notification routes", "use the dashboard control plane")
}
