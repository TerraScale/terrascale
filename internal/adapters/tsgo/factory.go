// Package tsgo constructs scoped TerraScale SDK clients for the dashboard's
// live data-plane calls. Authorization of the dashboard principal happens in
// each feature before a client is requested.
package tsgo

import (
	"errors"
	"net/http"
	"strings"

	sdk "terrascale.tech/sdk"
	"terrascale.tech/sdk/v1"
)

// Factory holds the public API origin and a server credential. The public API
// must independently authorize the tenant and project headers against that
// credential; those headers are selectors, never an authorization grant.
type Factory struct {
	APIBaseURL  string
	AccessToken string
	HTTPClient  *http.Client
}

func (f Factory) ForDocuments(tenantID, projectID string) (*v1.Client, error) {
	if strings.TrimSpace(tenantID) == "" || strings.TrimSpace(projectID) == "" {
		return nil, errors.New("tsgo: organization and project scope are required")
	}
	return v1.New(v1.Config{BaseURL: f.APIBaseURL, Token: f.AccessToken, HTTPClient: f.HTTPClient})
}

// ForScope returns a new client so its causal session frontier cannot leak
// across projects or dashboard requests.
func (f Factory) ForScope(tenantID, projectID string) (*sdk.Client, error) {
	if strings.TrimSpace(f.APIBaseURL) == "" || strings.TrimSpace(f.AccessToken) == "" {
		return nil, errors.New("tsgo: public API origin and access token are required")
	}
	if strings.TrimSpace(tenantID) == "" || strings.TrimSpace(projectID) == "" {
		return nil, errors.New("tsgo: tenant and project scope are required")
	}
	return sdk.NewClient(sdk.Config{
		APIBaseURL:  f.APIBaseURL,
		AccessToken: f.AccessToken,
		TenantID:    tenantID,
		ProjectID:   projectID,
		HTTPClient:  f.HTTPClient,
	})
}
