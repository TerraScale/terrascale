// Package tsgo constructs TerraScale SDK clients for the dashboard's live
// TerraBase calls. Authorization of the dashboard principal happens in each
// feature before a client is requested.
package tsgo

import (
	"net/http"

	"terrascale.tech/sdk/v1"
)

// Factory holds the HTTP client every TerraBase client shares. Callers
// address the project in every path, and TerraBase authorizes it against
// the key or the user's token.
type Factory struct {
	HTTPClient *http.Client
}

func (f Factory) Documents(api, key string) (*v1.Client, error) {
	return v1.New(v1.Config{BaseURL: api, Token: key, HTTPClient: f.HTTPClient})
}

func (f Factory) Management(api string) (*v1.Management, error) {
	return v1.NewManagement(v1.Config{BaseURL: api, HTTPClient: f.HTTPClient})
}
