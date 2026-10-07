package sdk

import (
	"bytes"
	"context"
	"encoding/json"
	"fmt"
	"maps"
	"net/http"
	"net/url"
	"slices"
	"strings"
	"sync"
	"time"
	"unicode/utf8"
)

const TSAuthDeviceCodeGrant = "urn:ietf:params:oauth:grant-type:device_code"

// TSAuthResourceAuthentication belongs to the serving resource. It cannot be
// passed to an originating client's grant, refresh or revocation methods.
type TSAuthResourceAuthentication struct {
	ClientID string
	Secret   string `json:"-"`
}

func (TSAuthResourceAuthentication) String() string {
	return "TSAuthResourceAuthentication{credentials:[REDACTED]}"
}

// TSAuthResourceClientConfig fixes the issuer and registered resource exactly.
// ResourceAudience is preserved as supplied, including its raw path and query.
type TSAuthResourceClientConfig struct {
	Issuer           string
	ResourceAudience string
	Authentication   TSAuthOAuthClientAuthentication
}

func (TSAuthResourceClientConfig) String() string {
	return "TSAuthResourceClientConfig{authentication:[REDACTED]}"
}

type TSAuthResourceClient struct {
	oidc   *TSAuthOIDCClient
	config TSAuthResourceClientConfig
}

func (i *TSAuthInstanceClient) NewResourceClient(config TSAuthResourceClientConfig) (*TSAuthResourceClient, error) {
	base, err := tsAuthResourceConfig(i, config.Issuer, config.ResourceAudience)
	if err != nil {
		return nil, err
	}
	if err := config.Authentication.validate(); err != nil {
		return nil, err
	}
	if !utf8.ValidString(config.Authentication.ClientID) || !utf8.ValidString(config.Authentication.ClientSecret) {
		return nil, oidcError("invalid_client_configuration", "configuration", false, nil)
	}
	oidc, err := tsAuthCredentialTransport(i, base)
	if err != nil {
		return nil, err
	}
	return &TSAuthResourceClient{oidc: oidc, config: config}, nil
}

// Credential operations use an owned HTTP client without browser cookies or
// management/native token providers. The existing no-redirect form transport
// still formats each Basic component independently before joining them.
func tsAuthCredentialTransport(i *TSAuthInstanceClient, base string) (*TSAuthOIDCClient, error) {
	httpClient := *i.c.http
	httpClient.Jar = nil
	c, err := NewClient(Config{
		TSAuthBaseURL: base, HTTPClient: &httpClient,
		DiscoveryBaseURL:      i.c.cfg.DiscoveryBaseURL,
		TSAuthUseReverseProxy: i.c.cfg.TSAuthUseReverseProxy,
		RequestTimeout:        i.c.cfg.RequestTimeout,
	})
	if err != nil {
		return nil, err
	}
	instance, err := c.TSAuth.Instance(i.slug)
	if err != nil {
		return nil, err
	}
	return instance.OIDC, nil
}

func tsAuthResourceConfig(i *TSAuthInstanceClient, issuer, audience string) (string, error) {
	if err := tsAuthResourceAudience(audience); err != nil {
		return "", err
	}
	u, err := trustedOIDCURL(issuer)
	if err != nil {
		return "", oidcError("issuer_transport_mismatch", "configuration", false, nil)
	}
	base, ok := i.issuerBase(u)
	if !ok || issuer != u.Scheme+"://"+u.Host+"/"+url.PathEscape(i.slug) {
		return "", oidcError("issuer_transport_mismatch", "configuration", false, nil)
	}
	return base, nil
}

