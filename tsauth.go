package sdk

import (
	"bytes"
	"context"
	"encoding/base64"
	"encoding/json"
	"errors"
	"fmt"
	"io"
	"net/http"
	"net/url"
	"strings"
	"unicode"
)

// publicAuthBaseURL is the fixed global ts-auth origin. Every tenant issues
// from https://auth.terrascale.tech/<slug>.
const publicAuthBaseURL = "https://auth.terrascale.tech"

// TSAuthClient calls a hosted ts-auth deployment's OpenID Connect provider
// for an application that signs users in with the redirect flow (authorization
// code with PKCE). Call Instance with the tenant slug. The platform management
// API, API key tokens and device sign-in are in package
// terrascale.tech/sdk/tsauth.
type TSAuthClient struct{ c *Client }

// Instance returns the OIDC family of one ts-auth tenant slug.
func (t *TSAuthClient) Instance(slug string) (*TSAuthInstanceClient, error) {
	if err := tsAuthValidateSlug(slug); err != nil {
		return nil, err
	}
	base, err := tsAuthResolveBase(t.c.cfg)
	if err != nil {
		return nil, err
	}
	i := &TSAuthInstanceClient{c: t.c, slug: slug, base: base}
	i.OIDC = &TSAuthOIDCClient{i: i}
	return i, nil
}

func tsAuthValidateSlug(slug string) error {
	if strings.TrimSpace(slug) == "" {
		return &ValidationError{Field: "instance_slug", Message: "cannot be empty"}
	}
	if slug == "." || slug == ".." {
		return &ValidationError{Field: "instance_slug", Message: "must not be a dot segment"}
	}
	if len(slug) > 255 {
		return &ValidationError{Field: "instance_slug", Message: "must be at most 255 characters"}
	}
	for _, char := range slug {
		if unicode.IsSpace(char) || unicode.IsControl(char) || char == '/' || char == '\\' {
			return &ValidationError{Field: "instance_slug", Message: "must not contain whitespace, control characters, or path separators"}
		}
	}
	return nil
}

// tsAuthNoRedirectClient stops redirects so a response cannot move a
// credential or a replayed request to another origin.
func tsAuthNoRedirectClient(client *http.Client) *http.Client {
	copy := *client
	copy.CheckRedirect = func(*http.Request, []*http.Request) error { return http.ErrUseLastResponse }
	return &copy
}

// tsAuthResolveBase picks the ts-auth origin: the global auth host, unless
// TSAuthBaseURL overrides it for a dev or test deployment.
func tsAuthResolveBase(cfg Config) (string, error) {
	if cfg.TSAuthBaseURL == "" {
		return publicAuthBaseURL, nil
	}
	return resolveServiceBase(cfg.TSAuthBaseURL, "ts_auth_base_url", true)
}

// tsAuthAnySuccess accepts any 2xx answer without touching the body: the
// OAuth revoke route answers 200-empty (RFC 7009).
type tsAuthAnySuccess struct{}

func (tsAuthAnySuccess) decodeHTTPResponse(status int, body []byte) error { return nil }

// TSAuthInstanceClient is ts-auth for one tenant slug.
type TSAuthInstanceClient struct {
	c    *Client
	slug string
	// base is the resolved ts-auth deployment origin (never the product
	// default).
	base string

	OIDC *TSAuthOIDCClient
}

// oidc builds a public OIDC route /{slug}/{suffix}; the suffix is a fixed
// route tail (".well-known/..." or "oauth/...") and is never caller-supplied.
func (i *TSAuthInstanceClient) oidc(suffix string) string {
	return "/" + url.PathEscape(i.slug) + "/" + suffix
}

// Request pipeline: tsAuthSend applies the client's timeout, retry and
// decoding rules and routes directly to the instance's resolved ts-auth
// origin. Mutations are never retried.

