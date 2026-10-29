package sdk

import (
	"bytes"
	"context"
	"crypto/ecdsa"
	"crypto/elliptic"
	"crypto/rand"
	"crypto/sha256"
	"crypto/subtle"
	"encoding/base64"
	"encoding/json"
	"errors"
	"fmt"
	"io"
	"math/big"
	"net/http"
	"net/url"
	"slices"
	"strconv"
	"strings"
	"time"
	"unicode"
	"unicode/utf8"

	"terrascale.tech/sdk/internal/entityid"
)

// TSAuthOAuthClientAuthentication binds requests to the registered OAuth method.
// None requires an empty secret; confidential methods require a secret.
type TSAuthOAuthClientAuthentication struct {
	ClientID     string
	ClientSecret string `json:"-"`
	Method       TSAuthOAuthClientAuthenticationMethod
}

type TSAuthOAuthClientAuthenticationMethod string

const (
	TSAuthClientSecretBasic        TSAuthOAuthClientAuthenticationMethod = "client_secret_basic"
	TSAuthClientSecretPost         TSAuthOAuthClientAuthenticationMethod = "client_secret_post"
	TSAuthClientAuthenticationNone TSAuthOAuthClientAuthenticationMethod = "none"
)

func (a TSAuthOAuthClientAuthentication) String() string {
	return fmt.Sprintf("TSAuthOAuthClientAuthentication{Method:%s Secret:[REDACTED]}", a.Method)
}

func (a TSAuthOAuthClientAuthentication) GoString() string { return a.String() }

func (a TSAuthOAuthClientAuthentication) validate() error {
	if strings.TrimSpace(a.ClientID) == "" || strings.ContainsFunc(a.ClientID, unicode.IsControl) {
		return oidcError("invalid_client_configuration", "configuration", false, nil)
	}
	switch a.Method {
	case TSAuthClientSecretBasic, TSAuthClientSecretPost:
		if a.ClientSecret == "" || strings.ContainsFunc(a.ClientSecret, unicode.IsControl) {
			return oidcError("invalid_client_configuration", "configuration", false, nil)
		}
	case TSAuthClientAuthenticationNone:
		if a.ClientSecret != "" {
			return oidcError("invalid_client_configuration", "configuration", false, nil)
		}
	default:
		return oidcError("invalid_client_configuration", "configuration", false, nil)
	}
	return nil
}

// TSAuthOIDCError exposes uncertainty without logging tokens or credentials.
// An uncertain exchange or refresh must not be repeated automatically.
type TSAuthOIDCError struct {
	Code      string
	Operation string
	Uncertain bool
	cause     error
}

func (e *TSAuthOIDCError) Error() string {
	return fmt.Sprintf("sdk: OIDC %s: %s (uncertain=%t)", e.Operation, e.Code, e.Uncertain)
}
func (e *TSAuthOIDCError) Unwrap() error { return e.cause }
func oidcError(code, operation string, uncertain bool, cause error) error {
	return &TSAuthOIDCError{Code: code, Operation: operation, Uncertain: uncertain, cause: cause}
}

// TSAuthOIDCApplicationConfig is server configuration. Issuer must match the
// configured provider exactly, including slug.
type TSAuthOIDCApplicationConfig struct {
	Issuer                 string
	Authentication         TSAuthOAuthClientAuthentication
	RedirectURI            string
	PostLogoutRedirectURIs []string
	// ClockSkew may be at most one minute. Zero means no expiry grace.
	ClockSkew time.Duration
	// RequireSession rejects sessionless grants when used for browser sessions.
	RequireSession bool
}

func (c TSAuthOIDCApplicationConfig) String() string {
	return fmt.Sprintf("TSAuthOIDCApplicationConfig{Issuer:%s Authentication:[REDACTED]}", c.Issuer)
}

func (c TSAuthOIDCApplicationConfig) GoString() string { return c.String() }

type TSAuthOIDCApplication struct {
	oidc   *TSAuthOIDCClient
	config TSAuthOIDCApplicationConfig
}

