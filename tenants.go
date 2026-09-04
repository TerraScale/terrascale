// Tenants: database-per-tenant is a first-class pattern (GOAL D-51).
// Tenant = database; placement and key template inherit from a tenant
// template. The routes below follow the current TerraBase tenant draft. The
// published OpenAPI artifact does not include these routes yet; see
// contract.go for the contract-generation gap.
package sdk

import (
	"context"
	"encoding/json"
	"fmt"
	"net/url"
	"strconv"
	"strings"
)

// Tenant is a per-tenant database.
type Tenant struct {
	ID         string       `json:"id"`
	Key        string       `json:"key"`
	DatabaseID string       `json:"databaseId"`
	Template   string       `json:"template,omitempty"`
	Regions    []RegionCode `json:"regions,omitempty"`
	CreatedAt  string       `json:"createdAt,omitempty"`
}

// UnmarshalJSON accepts both the public camelCase spelling and the Rust
// draft's snake_case spelling while the published contract is pending.
func (t *Tenant) UnmarshalJSON(data []byte) error {
	var wire struct {
		ID            string       `json:"id"`
		Key           string       `json:"key"`
		DatabaseID    string       `json:"databaseId"`
		DatabaseIDAlt string       `json:"database_id"`
		Template      string       `json:"template"`
		Regions       []RegionCode `json:"regions"`
		CreatedAt     string       `json:"createdAt"`
		CreatedAtAlt  string       `json:"created_at"`
	}
	if err := json.Unmarshal(data, &wire); err != nil {
		return err
	}
	if wire.DatabaseID == "" {
		wire.DatabaseID = wire.DatabaseIDAlt
	}
	if wire.CreatedAt == "" {
		wire.CreatedAt = wire.CreatedAtAlt
	}
	*t = Tenant{
		ID: wire.ID, Key: wire.Key, DatabaseID: wire.DatabaseID,
		Template: wire.Template, Regions: wire.Regions, CreatedAt: wire.CreatedAt,
	}
	return nil
}

// TenantTemplate describes the template whose placement and key policy a
// tenant inherits.
type TenantTemplate struct {
	Name    string       `json:"name"`
	Regions []RegionCode `json:"regions,omitempty"`
}

// CreateTenantRequest creates a tenant database from a template.
type CreateTenantRequest struct {
	Key      string       `json:"key"`
	Template string       `json:"template,omitempty"`
	Regions  []RegionCode `json:"regions,omitempty"`
}

// TenantKey is a tenant-scoped API key minted from the template.
type TenantKey struct {
	KeyID     string `json:"keyId"`
	Token     string `json:"token,omitempty"`
	TenantKey string `json:"tenantKey,omitempty"`
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
	if err := validateCreateTenantRequest(&req); err != nil {
		return nil, err
	}
	var out Tenant
	if err := t.c.do(ctx, "POST", "/api/v1/tenants", nil, req, &out, "tenants.create", opts...); err != nil {
		return nil, err
	}
	return &out, nil
}

// Get fetches a tenant by key.
func (t *TenantsClient) Get(ctx context.Context, key string, opts ...RequestOption) (*Tenant, error) {
	if err := validateTenantKey(key); err != nil {
		return nil, err
	}
	var out Tenant
	if err := t.c.do(ctx, "GET", "/api/v1/tenants/"+url.PathEscape(key), nil, nil, &out, "tenants.get", opts...); err != nil {
		return nil, err
	}
	return &out, nil
}

// Delete removes a tenant database.
func (t *TenantsClient) Delete(ctx context.Context, key string, opts ...RequestOption) error {
	if err := validateTenantKey(key); err != nil {
		return err
	}
	// The draft contract requires the tenant key as the destructive-action
	// confirmation. A caller-supplied option may still override it, matching
	// the other delete operations in this package.
	opts = append([]RequestOption{WithDeleteConfirmation(key)}, opts...)
	return t.c.do(ctx, "DELETE", "/api/v1/tenants/"+url.PathEscape(key), nil, nil, nil, "tenants.delete", opts...)
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
	if limit < 0 {
		return nil, fmt.Errorf("sdk: limit cannot be negative")
	}
	query := url.Values{}
	if limit > 0 {
		query.Set("limit", strconv.Itoa(limit))
	}
	if cursor != "" {
		query.Set("cursor", cursor)
	}
	var out TenantPage
	if err := t.c.do(ctx, "GET", "/api/v1/tenants", query, nil, &out, "tenants.list", opts...); err != nil {
		return nil, err
	}
	return &out, nil
}

// CreateKey mints a tenant-scoped API key from the template's key policy.
func (t *TenantsClient) CreateKey(ctx context.Context, key string, opts ...RequestOption) (*TenantKey, error) {
	if err := validateTenantKey(key); err != nil {
		return nil, err
	}
	var out TenantKey
	if err := t.c.do(ctx, "POST", "/api/v1/tenants/"+url.PathEscape(key)+"/keys", nil, map[string]any{}, &out, "tenants.keys.create", opts...); err != nil {
		return nil, err
	}
	return &out, nil
}

// MintKey is retained as an alias for CreateKey.
func (t *TenantsClient) MintKey(ctx context.Context, key string, opts ...RequestOption) (*TenantKey, error) {
	return t.CreateKey(ctx, key, opts...)
}

// CreateBranch opens a per-tenant support branch.
func (t *TenantsClient) CreateBranch(ctx context.Context, key string, opts ...RequestOption) (*TenantBranch, error) {
	if err := validateTenantKey(key); err != nil {
		return nil, err
	}
	var out TenantBranch
	path := "/api/v1/tenants/" + url.PathEscape(key) + "/branches"
	if err := t.c.do(ctx, "POST", path, nil, map[string]any{}, &out, "tenants.branches.create", opts...); err != nil {
		return nil, err
	}
	return &out, nil
}

func validateTenantKey(key string) error {
	if strings.TrimSpace(key) == "" {
		return fmt.Errorf("sdk: tenant key cannot be empty")
	}
	return nil
}

func validateCreateTenantRequest(req *CreateTenantRequest) error {
	if err := validateTenantKey(req.Key); err != nil {
		return err
	}
	regions := make([]RegionCode, 0, len(req.Regions))
	for _, region := range req.Regions {
		parsed, err := ParseRegionCode(string(region))
		if err != nil {
			return err
		}
		for _, existing := range regions {
			if existing == parsed {
				return fmt.Errorf("sdk: tenant placement contains duplicate region %q", parsed)
			}
		}
		regions = append(regions, parsed)
	}
	if len(regions) > 0 {
		req.Regions = regions
	}
	return nil
}