func tsAuthResourceAudience(value string) error {
	remainder, ok := strings.CutPrefix(value, "https://")
	if !ok || len(value) > 2048 {
		return oidcError("invalid_resource", "configuration", false, nil)
	}
	end := strings.IndexAny(remainder, "/?#")
	if end < 0 {
		end = len(remainder)
	}
	authority := remainder[:end]
	if authority == "" || strings.Contains(authority, "@") || strings.ContainsAny(remainder[end:], "[]") {
		return oidcError("invalid_resource", "configuration", false, nil)
	}
	for index := range len(value) {
		b := value[index]
		if !(b >= 'a' && b <= 'z' || b >= 'A' && b <= 'Z' || b >= '0' && b <= '9' || strings.ContainsRune("-._~:/?[]@!$&'()*+,;=%", rune(b))) {
			return oidcError("invalid_resource", "configuration", false, nil)
		}
		if b == '%' && (index+2 >= len(value) || !tsAuthHex(value[index+1]) || !tsAuthHex(value[index+2])) {
			return oidcError("invalid_resource", "configuration", false, nil)
		}
	}
	u, err := url.Parse(value)
	if err != nil || u.Scheme != "https" || u.Hostname() == "" || u.User != nil || u.Fragment != "" || u.Opaque != "" {
		return oidcError("invalid_resource", "configuration", false, nil)
	}
	return nil
}

func tsAuthHex(b byte) bool {
	return b >= '0' && b <= '9' || b >= 'A' && b <= 'F' || b >= 'a' && b <= 'f'
}

func tsAuthResourceScopes(scopes []string) error {
	if len(scopes) > 100 {
		return oidcError("invalid_scope", "request", false, nil)
	}
	seen := make(map[string]bool, len(scopes))
	for _, scope := range scopes {
		if scope == "" || len(scope) > 256 || seen[scope] {
			return oidcError("invalid_scope", "request", false, nil)
		}
		for index := range len(scope) {
			b := scope[index]
			if !(b == 0x21 || b >= 0x23 && b <= 0x5b || b >= 0x5d && b <= 0x7e) {
				return oidcError("invalid_scope", "request", false, nil)
			}
		}
		seen[scope] = true
	}
	return nil
}

func tsAuthResourceScopeString(value string) ([]string, error) {
	var scopes []string
	for scope := range strings.FieldsSeq(value) {
		scopes = append(scopes, scope)
	}
	if err := tsAuthResourceScopes(scopes); err != nil {
		return nil, err
	}
	// A response uses the producer's canonical space-separated spelling.
	if strings.Join(scopes, " ") != value {
		return nil, oidcError("invalid_scope", "response", false, nil)
	}
	return scopes, nil
}

// TSAuthResourceTokens retains the exchange's audience and granted scope set.
// Tokens remain opaque; this result is not an accepted resource principal.
type TSAuthResourceTokens struct {
	Tokens   TSAuthTokenResponse
	issuer   string
	clientID string
	audience string
	refresh  string
	scopes   []string
}

func (TSAuthResourceTokens) String() string             { return "TSAuthResourceTokens{tokens:[REDACTED]}" }
func (t *TSAuthResourceTokens) Audience() string        { return t.audience }
func (t *TSAuthResourceTokens) GrantedScopes() []string { return slices.Clone(t.scopes) }

type tsAuthResourceTokenWire struct {
	tokens    TSAuthTokenResponse
	operation string
}

func (w *tsAuthResourceTokenWire) decodeHTTPResponse(status int, body []byte) error {
	optional := []string{"refresh_token", "id_token"}
	if w.operation == "client_credentials" {
		optional = nil
	}
	if w.operation == "refresh" {
		optional = []string{"refresh_token"}
	}
	object, err := tsAuthResourceObject(status, body, []string{"access_token", "token_type", "expires_in", "scope"}, optional)
	if err != nil {
		return err
	}
	if w.operation == "refresh" || w.operation == "exchange" || w.operation == "device_poll" {
		if _, present := object["refresh_token"]; !present {
			return oidcError("invalid_token_response", "response", false, nil)
		}
	}
	if err := json.Unmarshal(body, &w.tokens); err != nil || w.tokens.AccessToken == "" || w.tokens.TokenType != "Bearer" || w.tokens.ExpiresIn <= 0 || w.tokens.Scope == nil || (w.tokens.RefreshToken != nil && *w.tokens.RefreshToken == "") || (w.tokens.IDToken != nil && *w.tokens.IDToken == "") {
		return oidcError("invalid_token_response", "response", false, nil)
	}
	if w.operation == "refresh" && w.tokens.RefreshToken == nil {
		return oidcError("invalid_token_response", "response", false, nil)
	}
	if _, present := object["id_token"]; present && w.tokens.IDToken == nil {
		return oidcError("invalid_token_response", "response", false, nil)
	}
	if w.operation == "exchange" || w.operation == "device_poll" {
		scopes, err := tsAuthResourceScopeString(*w.tokens.Scope)
		if err != nil || slices.Contains(scopes, "openid") && w.tokens.IDToken == nil {
			return oidcError("invalid_token_response", "response", false, nil)
		}
	}
	return nil
}

