package v1

import (
	"context"
	"encoding/base64"
	"encoding/json"
	"errors"
	"fmt"
	"net/http"
	"time"

	"terrascale.tech/sdk/internal/entityid"
	"terrascale.tech/sdk/tsauth"
)

type CreateDatabaseOptions struct {
	HomeRegion     string   `json:"homeRegion,omitempty"`
	ReplicaRegions []string `json:"replicaRegions,omitzero"`
	AllRegions     bool     `json:"allRegions,omitzero"`
	TTLSeconds     *int64   `json:"ttlSeconds,omitempty"`
}

type ForkDatabaseOptions struct {
	PointInTime *time.Time `json:"pointInTime,omitempty"`
	TTLSeconds  *int64     `json:"ttlSeconds,omitempty"`
}

type ScopedKeyAccess string

const (
	ScopedKeyRead      ScopedKeyAccess = "read"
	ScopedKeyReadWrite ScopedKeyAccess = "read_write"
)

type ScopedKeyRequest struct {
	Project    string          `json:"project,omitempty"`
	Database   string          `json:"database"`
	Access     ScopedKeyAccess `json:"access"`
	TTLSeconds *int64          `json:"ttl_seconds,omitempty"`
}

type ScopedKey struct {
	Key       string          `json:"key"`
	ID        string          `json:"id" ulid:"required"`
	ProjectID string          `json:"project_id" ulid:"required"`
	Database  string          `json:"database" ulid:"required"`
	Access    ScopedKeyAccess `json:"access"`
	ExpiresAt time.Time       `json:"expires_at"`
}

func (c *Client) CreateDatabase(ctx context.Context, project, name string, opts CreateDatabaseOptions) (*ManagedDatabase, error) {
	if err := checkName("database", name); err != nil {
		return nil, err
	}
	if err := checkAgentTTL(opts.TTLSeconds, 31536000); err != nil {
		return nil, err
	}
	if opts.ReplicaRegions != nil && opts.AllRegions {
		return nil, errors.New("terrabase: set ReplicaRegions or AllRegions, not both")
	}
	req, err := projectRequest(http.MethodPost, project, nil, "databases")
	if err != nil {
		return nil, err
	}
	body := struct {
		Name string `json:"name"`
		CreateDatabaseOptions
	}{name, opts}
	return publicCall[ManagedDatabase](ctx, c, req, body)
}

func (c *Client) ForkDatabase(ctx context.Context, project, source, name string, opts ForkDatabaseOptions) (*ManagedDatabase, error) {
	if err := checkName("database", name); err != nil {
		return nil, err
	}
	if err := checkAgentTTL(opts.TTLSeconds, 31536000); err != nil {
		return nil, err
	}
	body := struct {
		Name string `json:"name"`
		ForkDatabaseOptions
	}{name, opts}
	d := c.Database(project, source)
	if opts.PointInTime != nil {
		operation, err := databaseCall[RestoreOperation](ctx, d, http.MethodPost, nil, body, "point-in-time", "restore")
		if err != nil {
			return nil, err
		}
		return d.awaitRestore(ctx, operation)
	}
	return databaseCall[ManagedDatabase](ctx, d, http.MethodPost, nil, body, "branches")
}

func (c *Client) SetDatabaseTTL(ctx context.Context, project, database string, ttlSeconds int64) (*ManagedDatabase, error) {
	if err := checkAgentTTL(&ttlSeconds, 31536000); err != nil {
		return nil, err
	}
	return databaseCall[ManagedDatabase](ctx, c.Database(project, database), http.MethodPut, nil,
		CreateDatabaseOptions{TTLSeconds: &ttlSeconds}, "ttl")
}

func (c *Client) ClearDatabaseTTL(ctx context.Context, project, database string) (*ManagedDatabase, error) {
	return databaseCall[ManagedDatabase](ctx, c.Database(project, database), http.MethodDelete, nil, nil, "ttl")
}

func (c *Client) DeleteDatabase(ctx context.Context, project, database string) error {
	return c.Database(project, database).Delete(ctx)
}

func (c *Client) CreateScopedKey(ctx context.Context, in ScopedKeyRequest) (*ScopedKey, error) {
	if c.apiKey == "" {
		return nil, errors.New("terrabase: CreateScopedKey requires Config.APIKey")
	}
	if in.Project != "" {
		if err := checkID(in.Project); err != nil {
			return nil, err
		}
	}
	if err := checkID(in.Database); err != nil {
		return nil, fmt.Errorf("terrabase: scoped key database: %w", err)
	}
	if in.Access != ScopedKeyRead && in.Access != ScopedKeyReadWrite {
		return nil, errors.New("terrabase: scoped key access must be read or read_write")
	}
	if err := checkAgentTTL(in.TTLSeconds, 2592000); err != nil {
		return nil, err
	}
	body, err := json.Marshal(in)
	if err != nil {
		return nil, err
	}
	ctx, cancel := context.WithTimeout(ctx, OperationTimeout)
	defer cancel()
	hc := *c.hc
	hc.CheckRedirect = func(*http.Request, []*http.Request) error { return http.ErrUseLastResponse }
	transport := Client{hc: &hc, maxResponse: c.maxResponse}
	resp, err := transport.send(ctx, request{
		method: http.MethodPost,
		body:   body,
		header: http.Header{"Authorization": {"Basic " + base64.StdEncoding.EncodeToString([]byte(c.apiKey[:26]+":"+c.apiKey))}},
	}, c.authOrigin+"/v1/terrascale/api-keys/self/scoped-keys")
	if err != nil {
		if resp != nil && (resp.status < 200 || resp.status >= 300) {
			apiErr := scopedKeyError(resp)
			apiErr.cause = err
			return nil, apiErr
		}
		return nil, fmt.Errorf("%w: %w", ErrCommitUncertain, err)
	}
	if resp.status != http.StatusCreated {
		return nil, scopedKeyError(resp)
	}
	var out ScopedKey
	if err := decodeExact(resp.body, &out); err != nil || out.Key == "" || out.ExpiresAt.IsZero() || (out.Access != ScopedKeyRead && out.Access != ScopedKeyReadWrite) {
		return nil, errors.New("terrabase: invalid scoped key response")
	}
	if err := entityid.Model(out); err != nil {
		return nil, fmt.Errorf("terrabase: invalid scoped key response: %w", err)
	}
	return &out, nil
}

func checkAgentTTL(ttl *int64, maximum int64) error {
	if ttl != nil && (*ttl < 60 || *ttl > maximum) {
		return fmt.Errorf("terrabase: TTLSeconds must be between 60 and %d", maximum)
	}
	return nil
}

func (c *Client) projectClient(project string) (*Client, error) {
	if c.apiKey == "" {
		return c, nil
	}
	if cached, ok := c.projects.Load(project); ok {
		return cached.(*Client), nil
	}
	source, err := tsauth.NewAPIKeySource(tsauth.APIKeyConfig{Issuer: c.authOrigin + "/terrascale", Key: c.apiKey, Project: project, HTTPClient: c.hc})
	if err != nil {
		return nil, err
	}
	projectClient := *c
	projectClient.tokenSource = source
	cached, _ := c.projects.LoadOrStore(project, &projectClient)
	return cached.(*Client), nil
}

func scopedKeyError(resp *response) *APIError {
	out := parseAPIError(resp.status, resp.header, resp.body)
	var oauth struct {
		Error       string `json:"error"`
		Description string `json:"error_description"`
	}
	if decodeExact(resp.body, &oauth) == nil && oauth.Error != "" {
		out.Code, out.Message = ErrorCode(oauth.Error), oauth.Description
	}
	return out
}