// tsAuthRejectDotSegments refuses any path part that trims to "." or ".."
// (mirroring the other SDKs): a caller id must never resolve as a dot
// segment on the wire.
func tsAuthRejectDotSegments(path string) error {
	for _, segment := range strings.Split(path, "/") {
		if segment == "" {
			continue
		}
		part, err := url.PathUnescape(segment)
		if err != nil {
			continue
		}
		if trimmed := strings.TrimSpace(part); trimmed == "." || trimmed == ".." {
			return &ValidationError{Field: "path_segment", Message: fmt.Sprintf("must not be the dot segment %q", trimmed)}
		}
	}
	return nil
}

// tsAuthSend forwards a raw ts-auth request (OIDC discovery, JWKS, userinfo)
// through the instance's resolved origin.
func (i *TSAuthInstanceClient) tsAuthSend(ctx context.Context, method, path string, query url.Values, payload []byte, contentType, bearer string, out any, route string) error {
	return i.c.tsAuthSend(ctx, i.base, method, path, query, payload, contentType, bearer, out, route)
}

func (c *Client) tsAuthSend(ctx context.Context, base, method, path string, query url.Values, payload []byte, contentType, bearer string, out any, route string) error {
	return c.tsAuthSendAuthorized(ctx, base, method, path, query, payload, contentType, tsAuthAuthorization{bearer: bearer}, out, route)
}

func (c *Client) tsAuthSendAuthorized(ctx context.Context, base, method, path string, query url.Values, payload []byte, contentType string, authorization tsAuthAuthorization, out any, route string) error {
	return c.sendAuthorized(ctx, base, method, path, query, payload, contentType, authorization, out, route)
}

// sendAuthorized is the bounded request and retry pipeline shared by ts-auth
// and status calls. Mutations are never retried.
func (c *Client) sendAuthorized(ctx context.Context, base, method, path string, query url.Values, payload []byte, contentType string, authorization tsAuthAuthorization, out any, route string) error {
	if ctx == nil {
		return &ValidationError{Field: "context", Message: "cannot be nil"}
	}
	if err := tsAuthRejectDotSegments(path); err != nil {
		return err
	}
	if int64(len(payload)) > MaxRequestBodyBytes {
		return fmt.Errorf("sdk: %s request body exceeds %d bytes", route, MaxRequestBodyBytes)
	}
	requestCtx, cancel := context.WithTimeout(ctx, MaxRequestTimeout)
	defer cancel()
	retryable := tsAuthRetryable(method)
	maxRetries := maxRetryAttempts
	attempt := 0
	for {
		status, headers, respBody, err := c.tsAuthRoundTrip(requestCtx, base, method, path, query, payload, contentType, authorization, route)
		var responseErr *APIError
		if errors.As(err, &responseErr) && responseErr.Code == "response_too_large" {
			// A capped response is the server's answer. Keep its metadata
			// and never retry a body that already exceeded the protocol cap.
			return responseErr
		}
		if err != nil && responseErr == nil {
			if retryable && attempt < maxRetries && requestCtx.Err() == nil {
				if sleepErr := sleepContext(requestCtx, c.retryDelayFor(nil, attempt)); sleepErr != nil {
					return sleepErr
				}
				attempt++
				continue
			}
			return fmt.Errorf("sdk: %s %s (%s): %w", method, path, route, err)
		}
		if responseErr == nil && status >= 200 && status < 300 {
			if decoder, ok := out.(httpResponseDecoder); ok {
				return decodeSpecialResponse(decoder, status, respBody)
			}
			if len(bytes.TrimSpace(respBody)) == 0 {
				return fmt.Errorf("sdk: %s: empty response body", route)
			}
			if err := decodeSuccess(out, respBody, headers.Get("Content-Type")); err != nil {
				return fmt.Errorf("sdk: %s: invalid response body: %w", route, err)
			}
			return nil
		}
		apiErr := responseErr
		if apiErr == nil {
			apiErr = tsAuthErrorFromResponse(route, method, path, status, headers, respBody)
		}
		incompleteSuccess := responseErr != nil && status >= 200 && status < 300
		if retryable && attempt < maxRetries && (retryableStatus(status) && apiErr.Code != "quota_exceeded" || incompleteSuccess) {
			if sleepErr := waitRootRetry(requestCtx, c.retryDelayFor(headers, attempt), apiErr); sleepErr != nil {
				return sleepErr
			}
			attempt++
			continue
		}
		return apiErr
	}
}

