// Tenants: database-per-tenant is a first-class pattern (GOAL D-51).
// Tenant = database; placement and key template inherit from a tenant
// template (GOAL D-51). TerraBase has not published tenant routes yet, so the
// client retains the typed surface but fails closed with ContractGapError.
package sdk

import (
	"context"
	"encoding/json"
	"strings"
)

// Tenant is a per-tenant database.
type Tenant struct {
	ID            string        `json:"id,omitempty"`
	TenantID      string        `json:"tenantId,omitempty"`
	Key           string        `json:"key"`
	DatabaseID    string        `json:"databaseId"`
	Template      string        `json:"template,omitempty"`
	Regions       []RegionCode  `json:"regions,omitempty"`
	PlacementLock PlacementLock `json:"placementLock,omitempty"`
	CreatedAt     string        `json:"createdAt,omitempty"`
}

// UnmarshalJSON accepts both the public camelCase spelling and the Rust
// draft's snake_case spelling while the published contract is pending.
func (t *Tenant) UnmarshalJSON(data []byte) error {
	var wire struct {
		ID               string        `json:"id"`
		TenantID         string        `json:"tenantId"`
		TenantIDAlt      string        `json:"tenant_id"`
		Key              string        `json:"key"`
		DatabaseID       string        `json:"databaseId"`
		DatabaseIDAlt    string        `json:"database_id"`
		Template         string        `json:"template"`
		Regions          []RegionCode  `json:"regions"`
		PlacementLock    PlacementLock `json:"placementLock"`
		PlacementLockAlt PlacementLock `json:"placement_lock"`
		CreatedAt        string        `json:"createdAt"`
		CreatedAtAlt     string        `json:"created_at"`
	}
	if err := json.Unmarshal(data, &wire); err != nil {
		return err
	}
	if wire.DatabaseID == "" {
		wire.DatabaseID = wire.DatabaseIDAlt
	}
	if wire.TenantID == "" {
		wire.TenantID = wire.TenantIDAlt
	}
	if wire.PlacementLock == "" {
		wire.PlacementLock = wire.PlacementLockAlt
	}
	if wire.CreatedAt == "" {
		wire.CreatedAt = wire.CreatedAtAlt
	}
	*t = Tenant{
		ID: wire.ID, TenantID: wire.TenantID, Key: wire.Key, DatabaseID: wire.DatabaseID,
		Template: wire.Template, Regions: wire.Regions, PlacementLock: wire.PlacementLock, CreatedAt: wire.CreatedAt,
	}
	return nil
}

// TenantTemplate describes the planned template whose placement and key
// policy a tenant inherits once the TerraBase tenant contract is published.
type TenantTemplate struct {
	Name    string       `json:"name"`
	Regions []RegionCode `json:"regions,omitempty"`
}

// CreateTenantRequest is the planned tenant-create request. The route is not
// published, so Create returns ContractGapError without sending this body.
type CreateTenantRequest struct {
	Key           string        `json:"key"`
	Template      string        `json:"template,omitempty"`
	Regions       []RegionCode  `json:"regions,omitempty"`
	PlacementLock PlacementLock `json:"placementLock,omitempty"`
}

// TenantKey is the planned tenant-scoped API key response.
type TenantKey struct {
	KeyID     string `json:"keyId"`
	Token     string `json:"token,omitempty"`
	TenantKey string `json:"tenantKey,omitempty"`
}

// TenantApiKey is the descriptive generated-model spelling.
type TenantApiKey = TenantKey

// CreateTenantKeyRequest carries optional tenant-key metadata.
type CreateTenantKeyRequest struct {
	Name   string   `json:"name,omitempty"`
	Scopes []string `json:"scopes,omitempty"`
}

// CreateTenantBranchRequest carries optional support-branch metadata.
type CreateTenantBranchRequest struct {
	Name   string `json:"name,omitempty"`
	Reason string `json:"reason,omitempty"`
}