// NewApplication creates the verified OIDC relying-party client. It does not
// replace application-side one-time state consumption or durable BFF sessions.
func (o *TSAuthOIDCClient) NewApplication(config TSAuthOIDCApplicationConfig) (*TSAuthOIDCApplication, error) {
	if err := config.Authentication.validate(); err != nil {
		return nil, err
	}
	issuer, err := trustedOIDCURL(config.Issuer)
	if err != nil || issuer.EscapedPath() != "/"+url.PathEscape(o.i.slug) || strings.HasSuffix(config.Issuer, "/") {
		return nil, oidcError("invalid_issuer", "configuration", false, err)
	}
	if issuer.Scheme+"://"+issuer.Host != o.i.base {
		return nil, oidcError("issuer_transport_mismatch", "configuration", false, nil)
	}
	if config.ClockSkew < 0 || config.ClockSkew > time.Minute {
		return nil, oidcError("invalid_clock_skew", "configuration", false, nil)
	}
	if err := validateTSAuthOAuthRegistration([]string{config.RedirectURI}, config.PostLogoutRedirectURIs, nil); err != nil {
		return nil, err
	}
	config.PostLogoutRedirectURIs = slices.Clone(config.PostLogoutRedirectURIs)
	return &TSAuthOIDCApplication{oidc: o, config: config}, nil
}

// Discover validates issuer and every endpoint against fixed provider paths.
// Endpoint URLs never become arbitrary request destinations.
func (a *TSAuthOIDCApplication) Discover(ctx context.Context) (*TSAuthDiscoveryDocument, error) {
	if ctx == nil {
		return nil, oidcError("invalid_context", "discovery", false, nil)
	}
	if err := ctx.Err(); err != nil {
		return nil, oidcError("request_cancelled", "discovery", false, err)
	}
	doc, err := a.oidc.GetDiscoveryDocument(ctx)
	if err != nil {
		return nil, oidcError("discovery_unavailable", "discovery", false, err)
	}
	if doc.Issuer != a.config.Issuer {
		return nil, oidcError("issuer_mismatch", "discovery", false, nil)
	}
	endpoints := map[string]string{
		"oauth/authorize": doc.AuthorizationEndpoint, "oauth/token": doc.TokenEndpoint,
		"oauth/userinfo": doc.UserinfoEndpoint, ".well-known/jwks.json": doc.JwksURI,
		"oauth/revoke": doc.RevocationEndpoint, "oauth/logout": doc.EndSessionEndpoint,
	}
	for suffix, endpoint := range endpoints {
		if endpoint != a.config.Issuer+"/"+suffix {
			return nil, oidcError("endpoint_mismatch", "discovery", false, nil)
		}
	}
	if !slices.Contains(doc.IdTokenSigningAlgValuesSupported, "ES256") || !slices.Contains(doc.ResponseTypesSupported, "code") || !slices.Contains(doc.CodeChallengeMethodsSupported, "S256") || !slices.Contains(doc.TokenEndpointAuthMethodsSupported, string(a.config.Authentication.Method)) {
		return nil, oidcError("unsupported_provider", "discovery", false, nil)
	}
	return doc, nil
}

// TSAuthAuthorizationAttempt must be held by the application's server, bound
// to its browser, and consumed once atomically before exchanging the callback.
type TSAuthAuthorizationAttempt struct {
	AuthorizationURL string
	State            string
	Nonce            string
	CodeVerifier     string
	Issuer           string
	ClientID         string
	RedirectURI      string
	ExpiresAt        time.Time
	// StartedAt is when the attempt was created; MaxAge and Prompt are the
	// requested authentication constraints. ExchangeCode enforces them on the
	// ID token's auth_time, so keep them with the attempt.
	StartedAt time.Time
	MaxAge    *time.Duration
	Prompt    string
}

// TSAuthAuthorizationOptions are optional OIDC authentication request
// parameters.
type TSAuthAuthorizationOptions struct {
	// Prompt is a space-separated list of none, login, consent and
	// select_account; none cannot be combined with another value. login
	// demands a fresh sign-in: the ID token's auth_time must not predate the
	// attempt.
	Prompt string
	// MaxAge bounds the age of the end-user authentication (whole seconds,
	// zero or more). MaxAge 0 demands a fresh sign-in, for step-up.
	MaxAge *time.Duration
}

func (o TSAuthAuthorizationOptions) validate() error {
	if o.MaxAge != nil && (*o.MaxAge < 0 || *o.MaxAge%time.Second != 0) {
		return oidcError("invalid_max_age", "authorize", false, nil)
	}
	if o.Prompt == "" {
		return nil
	}
	values := strings.Fields(o.Prompt)
	if len(values) == 0 || strings.Join(values, " ") != o.Prompt {
		return oidcError("invalid_prompt", "authorize", false, nil)
	}
	seen := map[string]bool{}
	for _, v := range values {
		switch v {
		case "none", "login", "consent", "select_account":
		default:
			return oidcError("invalid_prompt", "authorize", false, nil)
		}
		if seen[v] {
			return oidcError("invalid_prompt", "authorize", false, nil)
		}
		seen[v] = true
	}
	if seen["none"] && len(values) > 1 {
		return oidcError("invalid_prompt", "authorize", false, nil)
	}
	return nil
}

