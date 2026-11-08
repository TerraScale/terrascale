package tsauth

import (
	"context"
	"errors"
	"net/http"
	"regexp"
	"strings"
	"time"
	"unicode"

	"terrascale.tech/sdk/internal/entityid"
)

// SsoVerification is the DNS TXT record that proves the organization owns
type SsoVerification struct {
	Type  string `json:"type"`
	Name  string `json:"name"`
	Value string `json:"value"`
}

func (*SsoVerification) requiredAuthMembers() string { return "type name value" }

// SsoOidc names the organization's OpenID Connect identity provider. The
type SsoOidc struct {
	Issuer          string `json:"issuer"`
	ClientID        string `json:"client_id"`
	ClientSecretSet bool   `json:"client_secret_set"`
	RedirectURI     string `json:"redirect_uri"`
}

func (*SsoOidc) requiredAuthMembers() string { return "client_secret_set redirect_uri" }

// SsoConnection is the organization's single sign-on connection. Until
type SsoConnection struct {
	OrganizationID string           `json:"organization_id" ulid:"required"`
	Domain         string           `json:"domain"`
	DomainVerified bool             `json:"domain_verified"`
	VerifiedAt     *time.Time       `json:"verified_at"`
	Verification   *SsoVerification `json:"verification"`
	Protocol       string           `json:"protocol"`
	DefaultRole    Role             `json:"default_role"`
	Oidc           *SsoOidc         `json:"oidc"`
	CreatedAt      time.Time        `json:"created_at"`
	UpdatedAt      time.Time        `json:"updated_at"`
}

func (*SsoConnection) requiredAuthMembers() string {
	return "organization_id domain domain_verified verification protocol default_role created_at updated_at"
}

// SsoOidcRequest carries the identity provider registration. ClientSecret
type SsoOidcRequest struct {
	Issuer       string
	ClientID     string
	ClientSecret string
}

// String redacts the OIDC client secret.
func (SsoOidcRequest) String() string { return "SsoOidcRequest{client_secret:[REDACTED]}" }

// GoString also redacts the secret in Go-syntax diagnostics.
func (r SsoOidcRequest) GoString() string { return r.String() }

type ssoOidcRequestBody struct {
	Issuer       string  `json:"issuer"`
	ClientID     string  `json:"client_id"`
	ClientSecret *string `json:"client_secret,omitempty"`
}

// SsoConnectionRequest creates or changes the organization's SSO connection.
type SsoConnectionRequest struct {
	Domain      string
	Protocol    string
	DefaultRole Role
	Oidc        *SsoOidcRequest
}

// String redacts the OIDC client secret.
func (SsoConnectionRequest) String() string {
	return "SsoConnectionRequest{oidc.client_secret:[REDACTED]}"
}

// GoString also redacts the secret in Go-syntax diagnostics.
func (r SsoConnectionRequest) GoString() string { return r.String() }

type ssoRequestBody struct {
	Domain      string              `json:"domain"`
	Protocol    *string             `json:"protocol,omitempty"`
	DefaultRole Role                `json:"default_role,omitempty"`
	Oidc        *ssoOidcRequestBody `json:"oidc,omitempty"`
}

// GetSsoConnection reads the organization's SSO connection (GET
func (u *User) GetSsoConnection(ctx context.Context, org string) (*SsoConnection, error) {
	if err := entityid.Check(org); err != nil {
		return nil, err
	}
	return call[SsoConnection](ctx, u, http.MethodGet, orgPath(org, "sso"), nil, nil)
}

// PutSsoConnection creates or changes the organization's SSO connection
func (u *User) PutSsoConnection(ctx context.Context, org string, request SsoConnectionRequest) (*SsoConnection, error) {
	if err := entityid.Check(org); err != nil {
		return nil, err
	}
	domain := strings.ToLower(strings.TrimSuffix(strings.TrimSpace(request.Domain), "."))
	if len(domain) < 4 || len(domain) > 253 || !ssoDomainPattern.MatchString(domain) {
		return nil, errors.New("tsauth: domain must be a lowercase email domain such as example.com")
	}
	if request.Protocol != "" && request.Protocol != "oidc" {
		return nil, errors.New("tsauth: protocol must be oidc")
	}
	if request.DefaultRole != "" && request.DefaultRole != RoleViewer && request.DefaultRole != RoleMember && request.DefaultRole != RoleAdmin {
		return nil, errors.New("tsauth: default_role must be viewer, member or admin")
	}
	oidc := request.Oidc
	if oidc == nil {
		return nil, errors.New("tsauth: oidc must carry the issuer and client registration")
	}
	issuer := strings.TrimSpace(oidc.Issuer)
	if len(issuer) > 512 || !strings.HasPrefix(issuer, "https://") || strings.ContainsAny(issuer, "@?#") {
		return nil, errors.New("tsauth: oidc.issuer must be an https URL without user info, query or fragment")
	}
	if oidc.ClientID == "" || len(oidc.ClientID) > 256 || strings.ContainsFunc(oidc.ClientID, unicode.IsControl) {
		return nil, errors.New("tsauth: oidc.client_id must be 1 to 256 characters without controls")
	}
	if len(oidc.ClientSecret) > 1024 {
		return nil, errors.New("tsauth: oidc.client_secret must be at most 1024 characters")
	}
	body := ssoRequestBody{Domain: domain, Protocol: nil, DefaultRole: request.DefaultRole, Oidc: &ssoOidcRequestBody{Issuer: issuer, ClientID: oidc.ClientID}}
	if request.Protocol != "" {
		protocol := request.Protocol
		body.Protocol = &protocol
	}
	if oidc.ClientSecret != "" {
		secret := oidc.ClientSecret
		body.Oidc.ClientSecret = &secret
	}
	return call[SsoConnection](ctx, u, http.MethodPut, orgPath(org, "sso"), nil, body)
}

// VerifySsoDomain re-checks the connection's DNS TXT record (POST
func (u *User) VerifySsoDomain(ctx context.Context, org string) (*SsoConnection, error) {
	if err := entityid.Check(org); err != nil {
		return nil, err
	}
	return call[SsoConnection](ctx, u, http.MethodPost, orgPath(org, "sso", "verify"), nil, nil)
}

// DeleteSsoConnection removes the SSO connection (DELETE
func (u *User) DeleteSsoConnection(ctx context.Context, org string) error {
	if err := entityid.Check(org); err != nil {
		return err
	}
	return u.none(ctx, http.MethodDelete, orgPath(org, "sso"), nil, nil)
}

var ssoDomainPattern = regexp.MustCompile(`^([a-z0-9]([a-z0-9-]{0,61}[a-z0-9])?\.)+[a-z][a-z0-9-]{0,61}[a-z0-9]$`)
