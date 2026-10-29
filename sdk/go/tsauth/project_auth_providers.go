package tsauth

import (
	"context"
	"errors"
	"fmt"
	"io"
	"net/http"
)

// Supported project social sign-in providers.
const (
	ProjectProviderGoogle = "google"
	ProjectProviderGitHub = "github"
	ProjectProviderApple  = "apple"
	ProjectProviderOIDC   = "oidc"
)

// ProjectAuthProviderRequest configures the project's own provider OAuth client.
// ClientID is a vendor ID, not a ULID. Nil ClientSecret omits the write-only
// secret, preserving the stored one; the first configuration requires a secret.
// OIDC requires an HTTPS issuer. Apple requires TeamID, KeyID and the EC P-256
// .p8 private key as ClientSecret. The server validates these provider rules.
// JSON serialization intentionally includes a supplied secret for the request;
// formatted diagnostics redact the entire request. Do not log serialized JSON.
type ProjectAuthProviderRequest struct {
	ClientID     string  `json:"client_id"`
	ClientSecret *string `json:"client_secret,omitempty"`
	Issuer       *string `json:"issuer,omitempty"`
	TeamID       *string `json:"team_id,omitempty"`
	KeyID        *string `json:"key_id,omitempty"`
}

// String redacts request diagnostics, including write-only secrets.
func (r ProjectAuthProviderRequest) String() string {
	return "tsauth.ProjectAuthProviderRequest{redacted}"
}

// Format redacts every formatting verb, including Go-syntax diagnostics.
func (r ProjectAuthProviderRequest) Format(s fmt.State, verb rune) {
	_, _ = io.WriteString(s, r.String())
}

// SetProjectProvider requires the platform user's sign-in token and admin
// project role. Register the returned provider CallbackURL with the vendor.
// An omitted secret preserves it; no secret is returned. Diagnostics retain
// APIError status/code/RetryAfter with sanitized messages. The mutation is not
// replayed automatically, including on authentication or transient failures.
func (u *User) SetProjectProvider(ctx context.Context, project, provider string, request ProjectAuthProviderRequest) (*ProjectAuthSettings, error) {
	if !projectAuthProviderName(provider) {
		return nil, errors.New("tsauth: provider must be google, github, apple or oidc")
	}
	return u.projectAuthRequest(ctx, project, http.MethodPut, "providers/"+provider, request)
}

// DeleteProjectProvider removes the provider and its stored secret, returning
// updated settings (HTTP 200). Existing user accounts survive. Requires the
// platform user's sign-in token and admin project role; sends one request.
func (u *User) DeleteProjectProvider(ctx context.Context, project, provider string) (*ProjectAuthSettings, error) {
	if !projectAuthProviderName(provider) {
		return nil, errors.New("tsauth: provider must be google, github, apple or oidc")
	}
	return u.projectAuthRequest(ctx, project, http.MethodDelete, "providers/"+provider, nil)
}

func projectAuthProviderName(provider string) bool {
	switch provider {
	case ProjectProviderGoogle, ProjectProviderGitHub, ProjectProviderApple, ProjectProviderOIDC:
		return true
	default:
		return false
	}
}
