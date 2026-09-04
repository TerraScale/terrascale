// Tenants: database-per-tenant is a first-class pattern (GOAL D-51).
// Tenant = database; placement and key template inherit from a tenant
// template. Paths are a hand-written stub against /api/v1 pending the
// published terrabase openapi.json.
package sdk

import (
	"context"
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
	Regions    []RegionCode `json:"regions,omitempty"`
}

// CreateTenantRequest creates a tenant database from a template.
type CreateTenantRequest struct {
	Key      string       `json:"key"`
	Template string       `json:"template,omitempty"`
	Regions  []RegionCode `json:"regions,omitempty"`
}

// TenantKey is a tenant-scoped API key minted from the template.
type TenantKey struct {
	KeyID string `json:"keyId"`
	Token string `json:"token"`
}

// TenantsClient manages tenant databases. Reach it via Client.Tenants.
type TenantsClient struct {
	c *Client
}

// Create provisions a tenant database.
func (t *TenantsClient) Create(ctx context.Context, req CreateTenantRequest, opts ...RequestOption) (*Tenant, error) {
	if strings.TrimSpace(req.Key) == "" {
		return nil, fmt.Errorf("sdk: tenant key cannot be empty")
	}
	var out Tenant
	if err := t.c.do(ctx, "POST", "/api/v1/tenants", nil, req, &out, "tenants.create", opts...); err != nil {
		return nil, err
	}
	return &out, nil
}

// Get fetches a tenant by key.
func (t *TenantsClient) Get(ctx context.Context, key string, opts ...RequestOption) (*Tenant, error) {
	if strings.TrimSpace(key) == "" {
		return nil, fmt.Errorf("sdk: tenant key cannot be empty")
	}
	var out Tenant
	if err := t.c.do(ctx, "GET", "/api/v1/tenants/"+url.PathEscape(key), nil, nil, &out, "tenants.get", opts...); err != nil {
		return nil, err
	}
	return &out, nil
}

// Delete removes a tenant database.
func (t *TenantsClient) Delete(ctx context.Context, key string, opts ...RequestOption) error {
	if strings.TrimSpace(key) == "" {
		return fmt.Errorf("sdk: tenant key cannot be empty")
	}
	return t.c.do(ctx, "DELETE", "/api/v1/tenants/"+url.PathEscape(key), nil, nil, nil, "tenants.delete", opts...)
}

// List pages tenants.
func (t *TenantsClient) List(ctx context.Context, limit int, cursor string, opts ...RequestOption) ([]Tenant, error) {
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
	var out struct {
		Tenants []Tenant `json:"tenants"`
	}
	if err := t.c.do(ctx, "GET", "/api/v1/tenants", query, nil, &out, "tenants.list", opts...); err != nil {
		return nil, err
	}
	return out.Tenants, nil
}

// MintKey mints a tenant-scoped API key from the template.
func (t *TenantsClient) MintKey(ctx context.Context, key string, opts ...RequestOption) (*TenantKey, error) {
	if strings.TrimSpace(key) == "" {
		return nil, fmt.Errorf("sdk: tenant key cannot be empty")
	}
	var out TenantKey
	if err := t.c.do(ctx, "POST", "/api/v1/tenants/"+url.PathEscape(key)+"/keys", nil, map[string]any{}, &out, "tenants.keys.mint", opts...); err != nil {
		return nil, err
	}
	return &out, nil
}
