package auth

import (
	"context"
	"encoding/base64"
	"encoding/json/v2"
	"errors"
	"fmt"
	"strings"

	"github.com/coreos/go-oidc/v3/oidc"
	"terrascale.tech/dashboard/internal/core"
	"terrascale.tech/dashboard/internal/platform/platformapi"
)

// ResourceToken verifies an organization or project token of ts-auth's
// terrascale tenant as ts-auth's docs/platform-contract.md (§6.4) asks a
// resource server to: an ES256 at+jwt signed with a key of the issuer's
// JWKS, whose iss is the issuer and whose aud includes audience, unexpired
// and naming an organization. It returns what the token grants, acting as
// the user it was exchanged for, whose account it links or opens as signing
// in does. A token that grants nothing fails with core.ErrUnauthorized; any
// other error means ts-auth could not be reached.
//
// API key tokens name no user, and the dashboard's tools still act as one,
// so they are refused until the identity store moves to ts-auth.
func (t *TSAuth) ResourceToken(ctx context.Context, store core.Store, audience, raw string) (platformapi.Grant, error) {
	if !accessTokenType(raw) {
		return platformapi.Grant{}, core.ErrUnauthorized
	}
	provider, err := t.discover(ctx)
	if err != nil {
		return platformapi.Grant{}, err
	}
	verifier := provider.issuer.Verifier(&oidc.Config{ClientID: audience, SupportedSigningAlgs: []string{oidc.ES256}})
	token, err := verifier.Verify(oidc.ClientContext(ctx, t.client), raw)
	if err != nil {
		return platformapi.Grant{}, fmt.Errorf("%w: %w", core.ErrUnauthorized, err)
	}
	var claims struct {
		Email          string `json:"email"`
		EmailVerified  bool   `json:"email_verified"`
		OrganizationID string `json:"org_id"`
		ProjectID      string `json:"project_id"`
		Scope          string `json:"scope"`
	}
	if err := token.Claims(&claims); err != nil || claims.OrganizationID == "" {
		return platformapi.Grant{}, core.ErrUnauthorized
	}
	email := ""
	if claims.EmailVerified {
		email = core.NormalizeEmail(claims.Email)
	}
	if token.Subject == "" || len(email) > 254 || !validEmail(email) {
		return platformapi.Grant{}, core.ErrUnauthorized
	}
	account, err := tsAuthAccount(ctx, store, tsAuthIdentity{Subject: token.Subject, Email: email, Name: email})
	if errors.Is(err, errEmailUnusable) {
		return platformapi.Grant{}, core.ErrUnauthorized
	}
	if err != nil {
		return platformapi.Grant{}, err
	}
	return platformapi.Grant{
		Principal: account.Principal(), OrganizationID: claims.OrganizationID, ProjectID: claims.ProjectID,
		Scopes: strings.Fields(claims.Scope),
	}, nil
}

// accessTokenType reports whether raw is a JWT whose typ header names an
// RFC 9068 access token; ID tokens are typ JWT.
func accessTokenType(raw string) bool {
	segment, _, ok := strings.Cut(raw, ".")
	header, err := base64.RawURLEncoding.DecodeString(segment)
	var fields struct {
		Typ string `json:"typ"`
	}
	return ok && err == nil && json.Unmarshal(header, &fields) == nil &&
		(strings.EqualFold(fields.Typ, "at+jwt") || strings.EqualFold(fields.Typ, "application/at+jwt"))
}
