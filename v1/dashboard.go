package v1

import (
	"context"
	"encoding/json"
	"fmt"
	"net/http"
	"net/url"
	"strconv"
	"strings"
	"time"
)

// Dashboard is the /v1/dashboard control-plane client for a control key.
//
// Every mutation takes an explicit idempotencyKey: the server requires one,
// and the caller must reuse the same key when retrying the same mutation so a
// retry returns the original response. NewIdempotencyKey generates one.
type Dashboard struct{ c *Client }

// NewDashboard returns a control-plane client; cfg.Token is a control key.
func NewDashboard(cfg Config) (*Dashboard, error) {
	c, err := New(cfg)
	if err != nil {
		return nil, err
	}
	return &Dashboard{c: c}, nil
}

// Page is one page of a dashboard list.
type Page[T any] struct {
	Items      []T     `json:"items"`
	NextCursor *string `json:"next_cursor"`
}

// ListOptions paginates dashboard lists. Limit is 1-200 (zero: server default 50).
type ListOptions struct {
	Limit  int
	Cursor string
}

func (o ListOptions) values() url.Values {
	v := url.Values{}
	if o.Limit != 0 {
		v.Set("limit", strconv.Itoa(o.Limit))
	}
	if o.Cursor != "" {
		v.Set("cursor", o.Cursor)
	}
	return v
}

func dash(segments ...string) []string { return append([]string{"v1", "dashboard"}, segments...) }

func (d *Dashboard) get(ctx context.Context, path []string, query url.Values, out any) error {
	if err := checkSegments(path); err != nil {
		return err
	}
	_, err := d.c.call(ctx, request{method: http.MethodGet, path: path, query: query, replaySafe: true}, nil, out)
	return err
}

func (d *Dashboard) mutate(ctx context.Context, method, idempotencyKey string, path []string, in, out any) error {
	if err := validIdempotencyKey(idempotencyKey); err != nil {
		return fmt.Errorf("%w (control-plane mutations require a key; see NewIdempotencyKey)", err)
	}
	if err := checkSegments(path); err != nil {
		return err
	}
	_, err := d.c.call(ctx, request{method: method, path: path, idemKey: idempotencyKey}, in, out)
	return err
}

func list[T any](ctx context.Context, d *Dashboard, path []string, query url.Values) (*Page[T], error) {
	return dget[Page[T]](ctx, d, path, query)
}

func dget[T any](ctx context.Context, d *Dashboard, path []string, query url.Values) (*T, error) {
	var out T
	if err := d.get(ctx, path, query, &out); err != nil {
		return nil, err
	}
	return &out, nil
}

func dmut[T any](ctx context.Context, d *Dashboard, method, idempotencyKey string, path []string, in any) (*T, error) {
	var out T
	if err := d.mutate(ctx, method, idempotencyKey, path, in, &out); err != nil {
		return nil, err
	}
	return &out, nil
}

// checkSegments rejects path parameters that would change the route.
func checkSegments(path []string) error {
	for _, seg := range path[2:] {
		if seg == "" || seg == "." || seg == ".." || strings.ContainsAny(seg, "/?#") {
			return fmt.Errorf("terrabase: invalid path parameter %q", seg)
		}
	}
	return nil
}

// Tenants.

// TenantLimits are a tenant's quotas.
type TenantLimits struct {
	MaxDatabases     int    `json:"max_databases"`
	MaxStorageBytes  Uint64 `json:"max_storage_bytes"`
	MaxRatePerSecond int    `json:"max_rate_per_second"`
}

// Tenant is one TerraBase tenant (one dashboard project).
type Tenant struct {
	ID              string       `json:"id"`
	Name            string       `json:"name"`
	ExternalRef     string       `json:"external_ref"`
	Status          string       `json:"status"` // active, suspended or deleting
	Limits          TenantLimits `json:"limits"`
	AdvisorExternal bool         `json:"advisor_external"`
	CreatedAt       Timestamp    `json:"created_at"`
}

