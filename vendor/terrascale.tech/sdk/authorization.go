// Authorization lease route (operationId authorization_lease).
package sdk

import (
	"context"
	"net/url"
)

// Lease is a previously authenticated producer lease returned with its
// original absolute expiry. The route never fetches identity state or renews
// a lease on a reader's behalf.
type Lease struct {
	Version      int32  `json:"version"`
	TenantID     string `json:"tenantId"`
	Epoch        string `json:"epoch"`
	FeatureEpoch string `json:"featureEpoch"`
	Audience     string `json:"audience"`
	Region       string `json:"region"`
	IssuedAt     int64  `json:"issuedAt"`
	ValidUntil   int64  `json:"validUntil"`
	KeyID        string `json:"keyId"`
	Signature    string `json:"signature"`
}

// GetAuthorizationLease returns the producer lease previously authenticated
// for one tenant, with its original absolute expiry in epoch milliseconds.
func (c *Client) GetAuthorizationLease(ctx context.Context, tenant string, opts ...RequestOption) (*Lease, error) {
	if err := managementContext(ctx); err != nil {
		return nil, err
	}
	if err := requireID("tenant", tenant); err != nil {
		return nil, err
	}
	var out Lease
	if err := c.do(ctx, "GET", "/api/v1/authorization/leases/"+url.PathEscape(tenant), nil, nil, &out, "authorization_lease", opts...); err != nil {
		return nil, err
	}
	return &out, nil
}
