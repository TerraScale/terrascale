package sdk

import (
	"context"
	"crypto/subtle"
	"net/url"
	"slices"
	"time"
)

type TSAuthResourceApplicationConfig struct {
	OIDC             TSAuthOIDCApplicationConfig
	ResourceAudience string
}

func (TSAuthResourceApplicationConfig) String() string {
	return "TSAuthResourceApplicationConfig{authentication:[REDACTED]}"
}

// TSAuthResourceApplication adds an explicit resource to the existing verified
// browser OIDC client. ID tokens still have the originating client audience.
type TSAuthResourceApplication struct {
	identity *TSAuthOIDCApplication
	resource *TSAuthResourceClient
}

func (i *TSAuthInstanceClient) NewResourceApplication(config TSAuthResourceApplicationConfig) (*TSAuthResourceApplication, error) {
	resource, err := i.NewResourceClient(TSAuthResourceClientConfig{Issuer: config.OIDC.Issuer, ResourceAudience: config.ResourceAudience, Authentication: config.OIDC.Authentication})
	if err != nil {
		return nil, err
	}
	identity, err := resource.oidc.NewApplication(config.OIDC)
	if err != nil {
		return nil, err
	}
	return &TSAuthResourceApplication{identity: identity, resource: resource}, nil
}

// The application stores this attempt on its server, bound to the browser, and
// consumes it atomically once before exchanging the callback.
type TSAuthResourceAuthorizationAttempt struct {
	Authorization    TSAuthAuthorizationAttempt
	ResourceAudience string
	scopes           []string
}

func (TSAuthResourceAuthorizationAttempt) String() string {
	return "TSAuthResourceAuthorizationAttempt{state,nonce,verifier:[REDACTED]}"
}

func (a *TSAuthResourceApplication) BeginAuthorization(ctx context.Context, scopes []string) (*TSAuthResourceAuthorizationAttempt, error) {
	scopes = slices.Clone(scopes)
	if err := tsAuthResourceScopes(scopes); err != nil {
		return nil, err
	}
	attempt, err := a.identity.BeginAuthorization(ctx, scopes)
	if err != nil {
		return nil, err
	}
	endpoint, err := url.Parse(attempt.AuthorizationURL)
	if err != nil {
		return nil, oidcError("invalid_authorization_endpoint", "authorize", false, nil)
	}
	query := endpoint.Query()
	query.Set("resource", a.resource.config.ResourceAudience)
	endpoint.RawQuery = query.Encode()
	attempt.AuthorizationURL = endpoint.String()
	return &TSAuthResourceAuthorizationAttempt{Authorization: *attempt, ResourceAudience: a.resource.config.ResourceAudience, scopes: slices.Clone(scopes)}, nil
}

type TSAuthResourceLogin struct {
	Grant    *TSAuthResourceTokens
	Identity TSAuthVerifiedIDToken
}

func (TSAuthResourceLogin) String() string { return "TSAuthResourceLogin{tokens:[REDACTED]}" }

func (a *TSAuthResourceApplication) ExchangeCode(ctx context.Context, callback TSAuthOIDCCallback, attempt TSAuthResourceAuthorizationAttempt) (*TSAuthResourceLogin, error) {
	binding := attempt.Authorization
	config := a.identity.config
	if attempt.ResourceAudience != a.resource.config.ResourceAudience || binding.Issuer != config.Issuer || binding.ClientID != config.Authentication.ClientID || binding.RedirectURI != config.RedirectURI || !time.Now().Before(binding.ExpiresAt) || binding.Nonce == "" || !validPKCEVerifier(binding.CodeVerifier) || callback.State == "" || subtle.ConstantTimeCompare([]byte(callback.State), []byte(binding.State)) != 1 || !slices.Contains(attempt.scopes, "openid") {
		return nil, oidcError("invalid_authorization_attempt", "exchange", false, nil)
	}
	if _, err := a.identity.Discover(ctx); err != nil {
		return nil, err
	}
	grant, err := a.resource.ExchangeAuthorizationCode(ctx, callback.Code, binding.RedirectURI, binding.CodeVerifier, attempt.scopes, true)
	if err != nil {
		return nil, err
	}
	if grant.Tokens.IDToken == nil || *grant.Tokens.IDToken == "" {
		return nil, oidcError("invalid_id_token", "exchange", true, nil)
	}
	identity, err := a.identity.VerifyIDToken(ctx, *grant.Tokens.IDToken, TSAuthIDTokenVerificationOptions{Nonce: binding.Nonce, AccessToken: grant.Tokens.AccessToken})
	if err != nil {
		return nil, oidcError("invalid_id_token", "exchange", true, err)
	}
	return &TSAuthResourceLogin{Grant: grant, Identity: *identity}, nil
}

func (a *TSAuthResourceApplication) Refresh(ctx context.Context, previous *TSAuthResourceTokens, options TSAuthResourceRefreshOptions) (*TSAuthResourceTokens, error) {
	return a.resource.Refresh(ctx, previous, options)
}

func (a *TSAuthResourceApplication) EndSessionURL(ctx context.Context, redirectURI, state string) (string, error) {
	return a.identity.EndSessionURL(ctx, redirectURI, state)
}

func (c TSAuthResourceApplicationConfig) GoString() string    { return c.String() }
func (a TSAuthResourceAuthorizationAttempt) GoString() string { return a.String() }
func (l TSAuthResourceLogin) GoString() string                { return l.String() }
func (*TSAuthResourceApplication) String() string {
	return "TSAuthResourceApplication{credentials:[REDACTED]}"
}
func (a *TSAuthResourceApplication) GoString() string { return a.String() }