// CreateTenantRequest is the body of CreateTenant.
type CreateTenantRequest struct {
	Name            string        `json:"name"`
	ExternalRef     string        `json:"external_ref"`
	Limits          *TenantLimits `json:"limits,omitempty"`
	AdvisorExternal *bool         `json:"advisor_external,omitempty"`
}

// UpdateTenantRequest is the body of UpdateTenant; nil members are unchanged.
type UpdateTenantRequest struct {
	Name            *string       `json:"name,omitempty"`
	Status          *string       `json:"status,omitempty"` // active or suspended
	Limits          *TenantLimits `json:"limits,omitempty"`
	AdvisorExternal *bool         `json:"advisor_external,omitempty"`
}

// Usage is a tenant's usage in the current UTC calendar month.
type Usage struct {
	Databases    int       `json:"databases"`
	Documents    Uint64    `json:"documents"`
	StorageBytes Uint64    `json:"storage_bytes"`
	Reads        Uint64    `json:"reads"`
	Writes       Uint64    `json:"writes"`
	PeriodStart  Timestamp `json:"period_start"`
}

// CreateTenant creates a tenant; ExternalRef is unique.
func (d *Dashboard) CreateTenant(ctx context.Context, idempotencyKey string, req CreateTenantRequest) (*Tenant, error) {
	return dmut[Tenant](ctx, d, http.MethodPost, idempotencyKey, dash("tenants"), req)
}

// ListTenants lists tenants, optionally filtered by external reference.
func (d *Dashboard) ListTenants(ctx context.Context, externalRef string, opts ListOptions) (*Page[Tenant], error) {
	q := opts.values()
	if externalRef != "" {
		q.Set("external_ref", externalRef)
	}
	return list[Tenant](ctx, d, dash("tenants"), q)
}

// GetTenant reads one tenant.
func (d *Dashboard) GetTenant(ctx context.Context, tenant string) (*Tenant, error) {
	return dget[Tenant](ctx, d, dash("tenants", tenant), nil)
}

// UpdateTenant changes a tenant's name, status, limits or advisor setting.
func (d *Dashboard) UpdateTenant(ctx context.Context, idempotencyKey, tenant string, req UpdateTenantRequest) (*Tenant, error) {
	return dmut[Tenant](ctx, d, http.MethodPatch, idempotencyKey, dash("tenants", tenant), req)
}

// DeleteTenant starts deleting a tenant with its databases and keys.
func (d *Dashboard) DeleteTenant(ctx context.Context, idempotencyKey, tenant string) (*Operation, error) {
	return dmut[Operation](ctx, d, http.MethodDelete, idempotencyKey, dash("tenants", tenant), nil)
}

// Usage reads a tenant's usage for the current month.
func (d *Dashboard) Usage(ctx context.Context, tenant string) (*Usage, error) {
	return dget[Usage](ctx, d, dash("tenants", tenant, "usage"), nil)
}

// Databases.

// Retention bounds a database's shape log.
type Retention struct {
	ShapeLogBytes      Uint64 `json:"shape_log_bytes"`
	ShapeLogAgeSeconds Uint64 `json:"shape_log_age_seconds"`
}

// DatabaseInfo is a database record.
type DatabaseInfo struct {
	ID         string     `json:"id"`
	TenantID   string     `json:"tenant_id"`
	Name       string     `json:"name"`
	HomeRegion string     `json:"home_region"`
	Status     string     `json:"status"` // ready, deleting or failed
	Durability Durability `json:"durability"`
	Retention  Retention  `json:"retention"`
	CreatedAt  Timestamp  `json:"created_at"`
}

// CreateDatabaseRequest is the body of CreateDatabase.
type CreateDatabaseRequest struct {
	Name       string     `json:"name"`
	HomeRegion string     `json:"home_region,omitempty"`
	Durability Durability `json:"durability,omitempty"`
	Retention  *Retention `json:"retention,omitempty"`
}