// UnmarshalJSON accepts both the public camelCase spelling and the Rust
// draft's snake_case spelling while the published contract is pending.
func (k *TenantKey) UnmarshalJSON(data []byte) error {
	var wire struct {
		KeyID        string `json:"keyId"`
		KeyIDAlt     string `json:"key_id"`
		Token        string `json:"token"`
		TenantKey    string `json:"tenantKey"`
		TenantKeyAlt string `json:"tenant_key"`
	}
	if err := json.Unmarshal(data, &wire); err != nil {
		return err
	}
	if wire.KeyID == "" {
		wire.KeyID = wire.KeyIDAlt
	}
	if wire.TenantKey == "" {
		wire.TenantKey = wire.TenantKeyAlt
	}
	*k = TenantKey{KeyID: wire.KeyID, Token: wire.Token, TenantKey: wire.TenantKey}
	return nil
}

// TenantBranch is a support/debugging branch of one tenant database.
type TenantBranch struct {
	BranchID  string `json:"branchId"`
	TenantKey string `json:"tenantKey"`
	Status    string `json:"status,omitempty"`
}

// TenantOperationOptions is retained as a named place for future tenant
// route options; regular RequestOption values remain the Go SDK surface.
type TenantOperationOptions struct {
	IdempotencyKey string
}

// UnmarshalJSON accepts both the public camelCase spelling and the Rust
// draft's snake_case spelling while the published contract is pending.
func (b *TenantBranch) UnmarshalJSON(data []byte) error {
	var wire struct {
		BranchID     string `json:"branchId"`
		BranchIDAlt  string `json:"branch_id"`
		TenantKey    string `json:"tenantKey"`
		TenantKeyAlt string `json:"tenant_key"`
		Status       string `json:"status"`
	}
	if err := json.Unmarshal(data, &wire); err != nil {
		return err
	}
	if wire.BranchID == "" {
		wire.BranchID = wire.BranchIDAlt
	}
	if wire.TenantKey == "" {
		wire.TenantKey = wire.TenantKeyAlt
	}
	*b = TenantBranch{BranchID: wire.BranchID, TenantKey: wire.TenantKey, Status: wire.Status}
	return nil
}

// TenantPage is one page returned by the tenant list route.
type TenantPage struct {
	Tenants    []Tenant `json:"tenants"`
	NextCursor string   `json:"nextCursor,omitempty"`
}

// UnmarshalJSON accepts the tenant envelope and the Rust draft's bare array.
func (p *TenantPage) UnmarshalJSON(data []byte) error {
	trimmed := strings.TrimSpace(string(data))
	if strings.HasPrefix(trimmed, "[") {
		var tenants []Tenant
		if err := json.Unmarshal(data, &tenants); err != nil {
			return err
		}
		p.Tenants = tenants
		p.NextCursor = ""
		return nil
	}
	var wire struct {
		Tenants       []Tenant `json:"tenants"`
		Items         []Tenant `json:"items"`
		NextCursor    string   `json:"nextCursor"`
		NextCursorAlt string   `json:"next_cursor"`
	}
	if err := json.Unmarshal(data, &wire); err != nil {
		return err
	}
	if len(wire.Tenants) == 0 {
		wire.Tenants = wire.Items
	}
	if wire.NextCursor == "" {
		wire.NextCursor = wire.NextCursorAlt
	}
	p.Tenants = wire.Tenants
	p.NextCursor = wire.NextCursor
	return nil
}

// TenantsClient manages tenant databases. Reach it via Client.Tenants.
type TenantsClient struct {
	c *Client
}

// Create provisions a tenant database.
func (t *TenantsClient) Create(ctx context.Context, req CreateTenantRequest, opts ...RequestOption) (*Tenant, error) {
	if err := managementContext(ctx); err != nil {
		return nil, err
	}
	if err := validateCreateTenantRequest(&req); err != nil {
		return nil, err
	}
	if err := managementMutation(opts); err != nil {
		return nil, err
	}
	return nil, contractGap(ctx, "tenants.create", "POST", "/api/v1/tenants",
		"the TerraBase public contract does not publish tenant routes",
		"create a database with Databases.CreateDatabase; tenant provisioning is pending D-51/E-8")
}