// freshLoginRequired reports whether the attempt demands an authentication
// after it started.
func (a TSAuthAuthorizationAttempt) freshLoginRequired() bool {
	return slices.Contains(strings.Fields(a.Prompt), "login")
}

func (a TSAuthAuthorizationAttempt) String() string {
	return "TSAuthAuthorizationAttempt{state,nonce,verifier:[REDACTED]}"
}

func (a TSAuthAuthorizationAttempt) GoString() string { return a.String() }

type TSAuthOIDCCallback struct{ Code, State string }

// BeginAuthorization starts an authorization code flow with PKCE, state and
// nonce. It is BeginAuthorizationWithOptions with no options.
func (a *TSAuthOIDCApplication) BeginAuthorization(ctx context.Context, scopes []string) (*TSAuthAuthorizationAttempt, error) {
	return a.BeginAuthorizationWithOptions(ctx, scopes, TSAuthAuthorizationOptions{})
}

// BeginAuthorizationWithOptions starts an authorization code flow and sends
// options.Prompt and options.MaxAge as prompt and max_age. They are stored on
// the attempt and enforced by ExchangeCode.
func (a *TSAuthOIDCApplication) BeginAuthorizationWithOptions(ctx context.Context, scopes []string, options TSAuthAuthorizationOptions) (*TSAuthAuthorizationAttempt, error) {
	if err := options.validate(); err != nil {
		return nil, err
	}
	document, err := a.Discover(ctx)
	if err != nil {
		return nil, err
	}
	if !slices.Contains(scopes, "openid") {
		return nil, oidcError("openid_scope_required", "authorize", false, nil)
	}
	for _, scope := range scopes {
		if scope == "" || strings.ContainsFunc(scope, func(r rune) bool { return unicode.IsSpace(r) || unicode.IsControl(r) }) {
			return nil, oidcError("invalid_scope", "authorize", false, nil)
		}
	}
	state, err := oidcRandom()
	if err != nil {
		return nil, err
	}
	nonce, err := oidcRandom()
	if err != nil {
		return nil, err
	}
	verifier, err := oidcRandom()
	if err != nil {
		return nil, err
	}
	endpoint, err := url.Parse(document.AuthorizationEndpoint)
	if err != nil {
		return nil, err
	}
	challenge := sha256.Sum256([]byte(verifier))
	query := endpoint.Query()
	query.Set("client_id", a.config.Authentication.ClientID)
	query.Set("redirect_uri", a.config.RedirectURI)
	query.Set("response_type", "code")
	query.Set("scope", strings.Join(scopes, " "))
	query.Set("state", state)
	query.Set("nonce", nonce)
	query.Set("code_challenge", base64.RawURLEncoding.EncodeToString(challenge[:]))
	query.Set("code_challenge_method", "S256")
	var maxAge *time.Duration
	if options.MaxAge != nil {
		v := *options.MaxAge
		maxAge = &v
		query.Set("max_age", strconv.FormatInt(int64(v/time.Second), 10))
	}
	if options.Prompt != "" {
		query.Set("prompt", options.Prompt)
	}
	endpoint.RawQuery = query.Encode()
	now := time.Now()
	return &TSAuthAuthorizationAttempt{AuthorizationURL: endpoint.String(), State: state, Nonce: nonce, CodeVerifier: verifier, Issuer: a.config.Issuer, ClientID: a.config.Authentication.ClientID, RedirectURI: a.config.RedirectURI, ExpiresAt: now.Add(10 * time.Minute), StartedAt: now, MaxAge: maxAge, Prompt: options.Prompt}, nil
}

func oidcRandom() (string, error) {
	var b [32]byte
	if _, err := rand.Read(b[:]); err != nil {
		return "", oidcError("random_unavailable", "authorize", false, err)
	}
	return base64.RawURLEncoding.EncodeToString(b[:]), nil
}