// CreateDatabase creates a database synchronously.
func (d *Dashboard) CreateDatabase(ctx context.Context, idempotencyKey, tenant string, req CreateDatabaseRequest) (*DatabaseInfo, error) {
	return dmut[DatabaseInfo](ctx, d, http.MethodPost, idempotencyKey, dash("tenants", tenant, "databases"), req)
}

// ListDatabases lists a tenant's databases.
func (d *Dashboard) ListDatabases(ctx context.Context, tenant string, opts ListOptions) (*Page[DatabaseInfo], error) {
	return list[DatabaseInfo](ctx, d, dash("tenants", tenant, "databases"), opts.values())
}

// GetDatabase reads a database by name.
func (d *Dashboard) GetDatabase(ctx context.Context, tenant, database string) (*DatabaseInfo, error) {
	return dget[DatabaseInfo](ctx, d, dash("tenants", tenant, "databases", database), nil)
}

// UpdateDatabase changes a database's retention.
func (d *Dashboard) UpdateDatabase(ctx context.Context, idempotencyKey, tenant, database string, retention Retention) (*DatabaseInfo, error) {
	body := struct {
		Retention Retention `json:"retention"`
	}{retention}
	return dmut[DatabaseInfo](ctx, d, http.MethodPatch, idempotencyKey, dash("tenants", tenant, "databases", database), body)
}

// DeleteDatabase starts deleting a database.
func (d *Dashboard) DeleteDatabase(ctx context.Context, idempotencyKey, tenant, database string) (*Operation, error) {
	return dmut[Operation](ctx, d, http.MethodDelete, idempotencyKey, dash("tenants", tenant, "databases", database), nil)
}

// Operations.

// Operation is an asynchronous control-plane operation.
type Operation struct {
	ID       string `json:"id"`
	Kind     string `json:"kind"`
	Status   string `json:"status"` // running, succeeded or failed
	Resource string `json:"resource"`
	// Error is set when Status is failed.
	Error      *OperationFailure `json:"error"`
	CreatedAt  Timestamp         `json:"created_at"`
	FinishedAt *Timestamp        `json:"finished_at"`
}

// OperationFailure describes why an operation failed.
type OperationFailure struct {
	Code    ErrorCode `json:"code"`
	Message string    `json:"message"`
}

// Operation status values.
const (
	OperationRunning   = "running"
	OperationSucceeded = "succeeded"
	OperationFailed    = "failed"
)

// OperationError reports an operation that finished with status failed.
// errors.Is matches its failure code.
type OperationError struct{ Operation *Operation }

func (e *OperationError) Error() string {
	msg := "unknown failure"
	if f := e.Operation.Error; f != nil {
		msg = string(f.Code) + ": " + f.Message
	}
	return fmt.Sprintf("terrabase: operation %s (%s) failed: %s", e.Operation.ID, e.Operation.Kind, msg)
}

// Is reports whether target is the operation's failure code.
func (e *OperationError) Is(target error) bool {
	code, ok := target.(ErrorCode)
	return ok && e.Operation.Error != nil && e.Operation.Error.Code == code
}

// GetOperation reads an operation.
func (d *Dashboard) GetOperation(ctx context.Context, id string) (*Operation, error) {
	return dget[Operation](ctx, d, dash("operations", id), nil)
}

// WaitOperation polls an operation every interval (default 1s) until it
// finishes. A failed operation returns it with an *OperationError.
func (d *Dashboard) WaitOperation(ctx context.Context, id string, interval time.Duration) (*Operation, error) {
	if interval <= 0 {
		interval = time.Second
	}
	for {
		op, err := d.GetOperation(ctx, id)
		if err != nil {
			return nil, err
		}
		switch op.Status {
		case OperationSucceeded:
			return op, nil
		case OperationFailed:
			return op, &OperationError{Operation: op}
		}
		if err := sleep(ctx, interval); err != nil {
			return op, err
		}
	}
}

// Collections.

// UniqueConstraint is a collection unique constraint.
type UniqueConstraint struct {
	Name      string   `json:"name"`
	Paths     []string `json:"paths"`
	Transform string   `json:"transform,omitempty"` // "lower"
}