// Get fetches a tenant by key.
func (t *TenantsClient) Get(ctx context.Context, key string, opts ...RequestOption) (*Tenant, error) {
	if err := managementContext(ctx); err != nil {
		return nil, err
	}
	if err := validateTenantKey(key); err != nil {
		return nil, err
	}
	return nil, contractGap(ctx, "tenants.get", "GET", "/api/v1/tenants/{key}",
		"the TerraBase public contract does not publish tenant routes",
		"address the database directly with GetDatabase")
}

// Delete removes a tenant database.
func (t *TenantsClient) Delete(ctx context.Context, key string, opts ...RequestOption) error {
	if err := managementContext(ctx); err != nil {
		return err
	}
	if err := validateTenantKey(key); err != nil {
		return err
	}
	if err := requiredManagementMutation(opts); err != nil {
		return err
	}
	if err := deleteConfirmation(opts, key); err != nil {
		return err
	}
	return contractGap(ctx, "tenants.delete", "DELETE", "/api/v1/tenants/{key}",
		"the TerraBase public contract does not publish tenant routes",
		"address the tenant database directly with DeleteDatabase")
}

// List pages tenants.
func (t *TenantsClient) List(ctx context.Context, limit int, cursor string, opts ...RequestOption) ([]Tenant, error) {
	page, err := t.ListPage(ctx, limit, cursor, opts...)
	if err != nil {
		return nil, err
	}
	return page.Tenants, nil
}

// ListPage returns one page of tenants and its continuation cursor.
func (t *TenantsClient) ListPage(ctx context.Context, limit int, cursor string, opts ...RequestOption) (*TenantPage, error) {
	if err := managementContext(ctx); err != nil {
		return nil, err
	}
	if limit < 0 {
		return nil, &PaginationError{Field: "limit", Value: limit}
	}
	if limit > 1000 {
		return nil, &PaginationError{Field: "limit", Value: limit}
	}
	if strings.TrimSpace(cursor) == "" && cursor != "" {
		return nil, &ValidationError{Field: "cursor", Message: "cannot be blank"}
	}
	return nil, contractGap(ctx, "tenants.list", "GET", "/api/v1/tenants",
		"the TerraBase public contract does not publish tenant routes",
		"list databases with ListDatabases")
}

// CreateKey mints a tenant-scoped API key from the template's key policy.
func (t *TenantsClient) CreateKey(ctx context.Context, key string, opts ...RequestOption) (*TenantKey, error) {
	if err := managementContext(ctx); err != nil {
		return nil, err
	}
	if err := validateTenantKey(key); err != nil {
		return nil, err
	}
	if err := managementMutation(opts); err != nil {
		return nil, err
	}
	return nil, contractGap(ctx, "tenants.keys.create", "POST", "/api/v1/tenants/{key}/keys",
		"the TerraBase public contract does not publish tenant routes",
		"use the control-plane key-management surface")
}

// CreateKeyWithRequest validates optional key metadata before returning the
// same actionable gap for the unpublished tenant producer operation.
func (t *TenantsClient) CreateKeyWithRequest(ctx context.Context, key string, request CreateTenantKeyRequest, opts ...RequestOption) (*TenantKey, error) {
	if err := managementContext(ctx); err != nil {
		return nil, err
	}
	if err := validateTenantKey(key); err != nil {
		return nil, err
	}
	if err := validateOptionalTenantMetadata(request.Name, "name"); err != nil {
		return nil, err
	}
	if err := validateTenantScopes(request.Scopes); err != nil {
		return nil, err
	}
	if err := managementMutation(opts); err != nil {
		return nil, err
	}
	return nil, contractGap(ctx, "tenants.keys.create", "POST", "/api/v1/tenants/{key}/keys",
		"the TerraBase public contract does not publish tenant routes",
		"use the control-plane key-management surface")
}