func (c *TSAuthResourceClient) token(ctx context.Context, operation string, form url.Values, requested []string, requireRefresh bool) (*TSAuthResourceTokens, error) {
	if ctx == nil || ctx.Err() != nil {
		return nil, oidcError("invalid_context", operation, false, nil)
	}
	requested = slices.Clone(requested)
	if err := tsAuthResourceScopes(requested); err != nil {
		return nil, err
	}
	wire := tsAuthResourceTokenWire{operation: operation}
	if err := c.oidc.postAuthenticatedForm(ctx, "oauth/token", form, c.config.Authentication, &wire, "tsauth.resource."+operation); err != nil {
		return nil, oidcOperationError(operation, true, err)
	}
	scopes, err := tsAuthResourceScopeString(*wire.tokens.Scope)
	if err != nil || (requireRefresh && wire.tokens.RefreshToken == nil) {
		return nil, oidcError("invalid_token_response", operation, true, nil)
	}
	for _, scope := range scopes {
		if !slices.Contains(requested, scope) {
			return nil, oidcError("scope_expansion", operation, true, nil)
		}
	}
	result := &TSAuthResourceTokens{Tokens: wire.tokens, issuer: c.config.Issuer, clientID: c.config.Authentication.ClientID, audience: c.config.ResourceAudience, scopes: scopes}
	if wire.tokens.RefreshToken != nil {
		result.refresh = *wire.tokens.RefreshToken
	}
	return result, nil
}

func (c *TSAuthResourceClient) RequestClientCredentials(ctx context.Context, scopes []string) (*TSAuthResourceTokens, error) {
	scopes = slices.Clone(scopes)
	if c.config.Authentication.Method == TSAuthClientAuthenticationNone || len(scopes) == 0 {
		return nil, oidcError("explicit_confidential_resource_grant_required", "client_credentials", false, nil)
	}
	form := url.Values{"grant_type": {"client_credentials"}, "resource": {c.config.ResourceAudience}, "scope": {strings.Join(scopes, " ")}}
	return c.token(ctx, "client_credentials", form, scopes, false)
}

// ExchangeAuthorizationCode repeats the configured resource only when asked;
// omission inherits the producer's code binding. It never accepts a new URI.
func (c *TSAuthResourceClient) ExchangeAuthorizationCode(ctx context.Context, code, redirectURI, verifier string, scopes []string, repeatResource bool) (*TSAuthResourceTokens, error) {
	scopes = slices.Clone(scopes)
	if requireID("code", code) != nil || !validPKCEVerifier(verifier) {
		return nil, oidcError("invalid_code_request", "exchange", false, nil)
	}
	if _, err := trustedOIDCRedirect(redirectURI); err != nil {
		return nil, err
	}
	form := url.Values{"grant_type": {"authorization_code"}, "code": {code}, "redirect_uri": {redirectURI}, "code_verifier": {verifier}}
	if repeatResource {
		form.Set("resource", c.config.ResourceAudience)
	}
	return c.token(ctx, "exchange", form, scopes, false)
}

// RefreshScopes nil inherits the granted set; a nonnil empty slice explicitly
// narrows to no scopes. Every replacement keeps the private resource binding.
type TSAuthResourceRefreshOptions struct {
	RefreshScopes  []string
	RepeatResource bool
}