// Schema is an enforced field list; a nil *Schema means schemaless.
type Schema struct {
	Fields []Field `json:"fields"`
}

// Field is a schema field. Type is null, bool, i64, utf8, bytes, map or array.
type Field struct {
	Name     string   `json:"name"`
	Type     string   `json:"type"`
	Required bool     `json:"required"`
	Nullable bool     `json:"nullable"`
	Fields   []Field  `json:"fields,omitempty"`  // for map
	Element  *Element `json:"element,omitempty"` // for array
}

// Element is an array field's element type (no nested arrays).
type Element struct {
	Type     string  `json:"type"`
	Nullable bool    `json:"nullable"`
	Fields   []Field `json:"fields,omitempty"`
}

// CollectionInfo is a collection record.
type CollectionInfo struct {
	ID         string             `json:"id"`
	Name       string             `json:"name"`
	Generation Uint64             `json:"generation"`
	Schema     *Schema            `json:"schema"`
	TTLPath    *string            `json:"ttl_path"`
	Unique     []UniqueConstraint `json:"unique"`
	Advisor    string             `json:"advisor"` // auto or manual
	CreatedAt  Timestamp          `json:"created_at"`
}

// IndexKey is one index key.
type IndexKey struct {
	Path      string `json:"path"`
	Order     string `json:"order"`               // asc or desc
	Transform string `json:"transform,omitempty"` // "lower"
}

// IndexSpec declares an index.
type IndexSpec struct {
	Name string     `json:"name"`
	Keys []IndexKey `json:"keys"`
}

// CreateCollectionRequest is the body of CreateCollection.
type CreateCollectionRequest struct {
	Name    string             `json:"name"`
	Schema  *Schema            `json:"schema,omitempty"`
	TTLPath string             `json:"ttl_path,omitempty"`
	Unique  []UniqueConstraint `json:"unique,omitempty"`
	Indexes []IndexSpec        `json:"indexes,omitempty"`
	Advisor string             `json:"advisor,omitempty"`
}

// UpdateCollectionRequest is the body of UpdateCollection; nil and zero
// members are unchanged. TTLPath is nullable: Null[string]() removes the TTL.
type UpdateCollectionRequest struct {
	Schema  *Schema             `json:"schema,omitempty"`
	TTLPath Optional[string]    `json:"ttl_path,omitzero"`
	Unique  *[]UniqueConstraint `json:"unique,omitempty"`
	Advisor *string             `json:"advisor,omitempty"`
}

// PathStats are sampled statistics for one path.
type PathStats struct {
	Path            string             `json:"path"`
	Types           map[string]float64 `json:"types"`
	MissingFraction float64            `json:"missing_fraction"`
	DistinctEst     Uint64             `json:"distinct_estimate"`
	ArrayFraction   float64            `json:"array_fraction"`
	MeanValueBytes  float64            `json:"mean_value_bytes"`
}

// CollectionStats are bounded sample statistics for a collection.
type CollectionStats struct {
	DocumentCountEstimate Uint64      `json:"document_count_estimate"`
	BytesEstimate         Uint64      `json:"bytes_estimate"`
	SampledDocuments      Uint64      `json:"sampled_documents"`
	SampledAt             Timestamp   `json:"sampled_at"`
	Paths                 []PathStats `json:"paths"`
}

func collPath(tenant, database string, rest ...string) []string {
	return dash(append([]string{"tenants", tenant, "databases", database, "collections"}, rest...)...)
}

// CreateCollection creates a collection; declared indexes are pinned.
func (d *Dashboard) CreateCollection(ctx context.Context, idempotencyKey, tenant, database string, req CreateCollectionRequest) (*CollectionInfo, error) {
	return dmut[CollectionInfo](ctx, d, http.MethodPost, idempotencyKey, collPath(tenant, database), req)
}