func tsAuthRetryable(method string) bool {
	return method == http.MethodGet || method == http.MethodHead
}

func (c *Client) tsAuthRoundTrip(ctx context.Context, base, method, path string, query url.Values, payload []byte, contentType string, authorization tsAuthAuthorization, route string) (int, http.Header, []byte, error) {
	full := strings.TrimSuffix(base, "/") + path
	if len(query) > 0 {
		full += "?" + query.Encode()
	}
	var reader io.Reader
	if payload != nil {
		reader = bytes.NewReader(payload)
	}
	req, err := http.NewRequestWithContext(ctx, method, full, reader)
	if err != nil {
		return 0, nil, nil, err
	}
	h := req.Header
	h.Set("Accept", "application/json")
	if payload != nil {
		h.Set("Content-Type", contentType)
	}
	if authorization.bearer != "" {
		if err := setBearerAuthorization(h, authorization.bearer); err != nil {
			return 0, nil, nil, err
		}
	}
	if authorization.basic != "" {
		h.Set("Authorization", "Basic "+authorization.basic)
	}
	resp, err := tsAuthNoRedirectClient(c.http).Do(req)
	if err != nil {
		return 0, nil, nil, err
	}
	defer resp.Body.Close()
	if resp.ContentLength > MaxResponseBodyBytes {
		return resp.StatusCode, resp.Header, nil, responseTooLargeError(route, method, path, resp.StatusCode, resp.Header)
	}
	respBody, err := io.ReadAll(io.LimitReader(resp.Body, MaxResponseBodyBytes+1))
	if err != nil {
		return resp.StatusCode, resp.Header, nil, responseReadError(route, method, path, resp.StatusCode, resp.Header)
	}
	if int64(len(respBody)) > MaxResponseBodyBytes {
		return resp.StatusCode, resp.Header, nil, responseTooLargeError(route, method, path, resp.StatusCode, resp.Header)
	}
	return resp.StatusCode, resp.Header, respBody, nil
}

// tsAuthErrorFromResponse maps both ts-auth error shapes. errorFromResponse
// already handles the nested {"error":{"code","message"}} envelope; the OAuth
// endpoints answer with the flat {"error","error_description"} shape, which
// is folded in when no explicit code was found.
func tsAuthErrorFromResponse(route, method, path string, status int, headers http.Header, body []byte) *APIError {
	apiErr := errorFromResponse(route, method, path, status, headers, body)
	if apiErr.Code != defaultErrorCode(status) {
		return apiErr
	}
	var flat struct {
		Error            string `json:"error"`
		ErrorDescription string `json:"error_description"`
	}
	if json.Unmarshal(body, &flat) == nil && flat.Error != "" {
		apiErr.Code = flat.Error
		if flat.ErrorDescription != "" {
			apiErr.Message = flat.ErrorDescription
		}
	}
	return apiErr
}

// OIDC provider documents and responses (snake_case).

type TSAuthDiscoveryDocument struct {
	Issuer                            string   `json:"issuer"`
	AuthorizationEndpoint             string   `json:"authorization_endpoint"`
	TokenEndpoint                     string   `json:"token_endpoint"`
	UserinfoEndpoint                  string   `json:"userinfo_endpoint"`
	JwksURI                           string   `json:"jwks_uri"`
	RevocationEndpoint                string   `json:"revocation_endpoint"`
	EndSessionEndpoint                string   `json:"end_session_endpoint"`
	ResponseTypesSupported            []string `json:"response_types_supported"`
	GrantTypesSupported               []string `json:"grant_types_supported"`
	CodeChallengeMethodsSupported     []string `json:"code_challenge_methods_supported"`
	SubjectTypesSupported             []string `json:"subject_types_supported"`
	IdTokenSigningAlgValuesSupported  []string `json:"id_token_signing_alg_values_supported"`
	TokenEndpointAuthMethodsSupported []string `json:"token_endpoint_auth_methods_supported"`
	ScopesSupported                   []string `json:"scopes_supported"`
}