// TSAuthVerifiedIDToken is identity evidence for this relying party. It is
// never an access credential for database, product, or MCP operations.
type TSAuthVerifiedIDToken struct {
	Issuer    string
	Subject   string
	Audience  []string
	ExpiresAt time.Time
	IssuedAt  time.Time
	Nonce     string
	// AuthTime is the end-user authentication time (auth_time), when sent.
	AuthTime      *time.Time
	SessionID     *string
	Email         *string
	EmailVerified *bool
	Name          *string
}

// TSAuthIDTokenVerificationOptions bind an ID token to its authorization
// attempt. When MaxAge is set, the token must carry auth_time no older than
// MaxAge before AuthorizationStartedAt (or before now when that is zero);
// MaxAge 0 therefore demands an authentication after the attempt started.
type TSAuthIDTokenVerificationOptions struct {
	Nonce, AccessToken     string
	MaxAge                 *time.Duration
	AuthorizationStartedAt time.Time
}
type TSAuthOIDCLogin struct {
	Tokens   TSAuthTokenResponse
	Identity TSAuthVerifiedIDToken
}

func (l TSAuthOIDCLogin) String() string { return "TSAuthOIDCLogin{Tokens:[REDACTED]}" }

func (l TSAuthOIDCLogin) GoString() string { return l.String() }

func (a *TSAuthOIDCApplication) ExchangeCode(ctx context.Context, callback TSAuthOIDCCallback, attempt TSAuthAuthorizationAttempt) (*TSAuthOIDCLogin, error) {
	if (attempt.MaxAge != nil || attempt.freshLoginRequired()) && attempt.StartedAt.IsZero() {
		return nil, oidcError("invalid_authorization_attempt", "exchange", false, nil)
	}
	if attempt.Issuer != a.config.Issuer || attempt.ClientID != a.config.Authentication.ClientID || attempt.RedirectURI != a.config.RedirectURI || !time.Now().Before(attempt.ExpiresAt) || attempt.Nonce == "" || !validPKCEVerifier(attempt.CodeVerifier) || callback.State == "" || subtle.ConstantTimeCompare([]byte(callback.State), []byte(attempt.State)) != 1 {
		return nil, oidcError("invalid_authorization_attempt", "exchange", false, nil)
	}
	if _, err := a.Discover(ctx); err != nil {
		return nil, err
	}
	tokens, err := a.oidc.ExchangeAuthorizationCode(ctx, a.config.Authentication, callback.Code, a.config.RedirectURI, attempt.CodeVerifier)
	if err != nil {
		return nil, oidcOperationError("exchange", true, err)
	}
	if tokens.IDToken == nil || *tokens.IDToken == "" || !strings.EqualFold(tokens.TokenType, "Bearer") || tokens.AccessToken == "" || tokens.ExpiresIn <= 0 {
		return nil, oidcError("invalid_token_response", "exchange", true, nil)
	}
	verify := TSAuthIDTokenVerificationOptions{Nonce: attempt.Nonce, AccessToken: tokens.AccessToken, MaxAge: attempt.MaxAge, AuthorizationStartedAt: attempt.StartedAt}
	if attempt.freshLoginRequired() && (verify.MaxAge == nil || *verify.MaxAge > 0) {
		zero := time.Duration(0)
		verify.MaxAge = &zero
	}
	identity, err := a.VerifyIDToken(ctx, *tokens.IDToken, verify)
	if err != nil {
		return nil, oidcError("invalid_id_token", "exchange", true, err)
	}
	return &TSAuthOIDCLogin{Tokens: *tokens, Identity: *identity}, nil
}

func validPKCEVerifier(value string) bool {
	if len(value) < 43 || len(value) > 128 {
		return false
	}
	return !strings.ContainsFunc(value, func(r rune) bool {
		return !(r >= 'a' && r <= 'z' || r >= 'A' && r <= 'Z' || r >= '0' && r <= '9' || strings.ContainsRune("-._~", r))
	})
}

func (a *TSAuthOIDCApplication) Refresh(ctx context.Context, refreshToken string) (*TSAuthTokenResponse, error) {
	if _, err := a.Discover(ctx); err != nil {
		return nil, err
	}
	result, err := a.oidc.RefreshToken(ctx, a.config.Authentication, refreshToken)
	if err != nil {
		return nil, oidcOperationError("refresh", true, err)
	}
	if result.AccessToken == "" || result.RefreshToken == nil || *result.RefreshToken == "" || !strings.EqualFold(result.TokenType, "Bearer") || result.ExpiresIn <= 0 {
		return nil, oidcError("invalid_token_response", "refresh", true, nil)
	}
	return result, nil
}