// ListCollections lists a database's collections.
func (d *Dashboard) ListCollections(ctx context.Context, tenant, database string, opts ListOptions) (*Page[CollectionInfo], error) {
	return list[CollectionInfo](ctx, d, collPath(tenant, database), opts.values())
}

// GetCollection reads a collection by name.
func (d *Dashboard) GetCollection(ctx context.Context, tenant, database, collection string) (*CollectionInfo, error) {
	return dget[CollectionInfo](ctx, d, collPath(tenant, database, collection), nil)
}

// UpdateCollection changes schema, TTL path, unique constraints or advisor mode.
func (d *Dashboard) UpdateCollection(ctx context.Context, idempotencyKey, tenant, database, collection string, req UpdateCollectionRequest) (*CollectionInfo, error) {
	return dmut[CollectionInfo](ctx, d, http.MethodPatch, idempotencyKey, collPath(tenant, database, collection), req)
}

// DeleteCollection starts deleting a collection.
func (d *Dashboard) DeleteCollection(ctx context.Context, idempotencyKey, tenant, database, collection string) (*Operation, error) {
	return dmut[Operation](ctx, d, http.MethodDelete, idempotencyKey, collPath(tenant, database, collection), nil)
}

// CollectionStats reads sampled statistics.
func (d *Dashboard) CollectionStats(ctx context.Context, tenant, database, collection string) (*CollectionStats, error) {
	return dget[CollectionStats](ctx, d, collPath(tenant, database, collection, "stats"), nil)
}

// Indexes.

// IndexProgress reports a building index.
type IndexProgress struct {
	Scanned       Uint64 `json:"scanned"`
	TotalEstimate Uint64 `json:"total_estimate"`
}

// Index is an index record.
type Index struct {
	ID         string         `json:"id"`
	Name       string         `json:"name"`
	Keys       []IndexKey     `json:"keys"`
	Unique     bool           `json:"unique"`
	Source     string         `json:"source"` // manual, constraint, ttl, initial_decision or workload
	Pinned     bool           `json:"pinned"`
	State      string         `json:"state"` // proposed, backfilling, validating, active, retiring or dropped
	Multikey   bool           `json:"multikey"`
	Progress   *IndexProgress `json:"progress"`
	Bytes      Uint64         `json:"bytes"`
	CreatedAt  Timestamp      `json:"created_at"`
	LastUsedAt *Timestamp     `json:"last_used_at"`
}

// ListIndexes lists a collection's indexes.
func (d *Dashboard) ListIndexes(ctx context.Context, tenant, database, collection string, opts ListOptions) (*Page[Index], error) {
	return list[Index](ctx, d, collPath(tenant, database, collection, "indexes"), opts.values())
}

// CreateIndex creates a pinned manual index; it starts in backfilling.
func (d *Dashboard) CreateIndex(ctx context.Context, idempotencyKey, tenant, database, collection string, spec IndexSpec) (*Index, error) {
	return dmut[Index](ctx, d, http.MethodPost, idempotencyKey, collPath(tenant, database, collection, "indexes"), spec)
}

// GetIndex reads an index by name.
func (d *Dashboard) GetIndex(ctx context.Context, tenant, database, collection, index string) (*Index, error) {
	return dget[Index](ctx, d, collPath(tenant, database, collection, "indexes", index), nil)
}

// PinIndex sets whether an index is pinned (never retired by the advisor).
func (d *Dashboard) PinIndex(ctx context.Context, idempotencyKey, tenant, database, collection, index string, pinned bool) (*Index, error) {
	body := struct {
		Pinned bool `json:"pinned"`
	}{pinned}
	return dmut[Index](ctx, d, http.MethodPatch, idempotencyKey, collPath(tenant, database, collection, "indexes", index), body)
}

// DeleteIndex drops an index and returns it in state dropped or retiring;
// constraint and TTL indexes fail with ErrIndexProtected.
func (d *Dashboard) DeleteIndex(ctx context.Context, idempotencyKey, tenant, database, collection, index string) (*Index, error) {
	return dmut[Index](ctx, d, http.MethodDelete, idempotencyKey, collPath(tenant, database, collection, "indexes", index), nil)
}