type TSAuthJWK struct {
	Kty              string   `json:"kty"`
	Crv              string   `json:"crv"`
	Alg              string   `json:"alg"`
	Use              string   `json:"use"`
	Kid              string   `json:"kid"`
	X                string   `json:"x"`
	Y                string   `json:"y"`
	D                string   `json:"d,omitempty"`
	KeyOps           []string `json:"key_ops,omitempty"`
	hasKeyOps        bool
	hasPrivateMember bool
}

type TSAuthJWKS struct {
	Keys []TSAuthJWK `json:"keys"`
}

type TSAuthTokenResponse struct {
	AccessToken  string  `json:"access_token"`
	TokenType    string  `json:"token_type"`
	ExpiresIn    int64   `json:"expires_in"`
	Scope        *string `json:"scope"`
	RefreshToken *string `json:"refresh_token"`
	IDToken      *string `json:"id_token"`
}

type TSAuthUserInfo struct {
	Sub           string  `json:"sub"`
	Email         *string `json:"email"`
	EmailVerified *bool   `json:"email_verified"`
	Name          *string `json:"name"`
}

type TSAuthOIDCClient struct{ i *TSAuthInstanceClient }

func (o *TSAuthOIDCClient) GetDiscoveryDocument(ctx context.Context) (*TSAuthDiscoveryDocument, error) {
	var out TSAuthDiscoveryDocument
	if err := o.i.tsAuthSend(ctx, http.MethodGet, o.i.oidc(".well-known/openid-configuration"), nil, nil, "", "", &out, "tsauth.oidc.discovery"); err != nil {
		return nil, err
	}
	return &out, nil
}

func (o *TSAuthOIDCClient) GetJwks(ctx context.Context) (*TSAuthJWKS, error) {
	var out TSAuthJWKS
	if err := o.i.tsAuthSend(ctx, http.MethodGet, o.i.oidc(".well-known/jwks.json"), nil, nil, "", "", &out, "tsauth.oidc.jwks"); err != nil {
		return nil, err
	}
	return &out, nil
}

func (o *TSAuthOIDCClient) ExchangeAuthorizationCode(ctx context.Context, authentication TSAuthOAuthClientAuthentication, code, redirectURI, codeVerifier string) (*TSAuthTokenResponse, error) {
	if err := authentication.validate(); err != nil {
		return nil, err
	}
	if err := requireID("code", code); err != nil {
		return nil, err
	}
	if err := tsAuthRequireAbsoluteURL("redirect_uri", redirectURI); err != nil {
		return nil, err
	}
	form := url.Values{}
	form.Set("grant_type", "authorization_code")
	form.Set("code", code)
	form.Set("redirect_uri", redirectURI)
	if codeVerifier != "" {
		form.Set("code_verifier", codeVerifier)
	}
	var out TSAuthTokenResponse
	if err := o.postAuthenticatedForm(ctx, "oauth/token", form, authentication, &out, "tsauth.oidc.token.code"); err != nil {
		return nil, err
	}
	return &out, nil
}

func (o *TSAuthOIDCClient) RequestClientCredentialsToken(ctx context.Context, authentication TSAuthOAuthClientAuthentication, scope string) (*TSAuthTokenResponse, error) {
	if err := authentication.validate(); err != nil {
		return nil, err
	}
	form := url.Values{}
	form.Set("grant_type", "client_credentials")
	if scope != "" {
		form.Set("scope", scope)
	}
	var out TSAuthTokenResponse
	if err := o.postAuthenticatedForm(ctx, "oauth/token", form, authentication, &out, "tsauth.oidc.token.client_credentials"); err != nil {
		return nil, err
	}
	return &out, nil
}