func (a *TSAuthOIDCApplication) Revoke(ctx context.Context, token string) error {
	if _, err := a.Discover(ctx); err != nil {
		return err
	}
	return oidcOperationError("revoke", true, a.oidc.RevokeToken(ctx, a.config.Authentication, token))
}

// EndSessionURL accepts only an exact URL in the configured registration.
// The provider revokes its cookie session; it does not support id_token_hint.
func (a *TSAuthOIDCApplication) EndSessionURL(ctx context.Context, redirectURI, state string) (string, error) {
	if !slices.Contains(a.config.PostLogoutRedirectURIs, redirectURI) || state == "" || strings.ContainsFunc(state, unicode.IsControl) {
		return "", oidcError("unregistered_logout_redirect", "logout", false, nil)
	}
	document, err := a.Discover(ctx)
	if err != nil {
		return "", err
	}
	endpoint, err := url.Parse(document.EndSessionEndpoint)
	if err != nil {
		return "", err
	}
	query := endpoint.Query()
	query.Set("client_id", a.config.Authentication.ClientID)
	query.Set("post_logout_redirect_uri", redirectURI)
	query.Set("state", state)
	endpoint.RawQuery = query.Encode()
	return endpoint.String(), nil
}

func oidcOperationError(operation string, mutation bool, err error) error {
	if err == nil {
		return nil
	}
	var validation *ValidationError
	if errors.As(err, &validation) {
		return oidcError("invalid_request", operation, false, err)
	}
	var api *APIError
	if errors.As(err, &api) {
		incompleteSuccess := api.Status >= 200 && api.Status < 300 && api.Code == "response_read_failed"
		return oidcError(api.Code, operation, mutation && (api.Status >= 500 || api.Status == http.StatusRequestTimeout || api.Code == "response_too_large" || incompleteSuccess), err)
	}
	return oidcError("transport_unavailable", operation, mutation, err)
}

func trustedOIDCURL(raw string) (*url.URL, error) {
	u, err := url.Parse(raw)
	if err != nil || u.Host == "" || u.User != nil || u.RawQuery != "" || u.Fragment != "" || u.Opaque != "" || (u.Scheme != "https" && !(u.Scheme == "http" && oidcLoopback(u.Hostname()))) || strings.EqualFold(u.Hostname(), "api.terrascale.tech") {
		return nil, oidcError("untrusted_endpoint", "configuration", false, err)
	}
	return u, nil
}
func trustedOIDCRedirect(raw string) (*url.URL, error) {
	u, err := url.Parse(raw)
	if err != nil || u == nil {
		return nil, oidcError("untrusted_endpoint", "registration", false, err)
	}
	query := u.RawQuery
	u.RawQuery = ""
	if _, err := trustedOIDCURL(u.String()); err != nil {
		return nil, err
	}
	u.RawQuery = query
	return u, nil
}
func oidcLoopback(host string) bool {
	return host == "localhost" || host == "127.0.0.1" || host == "::1"
}

func validateTSAuthOAuthRegistration(redirects, logout, origins []string) error {
	if len(redirects) < 1 || len(redirects) > 100 || len(logout) > 100 || len(origins) > 20 {
		return oidcError("invalid_registration", "registration", false, nil)
	}
	for _, uri := range append(slices.Clone(redirects), logout...) {
		if _, err := trustedOIDCRedirect(uri); err != nil {
			return oidcError("invalid_redirect_uri", "registration", false, err)
		}
	}
	for _, origin := range origins {
		u, err := trustedOIDCURL(origin)
		if err != nil || (u.Path != "" && u.Path != "/") {
			return oidcError("invalid_allowed_origin", "registration", false, err)
		}
	}
	return nil
}

