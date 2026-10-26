package tsauth

import (
	"context"
	"encoding/json"
	"errors"
	"net/http"
	"net/url"
	"strings"
	"time"
	"unicode"
	"unicode/utf16"

	"terrascale.tech/sdk/internal/entityid"
)

// ProjectOAuthClient is a project's registered authorization-code/S256 PKCE
// client. ClientSecret is returned only when creating a confidential client.
// Formatting the model never reveals that secret; copy it explicitly to secure
// storage before discarding the creation result.
type ProjectOAuthClient struct {
	ClientID     string    `json:"client_id" ulid:"required"`
	Name         string    `json:"name"`
	Type         string    `json:"type"`
	RedirectURIs []string  `json:"redirect_uris"`
	CreatedAt    time.Time `json:"created_at"`
	ClientSecret string    `json:"client_secret,omitempty"`
}

func (*ProjectOAuthClient) requiredAuthMembers() string {
	return "client_id name type redirect_uris created_at"
}

// String redacts the one-time client secret.
func (ProjectOAuthClient) String() string { return "ProjectOAuthClient{secret:[REDACTED]}" }

// GoString also redacts the secret in Go-syntax diagnostics.
func (c ProjectOAuthClient) GoString() string { return c.String() }

// ProjectOAuthClientRequest registers exact redirect URIs. Type is public or
// confidential; redirects must also satisfy the project's server-side allowlist.
type ProjectOAuthClientRequest struct {
	Name         string   `json:"name"`
	Type         string   `json:"type"`
	RedirectURIs []string `json:"redirect_uris"`
}

// ListProjectOAuthClients returns every registered client in one page, without
// secrets (GET /v1/{project}/clients, viewer). No pagination parameters exist.
func (u *User) ListProjectOAuthClients(ctx context.Context, project string) (*Page[ProjectOAuthClient], error) {
	projectUser, err := u.projectClientUser(project)
	if err != nil {
		return nil, err
	}
	return call[Page[ProjectOAuthClient]](ctx, projectUser, http.MethodGet, []string{"clients"}, nil, nil)
}

// CreateProjectOAuthClient registers a client (POST /v1/{project}/clients,
// admin). Confidential secrets appear once. The request is sent once: an
// interrupted response may leave a created client; list before deciding to retry.
func (u *User) CreateProjectOAuthClient(ctx context.Context, project string, request ProjectOAuthClientRequest) (*ProjectOAuthClient, error) {
	projectUser, err := u.projectClientUser(project)
	if err != nil {
		return nil, err
	}
	name := strings.TrimSpace(request.Name)
	if len(utf16.Encode([]rune(name))) > 128 || !strings.ContainsFunc(name, func(r rune) bool { return unicode.IsLetter(r) || unicode.IsDigit(r) }) || strings.ContainsFunc(name, unicode.IsControl) {
		return nil, errors.New("tsauth: client name must contain a letter or digit, no controls, and at most 128 UTF-16 code units")
	}
	if request.Type != "public" && request.Type != "confidential" {
		return nil, errors.New("tsauth: client type must be public or confidential")
	}
	if len(request.RedirectURIs) < 1 || len(request.RedirectURIs) > 10 {
		return nil, errors.New("tsauth: client needs 1 to 10 exact redirect URIs")
	}
	for _, redirect := range request.RedirectURIs {
		parsed, parseErr := url.Parse(redirect)
		if parseErr != nil {
			return nil, errors.New("tsauth: redirect URI must be an absolute URL")
		}
		secure := parsed.Scheme == "https" || parsed.Scheme == "http" && loopback(strings.ToLower(parsed.Hostname()))
		if parsed.Host == "" || strings.Contains(redirect, "#") || !secure {
			return nil, errors.New("tsauth: redirect URI must use absolute HTTPS, or HTTP on loopback, without a fragment")
		}
	}
	data, err := json.Marshal(request)
	if err != nil || len(data) > 16<<10 {
		return nil, errors.New("tsauth: client registration exceeds the 16 KiB request bound")
	}
	return call[ProjectOAuthClient](ctx, projectUser, http.MethodPost, []string{"clients"}, nil, request)
}

// DeleteProjectOAuthClient deletes a registered client (DELETE
// /v1/{project}/clients/{client_id}, admin). It never retries the mutation.
func (u *User) DeleteProjectOAuthClient(ctx context.Context, project, clientID string) error {
	projectUser, err := u.projectClientUser(project)
	if err != nil {
		return err
	}
	if err := entityid.Check(clientID); err != nil {
		return err
	}
	return projectUser.none(ctx, http.MethodDelete, []string{"clients", clientID}, nil, nil)
}

func (u *User) projectClientUser(project string) (*User, error) {
	if err := entityid.Check(project); err != nil {
		return nil, err
	}
	client := *u.c
	client.base = client.base[:strings.LastIndex(client.base, "/")+1] + project
	return &User{c: &client, token: u.token}, nil
}