// MintKey is retained as an alias for CreateKey.
func (t *TenantsClient) MintKey(ctx context.Context, key string, opts ...RequestOption) (*TenantKey, error) {
	return t.CreateKey(ctx, key, opts...)
}

// CreateBranch opens a per-tenant support branch.
func (t *TenantsClient) CreateBranch(ctx context.Context, key string, opts ...RequestOption) (*TenantBranch, error) {
	if err := managementContext(ctx); err != nil {
		return nil, err
	}
	if err := validateTenantKey(key); err != nil {
		return nil, err
	}
	if err := managementMutation(opts); err != nil {
		return nil, err
	}
	return nil, contractGap(ctx, "tenants.branches.create", "POST", "/api/v1/tenants/{key}/branches",
		"the TerraBase public contract does not publish tenant routes",
		"create a database branch with DatabaseBranches.CreateBranch")
}

// CreateBranchWithRequest validates optional branch metadata before returning
// the unpublished tenant-operation gap.
func (t *TenantsClient) CreateBranchWithRequest(ctx context.Context, key string, request CreateTenantBranchRequest, opts ...RequestOption) (*TenantBranch, error) {
	if err := managementContext(ctx); err != nil {
		return nil, err
	}
	if err := validateTenantKey(key); err != nil {
		return nil, err
	}
	if err := validateOptionalTenantMetadata(request.Name, "name"); err != nil {
		return nil, err
	}
	if err := validateOptionalTenantMetadata(request.Reason, "reason"); err != nil {
		return nil, err
	}
	if err := managementMutation(opts); err != nil {
		return nil, err
	}
	return nil, contractGap(ctx, "tenants.branches.create", "POST", "/api/v1/tenants/{key}/branches",
		"the TerraBase public contract does not publish tenant routes",
		"create a database branch with DatabaseBranches.CreateBranch")
}

func validateTenantKey(key string) error {
	if strings.TrimSpace(key) == "" {
		return &ValidationError{Field: "tenant key", Message: "cannot be empty"}
	}
	if len([]byte(key)) > 64 {
		return &ValidationError{Field: "tenant key", Message: "must be at most 64 bytes"}
	}
	for index, char := range []byte(key) {
		alpha := (char >= 'a' && char <= 'z') || (char >= 'A' && char <= 'Z')
		digit := char >= '0' && char <= '9'
		if !(alpha || digit || char == '.' || char == '_' || char == ':' || char == '-') {
			return &ValidationError{Field: "tenant key", Message: "must use letters, digits, '.', '_', ':', or '-'"}
		}
		if index == 0 && !(alpha || digit) {
			return &ValidationError{Field: "tenant key", Message: "must start with a letter or digit"}
		}
	}
	return nil
}

func validateCreateTenantRequest(req *CreateTenantRequest) error {
	if err := validateTenantKey(req.Key); err != nil {
		return err
	}
	if len(req.Regions) == 0 {
		if req.PlacementLock != "" {
			_, err := normalizePlacementLock(req.PlacementLock)
			return err
		}
		return nil
	}
	regions := make([]RegionCode, len(req.Regions))
	copy(regions, req.Regions)
	if err := validatePlacement(regions, ""); err != nil {
		return err
	}
	for i := range regions {
		regions[i], _ = ParseRegionCode(string(regions[i]))
	}
	if len(regions) > 0 {
		req.Regions = regions
	}
	return validatePlacement(regions, req.PlacementLock)
}

func validateOptionalTenantMetadata(value, field string) error {
	if strings.TrimSpace(value) == "" {
		return nil
	}
	if len([]byte(value)) > 128 {
		return &ValidationError{Field: field, Message: "must be at most 128 bytes"}
	}
	if strings.ContainsAny(value, "\r\n") {
		return &ValidationError{Field: field, Message: "must not contain control characters"}
	}
	return nil
}

func validateTenantScopes(scopes []string) error {
	for _, scope := range scopes {
		if err := validateOptionalTenantMetadata(scope, "scope"); err != nil {
			return err
		}
	}
	return nil
}