func (a *TSAuthOIDCApplication) VerifyIDToken(ctx context.Context, token string, options TSAuthIDTokenVerificationOptions) (*TSAuthVerifiedIDToken, error) {
	if _, err := a.Discover(ctx); err != nil {
		return nil, err
	}
	if options.Nonce == "" {
		return nil, oidcError("nonce_required", "verify_id_token", false, nil)
	}
	if options.MaxAge != nil && *options.MaxAge < 0 {
		return nil, oidcError("invalid_max_age", "verify_id_token", false, nil)
	}
	if len(token) > 65536 {
		return nil, oidcError("invalid_id_token", "verify_id_token", false, nil)
	}
	segments := strings.Split(token, ".")
	if len(segments) != 3 {
		return nil, oidcError("invalid_id_token", "verify_id_token", false, nil)
	}
	headerBytes, err := base64.RawURLEncoding.Strict().DecodeString(segments[0])
	if err != nil {
		return nil, oidcError("invalid_id_token", "verify_id_token", false, nil)
	}
	payload, err := base64.RawURLEncoding.Strict().DecodeString(segments[1])
	if err != nil {
		return nil, oidcError("invalid_id_token", "verify_id_token", false, nil)
	}
	signature, err := base64.RawURLEncoding.Strict().DecodeString(segments[2])
	if err != nil || len(signature) != 64 {
		return nil, oidcError("invalid_signature", "verify_id_token", false, err)
	}
	header, err := oidcObject(headerBytes)
	if err != nil {
		return nil, err
	}
	var alg, typ, kid string
	if json.Unmarshal(header["alg"], &alg) != nil || json.Unmarshal(header["typ"], &typ) != nil || json.Unmarshal(header["kid"], &kid) != nil || alg != "ES256" || typ != "JWT" || kid == "" || len(kid) > 256 {
		return nil, oidcError("invalid_token_header", "verify_id_token", false, nil)
	}
	for _, extension := range []string{"crit", "jku", "jwk", "x5u", "b64"} {
		if _, exists := header[extension]; exists {
			return nil, oidcError("unsupported_token_header", "verify_id_token", false, nil)
		}
	}
	_, err = oidcObject(payload)
	if err != nil {
		return nil, err
	}
	keys, err := a.oidc.GetJwks(ctx)
	if err != nil {
		return nil, oidcError("jwks_unavailable", "verify_id_token", false, err)
	}
	if len(keys.Keys) < 1 || len(keys.Keys) > 100 {
		return nil, oidcError("invalid_jwks", "verify_id_token", false, nil)
	}
	var key *TSAuthJWK
	seen := map[string]bool{}
	for _, candidate := range keys.Keys {
		if candidate.Kid == "" || seen[candidate.Kid] {
			return nil, oidcError("ambiguous_jwks", "verify_id_token", false, nil)
		}
		seen[candidate.Kid] = true
		if candidate.Kid == kid {
			k := candidate
			key = &k
		}
	}
	if key == nil || key.Kty != "EC" || key.Crv != "P-256" || key.Alg != "ES256" || key.Use != "sig" || key.D != "" || key.hasPrivateMember || (key.hasKeyOps && !slices.Contains(key.KeyOps, "verify")) {
		return nil, oidcError("invalid_signing_key", "verify_id_token", false, nil)
	}
	x, err := base64.RawURLEncoding.Strict().DecodeString(key.X)
	if err != nil || len(x) != 32 {
		return nil, oidcError("invalid_signing_key", "verify_id_token", false, err)
	}
	y, err := base64.RawURLEncoding.Strict().DecodeString(key.Y)
	if err != nil || len(y) != 32 {
		return nil, oidcError("invalid_signing_key", "verify_id_token", false, err)
	}
	public := ecdsa.PublicKey{Curve: elliptic.P256(), X: new(big.Int).SetBytes(x), Y: new(big.Int).SetBytes(y)}
	if !public.Curve.IsOnCurve(public.X, public.Y) {
		return nil, oidcError("invalid_signing_key", "verify_id_token", false, nil)
	}
	digest := sha256.Sum256([]byte(segments[0] + "." + segments[1]))
	if !ecdsa.Verify(&public, digest[:], new(big.Int).SetBytes(signature[:32]), new(big.Int).SetBytes(signature[32:])) {
		return nil, oidcError("invalid_signature", "verify_id_token", false, nil)
	}
	var c struct {
		Issuer          string          `json:"iss"`
		Subject         string          `json:"sub"`
		Audience        json.RawMessage `json:"aud"`
		Expires         int64           `json:"exp"`
		Issued          int64           `json:"iat"`
		NotBefore       *int64          `json:"nbf"`
		Nonce           string          `json:"nonce"`
		TokenUse        string          `json:"token_use"`
		AuthorizedParty *string         `json:"azp"`
		SessionID       *string         `json:"sid"`
		AuthTime        *int64          `json:"auth_time"`
		AccessHash      *string         `json:"at_hash"`
		Email           *string         `json:"email"`
		EmailVerified   *bool           `json:"email_verified"`
		Name            *string         `json:"name"`
	}
	if oidcDecodeExactJSON(payload, &c, []string{"iss", "sub", "aud", "exp", "iat", "nbf", "nonce", "token_use", "azp", "sid", "auth_time", "at_hash", "email", "email_verified", "name"}) != nil {
		return nil, oidcError("invalid_claims", "verify_id_token", false, nil)
	}
	var audiences []string
	var audience string
	if json.Unmarshal(c.Audience, &audience) == nil {
		audiences = []string{audience}
	} else if json.Unmarshal(c.Audience, &audiences) != nil {
		return nil, oidcError("invalid_audience", "verify_id_token", false, nil)
	}
	if c.Issuer != a.config.Issuer || entityid.Check(c.Subject) != nil || c.TokenUse != "id" || !slices.Contains(audiences, a.config.Authentication.ClientID) || (len(audiences) > 1 && (c.AuthorizedParty == nil || *c.AuthorizedParty != a.config.Authentication.ClientID)) || (c.AuthorizedParty != nil && *c.AuthorizedParty != a.config.Authentication.ClientID) {
		return nil, oidcError("invalid_identity_claims", "verify_id_token", false, nil)
	}
	unique := map[string]bool{}
	for _, aud := range audiences {
		if aud == "" || unique[aud] {
			return nil, oidcError("invalid_audience", "verify_id_token", false, nil)
		}
		unique[aud] = true
	}
	now := time.Now()
	skew := int64(a.config.ClockSkew / time.Second)
	if c.Expires <= 0 || c.Issued <= 0 || c.Expires <= c.Issued || c.Expires <= now.Unix()-skew || c.Issued > now.Unix()+skew || (c.NotBefore != nil && *c.NotBefore > now.Unix()+skew) {
		return nil, oidcError("token_expired_or_not_yet_valid", "verify_id_token", false, nil)
	}
	if subtle.ConstantTimeCompare([]byte(c.Nonce), []byte(options.Nonce)) != 1 {
		return nil, oidcError("nonce_mismatch", "verify_id_token", false, nil)
	}
	if (c.SessionID != nil && entityid.Check(*c.SessionID) != nil) || (a.config.RequireSession && c.SessionID == nil) {
		return nil, oidcError("invalid_session_binding", "verify_id_token", false, nil)
	}
	if c.AuthTime != nil && (*c.AuthTime <= 0 || *c.AuthTime > now.Unix()+skew) {
		return nil, oidcError("invalid_auth_time", "verify_id_token", false, nil)
	}
	if options.MaxAge != nil {
		reference := options.AuthorizationStartedAt
		if reference.IsZero() {
			reference = now
		}
		// auth_time has whole-second precision; the attempt start is
		// truncated so a sign-in in the same second counts as fresh.
		oldest := reference.Truncate(time.Second).Unix() - int64(*options.MaxAge/time.Second) - skew
		if c.AuthTime == nil || *c.AuthTime < oldest {
			return nil, oidcError("authentication_too_old", "verify_id_token", false, nil)
		}
	}
	if options.AccessToken != "" {
		hash := sha256.Sum256([]byte(options.AccessToken))
		expected := base64.RawURLEncoding.EncodeToString(hash[:16])
		if c.AccessHash == nil || subtle.ConstantTimeCompare([]byte(*c.AccessHash), []byte(expected)) != 1 {
			return nil, oidcError("access_token_hash_mismatch", "verify_id_token", false, nil)
		}
	}
	var authTime *time.Time
	if c.AuthTime != nil {
		at := time.Unix(*c.AuthTime, 0)
		authTime = &at
	}
	return &TSAuthVerifiedIDToken{Issuer: c.Issuer, Subject: c.Subject, Audience: audiences, ExpiresAt: time.Unix(c.Expires, 0), IssuedAt: time.Unix(c.Issued, 0), Nonce: c.Nonce, AuthTime: authTime, SessionID: c.SessionID, Email: c.Email, EmailVerified: c.EmailVerified, Name: c.Name}, nil
}