func (c *TSAuthResourceClient) Refresh(ctx context.Context, previous *TSAuthResourceTokens, options TSAuthResourceRefreshOptions) (*TSAuthResourceTokens, error) {
	if previous == nil || previous.issuer != c.config.Issuer || previous.clientID != c.config.Authentication.ClientID || previous.audience != c.config.ResourceAudience || previous.refresh == "" {
		return nil, oidcError("invalid_refresh_binding", "refresh", false, nil)
	}
	requested := previous.scopes
	form := url.Values{"grant_type": {"refresh_token"}, "refresh_token": {previous.refresh}}
	if options.RefreshScopes != nil {
		requested = slices.Clone(options.RefreshScopes)
		if err := tsAuthResourceScopes(requested); err != nil {
			return nil, err
		}
		for _, scope := range requested {
			if !slices.Contains(previous.scopes, scope) {
				return nil, oidcError("scope_expansion", "refresh", false, nil)
			}
		}
		form.Set("scope", strings.Join(requested, " "))
	}
	if options.RepeatResource {
		form.Set("resource", c.config.ResourceAudience)
	}
	return c.token(ctx, "refresh", form, requested, true)
}

// TSAuthResourceDeviceAuthorization holds a producer-issued polling interval.
// PollDevice performs one explicit attempt; there is no background polling.
type TSAuthResourceDeviceAuthorization struct {
	DeviceCode                       string `json:"device_code"`
	UserCode                         string `json:"user_code"`
	VerificationURI                  string `json:"verification_uri"`
	VerificationURIComplete          string `json:"verification_uri_complete"`
	ExpiresIn                        int64  `json:"expires_in"`
	Interval                         int64  `json:"interval"`
	mutex                            sync.Mutex
	issuer, clientID, audience, code string
	interval                         int64
	scopes                           []string
	expiresAt, nextPoll              time.Time
}

func (*TSAuthResourceDeviceAuthorization) String() string {
	return "TSAuthResourceDeviceAuthorization{codes:[REDACTED]}"
}

func (d *TSAuthResourceDeviceAuthorization) decodeHTTPResponse(status int, body []byte) error {
	if _, err := tsAuthResourceObject(status, body, []string{"device_code", "user_code", "verification_uri", "verification_uri_complete", "expires_in", "interval"}, nil); err != nil {
		return err
	}
	// Avoid decoding unexported synchronization state.
	type wire TSAuthResourceDeviceAuthorization
	return json.Unmarshal(body, (*wire)(d))
}

func (c *TSAuthResourceClient) BeginDeviceAuthorization(ctx context.Context, scopes []string) (*TSAuthResourceDeviceAuthorization, error) {
	scopes = slices.Clone(scopes)
	if ctx == nil || ctx.Err() != nil || len(scopes) == 0 {
		return nil, oidcError("invalid_device_request", "device", false, nil)
	}
	if err := tsAuthResourceScopes(scopes); err != nil {
		return nil, err
	}
	form := url.Values{"resource": {c.config.ResourceAudience}, "scope": {strings.Join(scopes, " ")}}
	var result TSAuthResourceDeviceAuthorization
	if err := c.oidc.postAuthenticatedForm(ctx, "oauth/device_authorization", form, c.config.Authentication, &result, "tsauth.resource.device"); err != nil {
		return nil, oidcOperationError("device", true, err)
	}
	if len(result.DeviceCode) != 75 || result.UserCode == "" || result.ExpiresIn <= 0 || result.ExpiresIn > 86400 || result.Interval <= 0 || result.Interval > result.ExpiresIn {
		return nil, oidcError("invalid_device_response", "device", true, nil)
	}
	verificationURI := c.oidc.i.base + "/ui/" + url.PathEscape(c.oidc.i.slug) + "/device"
	if result.VerificationURI != verificationURI || result.VerificationURIComplete != verificationURI+"?user_code="+url.QueryEscape(result.UserCode) {
		return nil, oidcError("invalid_device_response", "device", true, nil)
	}
	result.issuer, result.clientID, result.audience, result.code = c.config.Issuer, c.config.Authentication.ClientID, c.config.ResourceAudience, result.DeviceCode
	result.interval = result.Interval
	result.scopes = slices.Clone(scopes)
	now := time.Now()
	result.expiresAt, result.nextPoll = now.Add(time.Duration(result.ExpiresIn)*time.Second), now.Add(time.Duration(result.Interval)*time.Second)
	return &result, nil
}

