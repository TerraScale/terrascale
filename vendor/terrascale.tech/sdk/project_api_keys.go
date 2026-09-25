package sdk

import (
	"context"
	"net/http"
	"net/url"
	"strconv"
	"strings"
)

// ProjectAPIKey is the redacted representation returned by the Dashboard.
// The secret is returned only by Create and Rotate.
type ProjectAPIKey struct {
	ID         string   `json:"keyId"`
	ProjectID  string   `json:"projectId"`
	Name       string   `json:"name"`
	Prefix     string   `json:"prefix"`
	LastFour   string   `json:"lastFour"`
	Scopes     []string `json:"scopes"`
	DatabaseID string   `json:"databaseId"`
	CreatedBy  string   `json:"createdByEmail"`
	CreatedAt  string   `json:"createdAt"`
	ExpiresAt  string   `json:"expiresAt"`
	LastUsedAt string   `json:"lastUsedAt"`
	RevokedAt  string   `json:"revokedAt"`
	Active     bool     `json:"isActive"`
}

type ProjectAPIKeyPage struct {
	APIKeys    []ProjectAPIKey `json:"apiKeys"`
	NextCursor string          `json:"nextCursor"`
}

type CreateProjectAPIKeyRequest struct {
	Name       string   `json:"name"`
	Scopes     []string `json:"scopes"`
	DatabaseID string   `json:"databaseId,omitempty"`
	ExpiresAt  string   `json:"expiresAt,omitempty"`
}

type ProjectAPIKeySecret struct {
	APIKey ProjectAPIKey `json:"apiKey"`
	Secret string        `json:"secret"`
}

type ProjectAPIKeyRevocation struct {
	KeyID   string `json:"keyId"`
	Revoked bool   `json:"revoked"`
}

// ProjectAPIKeysClient calls the Dashboard's authenticated project key
// lifecycle routes. Construct a Client with APIBaseURL set to the Dashboard
// origin and AccessToken set to a Dashboard session token. The returned secret
// can then be used as AccessToken on a separate public API Client.
type ProjectAPIKeysClient struct{ c *Client }

func projectAPIKeysPath(projectID string) (string, error) {
	if err := requireID("project_id", projectID); err != nil {
		return "", err
	}
	return "/api/v1/projects/" + url.PathEscape(projectID) + "/auth/api-keys", nil
}

func (p *ProjectAPIKeysClient) List(ctx context.Context, projectID, cursor string, limit uint32, opts ...RequestOption) (*ProjectAPIKeyPage, error) {
	path, err := projectAPIKeysPath(projectID)
	if err != nil {
		return nil, err
	}
	query := url.Values{}
	if strings.TrimSpace(cursor) != "" {
		query.Set("cursor", cursor)
	}
	if limit > 0 {
		query.Set("limit", strconv.FormatUint(uint64(limit), 10))
	}
	var out ProjectAPIKeyPage
	if err := p.c.do(ctx, http.MethodGet, path, query, nil, &out, "project_api_keys.list", opts...); err != nil {
		return nil, err
	}
	return &out, nil
}

func (p *ProjectAPIKeysClient) Create(ctx context.Context, projectID string, request CreateProjectAPIKeyRequest, opts ...RequestOption) (*ProjectAPIKeySecret, error) {
	path, err := projectAPIKeysPath(projectID)
	if err != nil {
		return nil, err
	}
	if strings.TrimSpace(request.Name) == "" {
		return nil, &ValidationError{Field: "name", Message: "must be nonblank"}
	}
	if len(request.Scopes) == 0 {
		return nil, &ValidationError{Field: "scopes", Message: "must not be empty"}
	}
	if err := managementMutation(opts); err != nil {
		return nil, err
	}
	var out ProjectAPIKeySecret
	if err := p.c.do(ctx, http.MethodPost, path, nil, request, &out, "project_api_keys.create", opts...); err != nil {
		return nil, err
	}
	return &out, nil
}

func (p *ProjectAPIKeysClient) Rotate(ctx context.Context, projectID, keyID string, opts ...RequestOption) (*ProjectAPIKeySecret, error) {
	path, err := projectAPIKeysPath(projectID)
	if err != nil {
		return nil, err
	}
	if err := requireID("key_id", keyID); err != nil {
		return nil, err
	}
	if err := managementMutation(opts); err != nil {
		return nil, err
	}
	var out ProjectAPIKeySecret
	if err := p.c.do(ctx, http.MethodPost, path+"/"+url.PathEscape(keyID)+"/rotate", nil, nil, &out, "project_api_keys.rotate", opts...); err != nil {
		return nil, err
	}
	return &out, nil
}

func (p *ProjectAPIKeysClient) Revoke(ctx context.Context, projectID, keyID string, opts ...RequestOption) (*ProjectAPIKeyRevocation, error) {
	path, err := projectAPIKeysPath(projectID)
	if err != nil {
		return nil, err
	}
	if err := requireID("key_id", keyID); err != nil {
		return nil, err
	}
	if err := managementMutation(opts); err != nil {
		return nil, err
	}
	var out ProjectAPIKeyRevocation
	if err := p.c.do(ctx, http.MethodDelete, path+"/"+url.PathEscape(keyID), nil, nil, &out, "project_api_keys.revoke", opts...); err != nil {
		return nil, err
	}
	return &out, nil
}