// Duplicate JSON member names cannot alter the signed claim interpretation.
func oidcObject(data []byte) (map[string]json.RawMessage, error) {
	if !utf8.Valid(data) {
		return nil, oidcError("invalid_token_json", "verify_id_token", false, nil)
	}
	decoder := json.NewDecoder(bytes.NewReader(data))
	decoder.UseNumber()
	if err := oidcJSONValue(decoder, 0); err != nil {
		return nil, oidcError("invalid_token_json", "verify_id_token", false, err)
	}
	if _, err := decoder.Token(); err != io.EOF {
		return nil, oidcError("invalid_token_json", "verify_id_token", false, err)
	}
	var object map[string]json.RawMessage
	if err := json.Unmarshal(data, &object); err != nil || object == nil {
		return nil, oidcError("invalid_token_json", "verify_id_token", false, err)
	}
	return object, nil
}
func oidcJSONValue(decoder *json.Decoder, depth int) error {
	if depth > 16 {
		return errors.New("JSON nesting limit")
	}
	token, err := decoder.Token()
	if err != nil {
		return err
	}
	delimiter, ok := token.(json.Delim)
	if !ok {
		return nil
	}
	switch delimiter {
	case '{':
		names := map[string]bool{}
		for decoder.More() {
			name, err := decoder.Token()
			if err != nil {
				return err
			}
			key, ok := name.(string)
			if !ok || names[key] {
				return errors.New("duplicate JSON member")
			}
			names[key] = true
			if err := oidcJSONValue(decoder, depth+1); err != nil {
				return err
			}
		}
	case '[':
		for decoder.More() {
			if err := oidcJSONValue(decoder, depth+1); err != nil {
				return err
			}
		}
	default:
		return errors.New("invalid JSON delimiter")
	}
	_, err = decoder.Token()
	return err
}