func (c *TSAuthResourceClient) PollDevice(ctx context.Context, device *TSAuthResourceDeviceAuthorization, repeatResource bool) (*TSAuthResourceTokens, error) {
	if device == nil {
		return nil, oidcError("invalid_device_binding", "device_poll", false, nil)
	}
	device.mutex.Lock()
	defer device.mutex.Unlock()
	now := time.Now()
	if device.issuer != c.config.Issuer || device.clientID != c.config.Authentication.ClientID || device.audience != c.config.ResourceAudience || device.code == "" || !now.Before(device.expiresAt) || now.Before(device.nextPoll) {
		return nil, oidcError("device_poll_not_admitted", "device_poll", false, nil)
	}
	device.nextPoll = now.Add(time.Duration(device.interval) * time.Second)
	form := url.Values{"grant_type": {TSAuthDeviceCodeGrant}, "device_code": {device.code}}
	if repeatResource {
		form.Set("resource", c.config.ResourceAudience)
	}
	tokens, err := c.token(ctx, "device_poll", form, device.scopes, false)
	if failure, ok := err.(*TSAuthOIDCError); ok && failure.Code == "slow_down" {
		device.interval = min(device.interval+5, 86400)
		device.Interval = device.interval
		device.nextPoll = time.Now().Add(time.Duration(device.interval) * time.Second)
	}
	if failure, ok := err.(*TSAuthOIDCError); err == nil || ok && failure.Uncertain {
		device.code = ""
	}
	return tokens, err
}

type TSAuthResourceIntrospectorConfig struct {
	Issuer           string
	ResourceAudience string
	Authentication   TSAuthResourceAuthentication
}

func (TSAuthResourceIntrospectorConfig) String() string {
	return "TSAuthResourceIntrospectorConfig{credentials:[REDACTED]}"
}

type TSAuthResourceIntrospector struct {
	oidc   *TSAuthOIDCClient
	config TSAuthResourceIntrospectorConfig
}

func (i *TSAuthInstanceClient) NewResourceIntrospector(config TSAuthResourceIntrospectorConfig) (*TSAuthResourceIntrospector, error) {
	base, err := tsAuthResourceConfig(i, config.Issuer, config.ResourceAudience)
	if err != nil {
		return nil, err
	}
	auth := TSAuthOAuthClientAuthentication{ClientID: config.Authentication.ClientID, ClientSecret: config.Authentication.Secret, Method: TSAuthClientSecretBasic}
	if err := auth.validate(); err != nil || !utf8.ValidString(auth.ClientID) || !utf8.ValidString(auth.ClientSecret) {
		return nil, oidcError("invalid_resource_credentials", "configuration", false, nil)
	}
	oidc, err := tsAuthCredentialTransport(i, base)
	if err != nil {
		return nil, err
	}
	return &TSAuthResourceIntrospector{oidc: oidc, config: config}, nil
}

// TSAuthResourceIntrospection is authenticated resource evidence. ClientID,
// Subject and Scopes do not establish project ownership or durable grants.
type TSAuthResourceIntrospection struct {
	Active        bool    `json:"active"`
	TokenType     string  `json:"token_type"`
	Issuer        string  `json:"iss"`
	Subject       string  `json:"sub"`
	Audience      string  `json:"aud"`
	ClientID      string  `json:"client_id"`
	Scope         string  `json:"scope"`
	ExpiresAt     int64   `json:"exp"`
	IssuedAt      int64   `json:"iat"`
	SessionID     *string `json:"sid"`
	PrincipalKind string  `json:"principal_kind"`
}

func (r *TSAuthResourceIntrospection) decodeHTTPResponse(status int, body []byte) error {
	object, err := tsAuthResourceObject(status, body, []string{"active"}, []string{"token_type", "iss", "sub", "aud", "client_id", "scope", "exp", "iat", "sid", "principal_kind"})
	if err != nil {
		return err
	}
	var result TSAuthResourceIntrospection
	if err := json.Unmarshal(body, &result); err != nil {
		return oidcError("invalid_resource_response", "introspect", false, nil)
	}
	if !result.Active {
		if len(object) != 1 {
			return oidcError("invalid_resource_response", "introspect", false, nil)
		}
		*r = result
		return nil
	}
	if len(object) != 11 || result.TokenType != "Bearer" || result.Issuer == "" || result.Subject == "" || result.Audience == "" || result.ClientID == "" || result.IssuedAt <= 0 || result.ExpiresAt <= result.IssuedAt {
		return oidcError("invalid_resource_response", "introspect", false, nil)
	}
	for name, raw := range object {
		if name != "sid" && bytes.Equal(bytes.TrimSpace(raw), []byte("null")) {
			return oidcError("invalid_resource_response", "introspect", false, nil)
		}
	}
	if _, err := tsAuthResourceScopeString(result.Scope); err != nil {
		return err
	}
	if !(result.PrincipalKind == "user" && result.SessionID != nil && *result.SessionID != "" || result.PrincipalKind == "service" && result.SessionID == nil && result.Subject == result.ClientID) {
		return oidcError("invalid_resource_principal", "introspect", false, nil)
	}
	*r = result
	return nil
}

