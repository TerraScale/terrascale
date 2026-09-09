// Management clients for the published TerraBase /api/v1 contract. The
// direct database, region-status, backup, branch, and index routes below are
// contract-backed and never send invented requests.
package sdk

import (
	"context"
	"encoding/json"
	"fmt"
	"io"
	"net/http"
	"net/url"
	"strings"
	"unicode/utf8"
)

// Region describes an admitted public region in the edge catalog.
type Region struct {
	Code      RegionCode `json:"code"`
	Name      string     `json:"name"`
	Country   string     `json:"country"`
	Latitude  float64    `json:"latitude"`
	Longitude float64    `json:"longitude"`
	Available bool       `json:"available"`
}

// RegionCatalogResponse is the edge discovery response.
type RegionCatalogResponse struct {
	Regions []Region          `json:"regions"`
	Hosts   map[string]string `json:"hosts"`
}

// UnmarshalJSON validates the edge catalog at its wire boundary. The edge
// contract requires every catalog field, including hosts, and does not allow
// unknown fields.
func (r *RegionCatalogResponse) UnmarshalJSON(data []byte) error {
	type regionWire struct {
		Code      *string  `json:"code"`
		Name      *string  `json:"name"`
		Country   *string  `json:"country"`
		Latitude  *float64 `json:"latitude"`
		Longitude *float64 `json:"longitude"`
		Available *bool    `json:"available"`
	}
	var wire struct {
		Regions []regionWire        `json:"regions"`
		Hosts   *map[string]*string `json:"hosts"`
	}
	decoder := json.NewDecoder(strings.NewReader(string(data)))
	decoder.DisallowUnknownFields()
	if err := decoder.Decode(&wire); err != nil {
		return err
	}
	var extra any
	if err := decoder.Decode(&extra); err != io.EOF {
		if err == nil {
			return fmt.Errorf("sdk: region catalog response contains trailing data")
		}
		return err
	}
	if wire.Hosts == nil || len(wire.Regions) == 0 {
		return fmt.Errorf("sdk: region catalog response must contain regions and hosts")
	}
	regions := make([]Region, len(wire.Regions))
	seen := make(map[RegionCode]struct{}, len(wire.Regions))
	for i, item := range wire.Regions {
		if item.Code == nil || item.Name == nil || item.Country == nil || item.Latitude == nil || item.Longitude == nil || item.Available == nil {
			return fmt.Errorf("sdk: region catalog entry %d is missing a required field", i)
		}
		code, err := parseCatalogRegionCode(*item.Code)
		if err != nil {
			return err
		}
		if _, ok := seen[code]; ok {
			return fmt.Errorf("sdk: region catalog contains duplicate region code %q", code)
		}
		seen[code] = struct{}{}
		if utf8.RuneCountInString(*item.Name) < 1 || utf8.RuneCountInString(*item.Name) > 128 {
			return fmt.Errorf("sdk: catalog region name must contain 1 to 128 characters, got %q", *item.Name)
		}
		if len(*item.Country) != 2 || (*item.Country)[0] < 'A' || (*item.Country)[0] > 'Z' || (*item.Country)[1] < 'A' || (*item.Country)[1] > 'Z' {
			return fmt.Errorf("sdk: catalog region country must be two uppercase letters, got %q", *item.Country)
		}
		if *item.Latitude < -90 || *item.Latitude > 90 || *item.Longitude < -180 || *item.Longitude > 180 {
			return fmt.Errorf("sdk: catalog region coordinates are out of range for %q", *item.Code)
		}
		regions[i] = Region{Code: code, Name: *item.Name, Country: *item.Country, Latitude: *item.Latitude, Longitude: *item.Longitude, Available: *item.Available}
	}
	hosts := make(map[string]string, len(*wire.Hosts))
	for code, host := range *wire.Hosts {
		if host == nil {
			return fmt.Errorf("sdk: region catalog host %q must be a string", code)
		}
		hosts[code] = *host
	}
	*r = RegionCatalogResponse{Regions: regions, Hosts: hosts}
	return nil
}

// HealthResponse is the generated response model for GET /api/v1/regions
// and GET /api/v1/health. It identifies the member that served the request.
type HealthResponse struct {
	Region  RegionCode `json:"region"`
	Healthy bool       `json:"healthy"`
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

// RegionsClient mirrors regions.* routes.
type RegionsClient struct{ c *Client }

// Status returns the member's serving region and health.
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

// List returns the admitted public region catalog.
func (r *RegionsClient) List(ctx context.Context, opts ...RequestOption) (*RegionCatalogResponse, error) {
	var out RegionCatalogResponse
	if err := r.c.do(ctx, "GET", "/api/v1/regions", nil, nil, &out, "regions.list", opts...); err != nil {
		return nil, err
	}
	return &out, nil
}

func parseCatalogRegionCode(value string) (RegionCode, error) {
	if len(value) < 1 || len(value) > 16 {
		return "", fmt.Errorf("sdk: catalog region code must contain 1 to 16 uppercase letters, digits, or hyphens, got %q", value)
	}
	for i, char := range value {
		if i == 0 && (char < 'A' || char > 'Z') && (char < '0' || char > '9') {
			return "", fmt.Errorf("sdk: catalog region code must contain 1 to 16 uppercase letters, digits, or hyphens, got %q", value)
		}
		if (char < 'A' || char > 'Z') && (char < '0' || char > '9') && char != '-' {
			return "", fmt.Errorf("sdk: catalog region code must contain 1 to 16 uppercase letters, digits, or hyphens, got %q", value)
		}
	}
	return RegionCode(value), nil
}

// DatabasesClient mirrors databases.* routes.
type DatabasesClient struct{ c *Client }

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

// DeleteDatabase admits a durable deletion; the response does not imply physical cleanup has completed.
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
	if err := validateDeletionOperationID(resolveRequestOptions(opts).idempotencyKey); err != nil {
		return nil, err
	}
	out := deletionResponse{status: http.StatusAccepted}
	if err := d.c.do(ctx, "DELETE", path, nil, nil, &out, "delete_database", opts...); err != nil {
		return nil, err
	}
	if err := d.validateDeletionScope(&out.DeletionOperation, name, strings.TrimSpace(resolveRequestOptions(opts).idempotencyKey)); err != nil {
		return nil, err
	}
	return &out.DeletionOperation, nil
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

// GetDatabaseUsage fetches usage for one direct TerraBase database.
type UsageClient struct{ c *Client }

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