// IndexDecision is a collection generation's initial index decision.
type IndexDecision struct {
	Collection string      `json:"collection"`
	Generation Uint64      `json:"generation"`
	Status     string      `json:"status"` // pending or decided
	Decider    Decider     `json:"decider"`
	Candidates []Candidate `json:"candidates"`
	// FallbackReason is nil or one of not_opted_in, timeout, unavailable,
	// invalid_answer, abstained and resting.
	FallbackReason *string    `json:"fallback_reason"`
	RequestDigest  string     `json:"request_digest"`
	DecidedAt      *Timestamp `json:"decided_at"`
}

// Decider is the decider that answered an index decision.
type Decider struct {
	Name  string        `json:"name"`
	Kind  string        `json:"kind"` // systemone, llm or deterministic
	Model string        `json:"model"`
	Usage *DeciderUsage `json:"usage"`
}

// DeciderUsage is a model decider's token usage.
type DeciderUsage struct {
	InputTokens  int `json:"input_tokens"`
	OutputTokens int `json:"output_tokens"`
}

// Candidate is one candidate index of a decision.
type Candidate struct {
	ID       string     `json:"id"`
	Keys     []IndexKey `json:"keys"`
	Score    float64    `json:"score"`
	Selected bool       `json:"selected"`
}

// IndexDecisions lists a collection's index decision records.
func (d *Dashboard) IndexDecisions(ctx context.Context, tenant, database, collection string, opts ListOptions) (*Page[IndexDecision], error) {
	return list[IndexDecision](ctx, d, collPath(tenant, database, collection, "index-decisions"), opts.values())
}

// Keys.

// Grant is a data key's access grant. Database and Collections accept "*".
type Grant struct {
	Database    string   `json:"database"`
	Collections []string `json:"collections"`
	Permissions []string `json:"permissions"` // read, write, shape
}

// KeyRate is a key's rate limit.
type KeyRate struct {
	PerSecond   int `json:"per_second"`
	Burst       int `json:"burst"`
	MaxInFlight int `json:"max_in_flight"`
}

// Key is an API key record. The secret is never returned after creation.
type Key struct {
	ID       string  `json:"id"`
	TenantID string  `json:"tenant_id,omitempty"`
	Name     string  `json:"name"`
	Role     string  `json:"role"` // data, control or edge
	Grants   []Grant `json:"grants,omitempty"`
	// RowFilter is the mandatory row filter (query-v1 grammar), raw.
	RowFilter json.RawMessage `json:"row_filter,omitempty"`
	Rate      *KeyRate        `json:"rate,omitempty"`
	ExpiresAt *Timestamp      `json:"expires_at"`
	RevokedAt *Timestamp      `json:"revoked_at"`
	CreatedAt Timestamp       `json:"created_at"`
	LastFour  string          `json:"last_four"`
}

// KeyWithToken is a created or rotated key with its one-time token.
type KeyWithToken struct {
	Key   Key    `json:"key"`
	Token string `json:"token"`
}

// CreateKeyRequest is the body of CreateKey (role data).
type CreateKeyRequest struct {
	Name       string   `json:"name"`
	Grants     []Grant  `json:"grants"`
	RowFilter  *Filter  `json:"row_filter,omitempty"`
	Rate       *KeyRate `json:"rate,omitempty"`
	TTLSeconds int      `json:"ttl_seconds,omitempty"`
}

// CreateKey creates a data key; the token is returned only once.
func (d *Dashboard) CreateKey(ctx context.Context, idempotencyKey, tenant string, req CreateKeyRequest) (*KeyWithToken, error) {
	return dmut[KeyWithToken](ctx, d, http.MethodPost, idempotencyKey, dash("tenants", tenant, "keys"), req)
}

// ListKeys lists a tenant's keys.
func (d *Dashboard) ListKeys(ctx context.Context, tenant string, opts ListOptions) (*Page[Key], error) {
	return list[Key](ctx, d, dash("tenants", tenant, "keys"), opts.values())
}