func (o *TSAuthOIDCClient) RefreshToken(ctx context.Context, authentication TSAuthOAuthClientAuthentication, refreshToken string) (*TSAuthTokenResponse, error) {
	if err := authentication.validate(); err != nil {
		return nil, err
	}
	if err := requireID("refresh_token", refreshToken); err != nil {
		return nil, err
	}
	form := url.Values{}
	form.Set("grant_type", "refresh_token")
	form.Set("refresh_token", refreshToken)
	var out TSAuthTokenResponse
	if err := o.postAuthenticatedForm(ctx, "oauth/token", form, authentication, &out, "tsauth.oidc.token.refresh"); err != nil {
		return nil, err
	}
	return &out, nil
}

func (o *TSAuthOIDCClient) RevokeToken(ctx context.Context, authentication TSAuthOAuthClientAuthentication, token string) error {
	if err := authentication.validate(); err != nil {
		return err
	}
	if err := requireID("token", token); err != nil {
		return err
	}
	form := url.Values{}
	form.Set("token", token)
	return o.postAuthenticatedForm(ctx, "oauth/revoke", form, authentication, tsAuthAnySuccess{}, "tsauth.oidc.token.revoke")
}

func (o *TSAuthOIDCClient) GetUserInfo(ctx context.Context, accessToken string) (*TSAuthUserInfo, error) {
	if err := requireID("access_token", accessToken); err != nil {
		return nil, err
	}
	var out TSAuthUserInfo
	if err := o.i.tsAuthSend(ctx, http.MethodGet, o.i.oidc("oauth/userinfo"), nil, nil, "", accessToken, &out, "tsauth.oidc.userinfo"); err != nil {
		return nil, err
	}
	return &out, nil
}

// URL preflight helpers.

func tsAuthRequireAbsoluteURL(field, value string) error {
	if err := requireID(field, value); err != nil {
		return err
	}
	parsed, err := url.Parse(strings.TrimSpace(value))
	if err != nil || !parsed.IsAbs() || (parsed.Scheme != "http" && parsed.Scheme != "https") || parsed.Host == "" {
		return &ValidationError{Field: field, Message: "must be an absolute http or https URL"}
	}
	return nil
}

// tsAuthAuthorization cannot be supplied by public request options.
// tsAuthAuthorization carries a request's credentials: a bearer token or
// HTTP Basic client authentication.
type tsAuthAuthorization struct{ bearer, basic string }

func (o *TSAuthOIDCClient) postAuthenticatedForm(ctx context.Context, endpoint string, form url.Values, authentication TSAuthOAuthClientAuthentication, out any, route string) error {
	if err := authentication.validate(); err != nil {
		return err
	}
	authorization := tsAuthAuthorization{}
	switch authentication.Method {
	case TSAuthClientSecretBasic:
		authorization.basic = base64.StdEncoding.EncodeToString([]byte(url.QueryEscape(authentication.ClientID) + ":" + url.QueryEscape(authentication.ClientSecret)))
	case TSAuthClientSecretPost:
		form.Set("client_id", authentication.ClientID)
		form.Set("client_secret", authentication.ClientSecret)
	case TSAuthClientAuthenticationNone:
		form.Set("client_id", authentication.ClientID)
	}
	return o.i.c.tsAuthSendAuthorized(ctx, o.i.base, http.MethodPost, o.i.oidc(endpoint), nil, []byte(form.Encode()), "application/x-www-form-urlencoded", authorization, out, route)
}

// ValidationError identifies an invalid argument. Methods return it before
// making an HTTP request, so callers can use errors.As instead of matching
// error strings.
type ValidationError struct {
	Field   string
	Message string
}

func (e *ValidationError) Error() string { return "sdk: " + e.Field + ": " + e.Message }

func requireID(name, value string) error {
	if strings.TrimSpace(value) == "" {
		return &ValidationError{Field: name, Message: "cannot be empty"}
	}
	return nil
}