func (r TSAuthTokenResponse) String() string { return "TSAuthTokenResponse{credentials:[REDACTED]}" }

func (r TSAuthTokenResponse) GoString() string { return r.String() }

func oidcDecodeExactJSON(data []byte, target any, allowed []string) error {
	object, err := oidcObject(data)
	if err != nil {
		return err
	}
	exact := make(map[string]json.RawMessage, len(allowed))
	for _, name := range allowed {
		if value, ok := object[name]; ok {
			exact[name] = value
		}
	}
	canonical, err := json.Marshal(exact)
	if err != nil {
		return err
	}
	return json.Unmarshal(canonical, target)
}

func (d *TSAuthDiscoveryDocument) decodeHTTPResponse(status int, body []byte) error {
	if status != http.StatusOK {
		return oidcError("unexpected_status", "response", false, nil)
	}
	return oidcDecodeExactJSON(body, d, []string{"issuer", "authorization_endpoint", "token_endpoint", "userinfo_endpoint", "jwks_uri", "revocation_endpoint", "end_session_endpoint", "response_types_supported", "grant_types_supported", "code_challenge_methods_supported", "subject_types_supported", "id_token_signing_alg_values_supported", "token_endpoint_auth_methods_supported", "scopes_supported"})
}
func (r *TSAuthTokenResponse) decodeHTTPResponse(status int, body []byte) error {
	if status != http.StatusOK {
		return oidcError("unexpected_status", "response", false, nil)
	}
	return oidcDecodeExactJSON(body, r, []string{"access_token", "token_type", "expires_in", "scope", "refresh_token", "id_token"})
}
func (r *TSAuthJWKS) decodeHTTPResponse(status int, body []byte) error {
	if status != http.StatusOK {
		return oidcError("unexpected_status", "response", false, nil)
	}
	object, err := oidcObject(body)
	if err != nil {
		return err
	}
	var keys []json.RawMessage
	if err := json.Unmarshal(object["keys"], &keys); err != nil || len(keys) < 1 || len(keys) > 100 {
		return oidcError("invalid_jwks", "jwks", false, err)
	}
	result := make([]TSAuthJWK, 0, len(keys))
	for _, raw := range keys {
		members, err := oidcObject(raw)
		if err != nil {
			return err
		}
		var key TSAuthJWK
		if err := oidcDecodeExactJSON(raw, &key, []string{"kty", "crv", "alg", "use", "kid", "x", "y", "key_ops"}); err != nil {
			return err
		}
		_, key.hasKeyOps = members["key_ops"]
		for _, private := range []string{"d", "p", "q", "dp", "dq", "qi", "oth", "k"} {
			if _, exists := members[private]; exists {
				key.hasPrivateMember = true
			}
		}
		result = append(result, key)
	}
	r.Keys = result
	return nil
}

func (r *TSAuthUserInfo) decodeHTTPResponse(status int, body []byte) error {
	if status != http.StatusOK {
		return oidcError("unexpected_status", "userinfo", false, nil)
	}
	if err := oidcDecodeExactJSON(body, r, []string{"sub", "email", "email_verified", "name"}); err != nil {
		return err
	}
	if err := entityid.Check(r.Sub); err != nil {
		return oidcError("invalid_identity_claims", "userinfo", false, nil)
	}
	return nil
}