// GetKey reads a key.
func (d *Dashboard) GetKey(ctx context.Context, tenant, key string) (*Key, error) {
	return dget[Key](ctx, d, dash("tenants", tenant, "keys", key), nil)
}

// RevokeKey revokes a key immediately and returns the revoked record.
func (d *Dashboard) RevokeKey(ctx context.Context, idempotencyKey, tenant, key string) (*Key, error) {
	return dmut[Key](ctx, d, http.MethodDelete, idempotencyKey, dash("tenants", tenant, "keys", key), nil)
}

// RotateKey issues a replacement key; the old key expires after grace
// (0-86400 seconds; nil uses the server default).
func (d *Dashboard) RotateKey(ctx context.Context, idempotencyKey, tenant, key string, graceSeconds *int) (*KeyWithToken, error) {
	body := struct {
		GraceSeconds *int `json:"grace_seconds,omitempty"`
	}{graceSeconds}
	return dmut[KeyWithToken](ctx, d, http.MethodPost, idempotencyKey, dash("tenants", tenant, "keys", key, "rotate"), body)
}

// CreateEdgeKey creates an edge-role key for the Cloudflare Worker.
func (d *Dashboard) CreateEdgeKey(ctx context.Context, idempotencyKey, name string) (*KeyWithToken, error) {
	body := struct {
		Name string `json:"name"`
	}{name}
	return dmut[KeyWithToken](ctx, d, http.MethodPost, idempotencyKey, dash("edge-keys"), body)
}

// ListEdgeKeys lists edge keys.
func (d *Dashboard) ListEdgeKeys(ctx context.Context, opts ListOptions) (*Page[Key], error) {
	return list[Key](ctx, d, dash("edge-keys"), opts.values())
}

// DeleteEdgeKey revokes an edge key and returns it with RevokedAt set.
func (d *Dashboard) DeleteEdgeKey(ctx context.Context, idempotencyKey, key string) (*Key, error) {
	return dmut[Key](ctx, d, http.MethodDelete, idempotencyKey, dash("edge-keys", key), nil)
}

// Advisors, regions and audit.

// Advisor is an index decider's health.
type Advisor struct {
	Name                string     `json:"name"`
	Kind                string     `json:"kind"`
	Model               string     `json:"model"`
	State               string     `json:"state"` // healthy or resting
	ConsecutiveFailures int        `json:"consecutive_failures"`
	RestUntil           *Timestamp `json:"rest_until"`
	LastLatencyMS       int        `json:"last_latency_ms"`
}

// Region is a TerraBase region.
type Region struct {
	Code   string `json:"code"`
	Name   string `json:"name"`
	Status string `json:"status"`
}

// AuditEvent is one audit record.
type AuditEvent struct {
	ID             string    `json:"id"`
	At             Timestamp `json:"at"`
	ActorKeyID     string    `json:"actor_key_id"`
	Action         string    `json:"action"`
	Resource       string    `json:"resource"`
	IdempotencyKey string    `json:"idempotency_key"`
	Outcome        string    `json:"outcome"`
}

// ListAdvisors lists the configured index deciders.
func (d *Dashboard) ListAdvisors(ctx context.Context, opts ListOptions) (*Page[Advisor], error) {
	return list[Advisor](ctx, d, dash("advisors"), opts.values())
}

// ListRegions lists regions.
func (d *Dashboard) ListRegions(ctx context.Context, opts ListOptions) (*Page[Region], error) {
	return list[Region](ctx, d, dash("regions"), opts.values())
}

// ListAudit lists audit events newest first; an empty tenant lists all tenants.
func (d *Dashboard) ListAudit(ctx context.Context, tenant string, opts ListOptions) (*Page[AuditEvent], error) {
	q := opts.values()
	if tenant != "" {
		q.Set("tenant", tenant)
	}
	return list[AuditEvent](ctx, d, dash("audit"), q)
}
