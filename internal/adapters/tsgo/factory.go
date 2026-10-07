// Package tsgo constructs TerraScale SDK clients for the dashboard's live
// Documents calls. Authorization of the dashboard principal happens in each
// feature before a client is requested.
package tsgo

import (
	"errors"
	"net/http"
	"strings"

	"terrascale.tech/sdk/v1"
)

// Factory holds the public API origin and a server credential. Callers
// address the project in every Documents path (Client.Database), and
// TerraBase authorizes it against the credential.
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