func tsAuthResourceObject(status int, body []byte, required, optional []string) (map[string]json.RawMessage, error) {
	return tsAuthExactResourceObject(status, body, required, optional, 65536)
}

func tsAuthPolicyObject(status int, body []byte, required, optional []string) (map[string]json.RawMessage, error) {
	return tsAuthExactResourceObject(status, body, required, optional, MaxResponseBodyBytes)
}

func tsAuthExactResourceObject(status int, body []byte, required, optional []string, maxBytes int64) (map[string]json.RawMessage, error) {
	if status != http.StatusOK || int64(len(body)) > maxBytes {
		return nil, oidcError("invalid_resource_response", "response", false, nil)
	}
	object, err := oidcObject(body)
	if err != nil {
		return nil, oidcError("invalid_resource_response", "response", false, nil)
	}
	for _, name := range required {
		value, ok := object[name]
		if !ok || bytes.Equal(bytes.TrimSpace(value), []byte("null")) {
			return nil, oidcError("invalid_resource_response", "response", false, nil)
		}
	}
	for name := range maps.Keys(object) {
		if !slices.Contains(required, name) && !slices.Contains(optional, name) {
			return nil, oidcError("invalid_resource_response", "response", false, nil)
		}
	}
	return object, nil
}

// Introspect checks a fresh authenticated response against local configuration
// and current caller time. The caller separately authorizes durable grants.
func (c *TSAuthResourceIntrospector) Introspect(ctx context.Context, token string) (*TSAuthResourceIntrospection, error) {
	if ctx == nil || ctx.Err() != nil || requireID("token", token) != nil {
		return nil, oidcError("invalid_request", "introspect", false, nil)
	}
	auth := TSAuthOAuthClientAuthentication{ClientID: c.config.Authentication.ClientID, ClientSecret: c.config.Authentication.Secret, Method: TSAuthClientSecretBasic}
	var result TSAuthResourceIntrospection
	if err := c.oidc.postAuthenticatedForm(ctx, "oauth/introspect", url.Values{"token": {token}}, auth, &result, "tsauth.resource.introspect"); err != nil {
		return nil, oidcOperationError("introspect", false, err)
	}
	now := time.Now().Unix()
	if result.Active && (now <= 0 || result.Issuer != c.config.Issuer || result.Audience != c.config.ResourceAudience || result.ExpiresAt <= now || result.IssuedAt > now) {
		return nil, oidcError("resource_binding_mismatch", "introspect", false, nil)
	}
	return &result, nil
}

func (r TSAuthResourceIntrospection) String() string {
	return fmt.Sprintf("TSAuthResourceIntrospection{Active:%t PrincipalKind:%s}", r.Active, r.PrincipalKind)
}

func (a TSAuthResourceAuthentication) GoString() string       { return a.String() }
func (c TSAuthResourceClientConfig) GoString() string         { return c.String() }
func (t TSAuthResourceTokens) GoString() string               { return t.String() }
func (d *TSAuthResourceDeviceAuthorization) GoString() string { return d.String() }
func (c TSAuthResourceIntrospectorConfig) GoString() string   { return c.String() }
func (*TSAuthResourceClient) String() string                  { return "TSAuthResourceClient{credentials:[REDACTED]}" }
func (c *TSAuthResourceClient) GoString() string              { return c.String() }
func (*TSAuthResourceIntrospector) String() string {
	return "TSAuthResourceIntrospector{credentials:[REDACTED]}"
}
func (c *TSAuthResourceIntrospector) GoString() string { return c.String() }
