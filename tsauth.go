package sdk

import (
	"bytes"
	"context"
	"crypto/hmac"
	"crypto/sha256"
	"crypto/subtle"
	"encoding/hex"
	"encoding/json"
	"errors"
	"fmt"
	"io"
	"net"
	"net/http"
	"net/url"
	"strconv"
	"strings"
	"time"
	"unicode"
)

// TSAuthClient calls a hosted ts-auth identity-provider deployment. Construct
// a Client with TSAuthBaseURL set to the deployment's public origin (or a
// non-default APIBaseURL) and AccessToken set to the instance sk_ management
// key: a custom origin keeps discovery routing off, so every request goes
// directly to the configured origin. All routes are scoped by instance slug;
// call Instance to obtain the family clients. The OIDC family authenticates
// with OAuth client credentials instead of the sk_ key. These routes are not
// part of the TerraBase producer contract.
type TSAuthClient struct{ c *Client }

// Instance returns the management and OIDC families scoped to one ts-auth
// instance slug. The slug must match the sk_ key's instance.
func (t *TSAuthClient) Instance(slug string) (*TSAuthInstanceClient, error) {
	if err := tsAuthValidateSlug(slug); err != nil {
		return nil, err
	}
	base, err := tsAuthResolveBase(t.c.cfg)
	if err != nil {
		return nil, err
	}
	i := &TSAuthInstanceClient{c: t.c, slug: slug, base: base, v1Prefix: "/v1/" + url.PathEscape(slug), bearer: tsAuthBearer(t.c.cfg)}
	i.Users = &TSAuthUsersClient{i: i}
	i.Sessions = &TSAuthSessionsClient{i: i}
	i.APIKeys = &TSAuthAPIKeysClient{i: i}
	i.Organizations = &TSAuthOrganizationsClient{i: i}
	i.OAuthClients = &TSAuthOAuthClientsClient{i: i}
	i.Connections = &TSAuthConnectionsClient{i: i}
	i.Directories = &TSAuthDirectoriesClient{i: i}
	i.JWTTemplates = &TSAuthJWTTemplatesClient{i: i}
	i.Webhooks = &TSAuthWebhooksClient{i: i}
	i.AuditEvents = &TSAuthAuditEventsClient{i: i}
	i.Billing = &TSAuthBillingClient{i: i}
	i.Instance = &TSAuthInstanceFamilyClient{i: i}
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

// tsAuthResolveBase picks the ts-auth deployment origin: TSAuthBaseURL when
// configured, else a non-default APIBaseURL. The default product origin never
// serves ts-auth routes, so silently using it is rejected instead.
func tsAuthResolveBase(cfg Config) (string, error) {
	const required = "ts-auth requires Config.TSAuthBaseURL (or an explicit non-default Config.APIBaseURL) pointing at the ts-auth deployment origin."
	field := "ts_auth_base_url"
	raw := strings.TrimSpace(cfg.TSAuthBaseURL)
	if raw == "" {
		raw = strings.TrimSpace(cfg.APIBaseURL)
		if raw == "" || strings.EqualFold(strings.TrimSuffix(raw, "/"), DefaultAPIBaseURL) {
			return "", &ValidationError{Field: field, Message: required}
		}
		field = "api_base_url"
	}
	base := strings.TrimSuffix(raw, "/")
	u, err := url.Parse(base + "/")
	if err != nil || (u.Scheme != "http" && u.Scheme != "https") || u.Host == "" {
		return "", &ValidationError{Field: field, Message: "must be an absolute http or https URL"}
	}
	if u.RawQuery != "" || u.Fragment != "" {
		return "", &ValidationError{Field: field, Message: "must not contain a query or fragment"}
	}
	if u.User != nil {
		return "", &ValidationError{Field: field, Message: "must not contain userinfo"}
	}
	if u.Scheme == "http" && !tsAuthDevTransport(u.Hostname()) {
		return "", &ValidationError{Field: field, Message: "must use HTTPS outside loopback and .test dev origins"}
	}
	return base, nil
}

// tsAuthAnySuccess accepts any 2xx answer without touching the body: the
// OAuth revoke route answers 200-empty (RFC 7009), unlike the management
// no-content routes that must answer exactly 204.
type tsAuthAnySuccess struct{}

func (tsAuthAnySuccess) decodeHTTPResponse(status int, body []byte) error { return nil }

// tsAuthDevTransport mirrors the plain-HTTP rule: loopback always, plus the
// .test dev origins (reserved TLD, never routable) like the other SDKs.
func tsAuthDevTransport(hostname string) bool {
	host := strings.ToLower(hostname)
	if host == "localhost" {
		return true
	}
	if ip := net.ParseIP(host); ip != nil && ip.IsLoopback() {
		return true
	}
	return host == "test" || strings.HasSuffix(host, ".test")
}

// tsAuthBearer caches the formatted static credential once per instance; a
// key that cannot be formatted is kept raw so the failure still surfaces at
// request time. A TokenProvider keeps per-request resolution.
func tsAuthBearer(cfg Config) string {
	if cfg.TokenProvider != nil {
		return ""
	}
	if formatted, err := bearerAuthorization(cfg.AccessToken); err == nil {
		return formatted
	}
	return cfg.AccessToken
}

// TSAuthInstanceClient exposes every ts-auth family for one instance slug.
type TSAuthInstanceClient struct {
	c    *Client
	slug string
	// base is the resolved ts-auth deployment origin (never the product
	// default). v1Prefix and bearer cache the per-request hot strings.
	base     string
	v1Prefix string
	bearer   string

	Users         *TSAuthUsersClient
	Sessions      *TSAuthSessionsClient
	APIKeys       *TSAuthAPIKeysClient
	Organizations *TSAuthOrganizationsClient
	OAuthClients  *TSAuthOAuthClientsClient
	Connections   *TSAuthConnectionsClient
	Directories   *TSAuthDirectoriesClient
	JWTTemplates  *TSAuthJWTTemplatesClient
	Webhooks      *TSAuthWebhooksClient
	AuditEvents   *TSAuthAuditEventsClient
	Billing       *TSAuthBillingClient
	Instance      *TSAuthInstanceFamilyClient
	OIDC          *TSAuthOIDCClient
}

// v1 builds a management route /v1/{slug}/{parts...}. Parts that trim to a
// dot segment are rejected by the send pipeline (tsAuthRejectDotSegments).
func (i *TSAuthInstanceClient) v1(parts ...string) string {
	segments := make([]string, 0, len(parts)+2)
	segments = append(segments, i.v1Prefix)
	for _, part := range parts {
		segments = append(segments, url.PathEscape(part))
	}
	return strings.Join(segments, "/")
}

// oidc builds a public OIDC route /{slug}/{suffix}; the suffix is a fixed
// route tail (".well-known/..." or "oauth/...") and is never caller-supplied.
func (i *TSAuthInstanceClient) oidc(suffix string) string {
	return "/" + url.PathEscape(i.slug) + "/" + suffix
}

// Request pipeline. ts-auth routes live outside the public /api/v1 contract,
// so they never enter Client.do: tsAuthSend mirrors do's timeout, retry, and
// decoding behavior while authenticating and routing directly to the
// instance's resolved ts-auth origin (no discovery, no placement repair, no
// idempotency contract — mutations are never retried).

func tsAuthRoute(path string) bool {
	return strings.HasPrefix(path, "/v1/") ||
		strings.Contains(path, "/.well-known/") ||
		strings.Contains(path, "/oauth/")
}

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

// tsAuthDo performs one ts-auth management call: JSON body, sk_ bearer auth.
func (i *TSAuthInstanceClient) tsAuthDo(ctx context.Context, method, path string, query url.Values, body any, out any, route string) error {
	if ctx == nil {
		return fmt.Errorf("sdk: request context cannot be nil")
	}
	if !tsAuthRoute(path) {
		return fmt.Errorf("sdk: path %q is outside the ts-auth routes", path)
	}
	var payload []byte
	if body != nil {
		raw, err := json.Marshal(body)
		if err != nil {
			return fmt.Errorf("sdk: marshal %s request body: %w", route, err)
		}
		payload = raw
	}
	return i.c.tsAuthSend(ctx, i.base, method, path, query, payload, "application/json", i.token(), out, route)
}

// tsAuthPostForm performs one OIDC form post (client_secret_post): the client
// credentials travel as form fields, so no bearer header is attached.
func (i *TSAuthInstanceClient) tsAuthPostForm(ctx context.Context, path string, form url.Values, out any, route string) error {
	if ctx == nil {
		return fmt.Errorf("sdk: request context cannot be nil")
	}
	if !tsAuthRoute(path) {
		return fmt.Errorf("sdk: path %q is outside the ts-auth routes", path)
	}
	return i.c.tsAuthSend(ctx, i.base, http.MethodPost, path, nil, []byte(form.Encode()), "application/x-www-form-urlencoded", "", out, route)
}

// tsAuthSend forwards a raw ts-auth request (OIDC discovery, JWKS, userinfo)
// through the instance's resolved origin.
func (i *TSAuthInstanceClient) tsAuthSend(ctx context.Context, method, path string, query url.Values, payload []byte, contentType, bearer string, out any, route string) error {
	return i.c.tsAuthSend(ctx, i.base, method, path, query, payload, contentType, bearer, out, route)
}

// token resolves the bearer credential: the cached formatted static key, or
// the per-request provider value when a TokenProvider is configured.
func (i *TSAuthInstanceClient) token() string {
	if i.c.cfg.TokenProvider == nil {
		return i.bearer
	}
	if t := i.c.cfg.TokenProvider(); t != "" {
		return t
	}
	return i.c.cfg.AccessToken
}

func (c *Client) tsAuthSend(ctx context.Context, base, method, path string, query url.Values, payload []byte, contentType, bearer string, out any, route string) error {
	if err := tsAuthRejectDotSegments(path); err != nil {
		return err
	}
	if int64(len(payload)) > MaxRequestBodyBytes {
		return fmt.Errorf("sdk: %s request body exceeds %d bytes", route, MaxRequestBodyBytes)
	}
	requestCtx := ctx
	cancel := func() {}
	if timeout := c.cfg.RequestTimeout; timeout > 0 {
		requestCtx, cancel = context.WithTimeout(ctx, timeout)
	}
	defer cancel()
	bases := []string{base}
	if base == "https://auth.terrascale.tech" && !c.cfg.TSAuthUseReverseProxy {
		var err error
		bases, err = c.authDiscovery.resolve(requestCtx, c.http, c.cfg.DiscoveryBaseURL)
		if err != nil {
			return fmt.Errorf("sdk: auth endpoint discovery: %w", err)
		}
	}
	regionIndex := 0
	attempt := 0
	for {
		status, headers, respBody, err := c.tsAuthRoundTrip(requestCtx, bases[regionIndex], method, path, query, payload, contentType, bearer, route)
		var oversized *APIError
		if errors.As(err, &oversized) {
			// A capped response is the server's answer, not a transport
			// failure: keep its status and never retry it.
			return oversized
		}
		if err != nil {
			if tsAuthRetryable(method) && regionIndex < len(bases)-1 && requestCtx.Err() == nil {
				regionIndex++
				continue
			}
			if tsAuthRetryable(method) && attempt < c.cfg.MaxRetryAttempts && requestCtx.Err() == nil {
				if sleepErr := sleepContext(requestCtx, c.retryDelayFor(nil, attempt)); sleepErr != nil {
					return sleepErr
				}
				attempt++
				continue
			}
			return fmt.Errorf("sdk: %s %s (%s): %w", method, path, route, err)
		}
		if status >= 200 && status < 300 {
			if out == nil {
				// No-content calls answer exactly 204; anything else breaks
				// the no-content contract.
				if status != http.StatusNoContent {
					return fmt.Errorf("sdk: %s: expected HTTP 204, got %d", route, status)
				}
				return nil
			}
			if decoder, ok := out.(interface{ decodeHTTPResponse(int, []byte) error }); ok {
				return decoder.decodeHTTPResponse(status, respBody)
			}
			if len(bytes.TrimSpace(respBody)) == 0 {
				return fmt.Errorf("sdk: %s: empty response body", route)
			}
			if err := decodeSuccess(out, respBody, headers.Get("Content-Type")); err != nil {
				return fmt.Errorf("sdk: %s: invalid response body: %w", route, err)
			}
			return nil
		}
		apiErr := tsAuthErrorFromResponse(route, method, path, status, headers, respBody)
		if tsAuthRetryable(method) && regionIndex < len(bases)-1 &&
			(status == http.StatusBadGateway || status == http.StatusServiceUnavailable || status == http.StatusGatewayTimeout) {
			regionIndex++
			continue
		}
		if tsAuthRetryable(method) && attempt < c.cfg.MaxRetryAttempts && retryableStatuses[status] {
			if sleepErr := sleepContext(requestCtx, c.retryDelayFor(headers, attempt)); sleepErr != nil {
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

// tsAuthTooLargeError reports a response body above the protocol cap while
// keeping the observed status.
func tsAuthTooLargeError(route string, status int) *APIError {
	return &APIError{
		Route:   route,
		Status:  status,
		Code:    "response_too_large",
		Message: fmt.Sprintf("response body exceeds %d bytes", MaxResponseBodyBytes),
	}
}

func (c *Client) tsAuthRoundTrip(ctx context.Context, base, method, path string, query url.Values, payload []byte, contentType, bearer, route string) (int, http.Header, []byte, error) {
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
	if bearer != "" {
		if err := setBearerAuthorization(h, bearer); err != nil {
			return 0, nil, nil, err
		}
	}
	resp, err := tsAuthNoRedirectClient(c.http).Do(req)
	if err != nil {
		return 0, nil, nil, err
	}
	defer resp.Body.Close()
	if resp.ContentLength > MaxResponseBodyBytes {
		return 0, nil, nil, tsAuthTooLargeError(route, resp.StatusCode)
	}
	respBody, err := io.ReadAll(io.LimitReader(resp.Body, MaxResponseBodyBytes+1))
	if err != nil {
		return 0, nil, nil, err
	}
	if int64(len(respBody)) > MaxResponseBodyBytes {
		return 0, nil, nil, tsAuthTooLargeError(route, resp.StatusCode)
	}
	return resp.StatusCode, resp.Header, respBody, nil
}

// tsAuthErrorFromResponse maps both ts-auth error shapes. errorFromResponse
// already handles the nested management envelope; the OAuth endpoints answer
// with the flat {"error","error_description"} shape, which is folded in when
// no explicit code was found.
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

// Users family (snake_case; list items omit ban_reason and private_metadata).

type TSAuthUser struct {
	ID              string         `json:"id"`
	Email           *string        `json:"email"`
	EmailVerified   bool           `json:"email_verified"`
	Username        *string        `json:"username"`
	Name            *string        `json:"name"`
	Banned          bool           `json:"banned"`
	BanReason       *string        `json:"ban_reason"`
	CreatedAt       int64          `json:"created_at"`
	LastSignInAt    *int64         `json:"last_sign_in_at"`
	PublicMetadata  map[string]any `json:"public_metadata"`
	PrivateMetadata map[string]any `json:"private_metadata"`
}

type TSAuthUserPage struct {
	Data []TSAuthUser `json:"data"`
}

type CreateTSAuthUserRequest struct {
	Email    string `json:"email,omitempty"`
	Username string `json:"username,omitempty"`
	Password string `json:"password,omitempty"`
	Name     string `json:"name,omitempty"`
}

// TSAuthUserPatch builds the tri-state users PATCH body: a field configured
// with WithX is sent as a value, WithoutX sends JSON null (unset), and a
// field never configured is omitted entirely (unchanged). An empty patch is
// rejected before any request is sent.
type TSAuthUserPatch struct {
	fields map[string]any
}

func (p TSAuthUserPatch) set(field string, value any) TSAuthUserPatch {
	fields := make(map[string]any, len(p.fields)+1)
	for name, existing := range p.fields {
		fields[name] = existing
	}
	fields[field] = value
	return TSAuthUserPatch{fields: fields}
}

func (p TSAuthUserPatch) WithName(value string) TSAuthUserPatch {
	return p.set("name", value)
}

func (p TSAuthUserPatch) UnsetName() TSAuthUserPatch { return p.set("name", nil) }

func (p TSAuthUserPatch) WithUsername(value string) TSAuthUserPatch {
	return p.set("username", value)
}

func (p TSAuthUserPatch) UnsetUsername() TSAuthUserPatch { return p.set("username", nil) }

func (p TSAuthUserPatch) WithPassword(value string) TSAuthUserPatch {
	return p.set("password", value)
}

func (p TSAuthUserPatch) UnsetPassword() TSAuthUserPatch { return p.set("password", nil) }

// WithEmail sets the email; the contract has no email unset (null is 400).
func (p TSAuthUserPatch) WithEmail(value string) TSAuthUserPatch {
	return p.set("email", value)
}

func (p TSAuthUserPatch) WithBanned(value bool) TSAuthUserPatch {
	return p.set("banned", value)
}

func (p TSAuthUserPatch) WithBanReason(value string) TSAuthUserPatch {
	return p.set("ban_reason", value)
}

func (p TSAuthUserPatch) UnsetBanReason() TSAuthUserPatch { return p.set("ban_reason", nil) }

func (p TSAuthUserPatch) WithPublicMetadata(value map[string]any) TSAuthUserPatch {
	return p.set("public_metadata", value)
}

func (p TSAuthUserPatch) UnsetPublicMetadata() TSAuthUserPatch {
	return p.set("public_metadata", nil)
}

func (p TSAuthUserPatch) WithPrivateMetadata(value map[string]any) TSAuthUserPatch {
	return p.set("private_metadata", value)
}

func (p TSAuthUserPatch) UnsetPrivateMetadata() TSAuthUserPatch {
	return p.set("private_metadata", nil)
}

func (p TSAuthUserPatch) body() (map[string]any, error) {
	if len(p.fields) == 0 {
		return nil, &ValidationError{Field: "patch", Message: "must configure at least one field"}
	}
	return p.fields, nil
}

type TSAuthSession struct {
	ID           string  `json:"id"`
	Status       string  `json:"status"`
	CreatedAt    int64   `json:"created_at"`
	ExpiresAt    int64   `json:"expires_at"`
	LastActiveAt *int64  `json:"last_active_at"`
	UserAgent    *string `json:"user_agent"`
	IPAddress    *string `json:"ip_address"`
}

type TSAuthImpersonation struct {
	Token     string `json:"token"`
	ExpiresAt int64  `json:"expires_at"`
}

type TSAuthMFAFactor struct {
	ID        string `json:"id"`
	Type      string `json:"type"`
	Confirmed bool   `json:"confirmed"`
	CreatedAt int64  `json:"createdAt"`
}

type TSAuthRecoveryCodes []string

type TSAuthPasskey struct {
	ID         string `json:"id"`
	Nickname   string `json:"nickname"`
	Algorithm  string `json:"algorithm"`
	CreatedAt  int64  `json:"createdAt"`
	LastUsedAt *int64 `json:"lastUsedAt"`
}

type TSAuthUserOrganization struct {
	ID           string              `json:"id"`
	Role         string              `json:"role"`
	CreatedAt    int64               `json:"created_at"`
	Organization *TSAuthOrganization `json:"organization"`
}

type tsAuthDataEnvelope[T any] struct {
	Data []T `json:"data"`
}

type TSAuthUsersClient struct{ i *TSAuthInstanceClient }

func (u *TSAuthUsersClient) List(ctx context.Context, limit uint32, startingAfter string) (*TSAuthUserPage, error) {
	query := url.Values{}
	if limit > 0 {
		query.Set("limit", strconv.FormatUint(uint64(limit), 10))
	}
	if strings.TrimSpace(startingAfter) != "" {
		query.Set("starting_after", startingAfter)
	}
	var out TSAuthUserPage
	if err := u.i.tsAuthDo(ctx, http.MethodGet, u.i.v1("users"), query, nil, &out, "tsauth.users.list"); err != nil {
		return nil, err
	}
	return &out, nil
}

// ListAll walks the cursor (last item id) until the server returns an empty
// page, a page shorter than the requested page size, or a repeated cursor.
func (u *TSAuthUsersClient) ListAll(ctx context.Context, pageSize uint32) ([]TSAuthUser, error) {
	var users []TSAuthUser
	var startingAfter string
	for {
		page, err := u.List(ctx, pageSize, startingAfter)
		if err != nil {
			return nil, err
		}
		if len(page.Data) == 0 {
			return users, nil
		}
		users = append(users, page.Data...)
		next := page.Data[len(page.Data)-1].ID
		if next == startingAfter || (pageSize > 0 && uint32(len(page.Data)) < pageSize) {
			return users, nil
		}
		startingAfter = next
	}
}

func (u *TSAuthUsersClient) Create(ctx context.Context, request CreateTSAuthUserRequest) (*TSAuthUser, error) {
	var out TSAuthUser
	if err := u.i.tsAuthDo(ctx, http.MethodPost, u.i.v1("users"), nil, request, &out, "tsauth.users.create"); err != nil {
		return nil, err
	}
	return &out, nil
}

func (u *TSAuthUsersClient) Get(ctx context.Context, userID string) (*TSAuthUser, error) {
	if err := requireID("user_id", userID); err != nil {
		return nil, err
	}
	var out TSAuthUser
	if err := u.i.tsAuthDo(ctx, http.MethodGet, u.i.v1("users", userID), nil, nil, &out, "tsauth.users.get"); err != nil {
		return nil, err
	}
	return &out, nil
}

func (u *TSAuthUsersClient) Update(ctx context.Context, userID string, patch TSAuthUserPatch) (*TSAuthUser, error) {
	if err := requireID("user_id", userID); err != nil {
		return nil, err
	}
	body, err := patch.body()
	if err != nil {
		return nil, err
	}
	var out TSAuthUser
	if err := u.i.tsAuthDo(ctx, http.MethodPatch, u.i.v1("users", userID), nil, body, &out, "tsauth.users.update"); err != nil {
		return nil, err
	}
	return &out, nil
}

func (u *TSAuthUsersClient) Delete(ctx context.Context, userID string) error {
	if err := requireID("user_id", userID); err != nil {
		return err
	}
	return u.i.tsAuthDo(ctx, http.MethodDelete, u.i.v1("users", userID), nil, nil, nil, "tsauth.users.delete")
}

func (u *TSAuthUsersClient) Impersonate(ctx context.Context, userID string) (*TSAuthImpersonation, error) {
	if err := requireID("user_id", userID); err != nil {
		return nil, err
	}
	var out TSAuthImpersonation
	if err := u.i.tsAuthDo(ctx, http.MethodPost, u.i.v1("users", userID, "impersonate"), nil, nil, &out, "tsauth.users.impersonate"); err != nil {
		return nil, err
	}
	return &out, nil
}

func (u *TSAuthUsersClient) ListSessions(ctx context.Context, userID string) ([]TSAuthSession, error) {
	if err := requireID("user_id", userID); err != nil {
		return nil, err
	}
	var out tsAuthDataEnvelope[TSAuthSession]
	if err := u.i.tsAuthDo(ctx, http.MethodGet, u.i.v1("users", userID, "sessions"), nil, nil, &out, "tsauth.users.sessions.list"); err != nil {
		return nil, err
	}
	return out.Data, nil
}

func (u *TSAuthUsersClient) RevokeAllSessions(ctx context.Context, userID string) error {
	if err := requireID("user_id", userID); err != nil {
		return err
	}
	return u.i.tsAuthDo(ctx, http.MethodPost, u.i.v1("users", userID, "sessions", "revoke-all"), nil, nil, nil, "tsauth.users.sessions.revoke_all")
}

func (u *TSAuthUsersClient) ListMFAFactors(ctx context.Context, userID string) ([]TSAuthMFAFactor, error) {
	if err := requireID("user_id", userID); err != nil {
		return nil, err
	}
	var out tsAuthDataEnvelope[TSAuthMFAFactor]
	if err := u.i.tsAuthDo(ctx, http.MethodGet, u.i.v1("users", userID, "mfa-factors"), nil, nil, &out, "tsauth.users.mfa.list"); err != nil {
		return nil, err
	}
	return out.Data, nil
}

func (u *TSAuthUsersClient) DeleteMFAFactor(ctx context.Context, userID, factorID string) error {
	if err := requireID("user_id", userID); err != nil {
		return err
	}
	if err := requireID("factor_id", factorID); err != nil {
		return err
	}
	return u.i.tsAuthDo(ctx, http.MethodDelete, u.i.v1("users", userID, "mfa-factors", factorID), nil, nil, nil, "tsauth.users.mfa.delete")
}

func (u *TSAuthUsersClient) RegenerateRecoveryCodes(ctx context.Context, userID, factorID string) (TSAuthRecoveryCodes, error) {
	if err := requireID("user_id", userID); err != nil {
		return nil, err
	}
	if err := requireID("factor_id", factorID); err != nil {
		return nil, err
	}
	var out tsAuthDataEnvelope[string]
	if err := u.i.tsAuthDo(ctx, http.MethodPost, u.i.v1("users", userID, "mfa-factors", factorID, "regenerate-recovery"), nil, nil, &out, "tsauth.users.mfa.regen_recovery"); err != nil {
		return nil, err
	}
	return TSAuthRecoveryCodes(out.Data), nil
}

func (u *TSAuthUsersClient) ListPasskeys(ctx context.Context, userID string) ([]TSAuthPasskey, error) {
	if err := requireID("user_id", userID); err != nil {
		return nil, err
	}
	var out struct {
		Passkeys []TSAuthPasskey `json:"passkeys"`
	}
	if err := u.i.tsAuthDo(ctx, http.MethodGet, u.i.v1("users", userID, "passkeys"), nil, nil, &out, "tsauth.users.passkeys.list"); err != nil {
		return nil, err
	}
	return out.Passkeys, nil
}

func (u *TSAuthUsersClient) DeletePasskey(ctx context.Context, userID, passkeyID string) error {
	if err := requireID("user_id", userID); err != nil {
		return err
	}
	if err := requireID("passkey_id", passkeyID); err != nil {
		return err
	}
	return u.i.tsAuthDo(ctx, http.MethodDelete, u.i.v1("users", userID, "passkeys", passkeyID), nil, nil, nil, "tsauth.users.passkeys.delete")
}

func (u *TSAuthUsersClient) ListOrganizations(ctx context.Context, userID string) ([]TSAuthUserOrganization, error) {
	if err := requireID("user_id", userID); err != nil {
		return nil, err
	}
	var out []TSAuthUserOrganization
	if err := u.i.tsAuthDo(ctx, http.MethodGet, u.i.v1("users", userID, "organizations"), nil, nil, &out, "tsauth.users.organizations.list"); err != nil {
		return nil, err
	}
	return out, nil
}

// Sessions family.

type TSAuthSessionsClient struct{ i *TSAuthInstanceClient }

func (s *TSAuthSessionsClient) Revoke(ctx context.Context, sessionID string) error {
	if err := requireID("session_id", sessionID); err != nil {
		return err
	}
	return s.i.tsAuthDo(ctx, http.MethodPost, s.i.v1("sessions", sessionID, "revoke"), nil, nil, nil, "tsauth.sessions.revoke")
}

// API keys family (snake_case; api_key is one-time, returned only by Create).

type TSAuthAPIKey struct {
	ID         string   `json:"id"`
	Name       string   `json:"name"`
	APIKey     string   `json:"api_key,omitempty"`
	Scopes     []string `json:"scopes"`
	CreatedAt  int64    `json:"created_at"`
	LastUsedAt *int64   `json:"last_used_at"`
	Revoked    bool     `json:"revoked"`
}

type CreateTSAuthAPIKeyRequest struct {
	Name   string   `json:"name"`
	Scopes []string `json:"scopes,omitempty"`
}

type TSAuthAPIKeysClient struct{ i *TSAuthInstanceClient }

func (a *TSAuthAPIKeysClient) Create(ctx context.Context, request CreateTSAuthAPIKeyRequest) (*TSAuthAPIKey, error) {
	if strings.TrimSpace(request.Name) == "" {
		return nil, &ValidationError{Field: "name", Message: "must be nonblank"}
	}
	var out TSAuthAPIKey
	if err := a.i.tsAuthDo(ctx, http.MethodPost, a.i.v1("api-keys"), nil, request, &out, "tsauth.api_keys.create"); err != nil {
		return nil, err
	}
	return &out, nil
}

func (a *TSAuthAPIKeysClient) List(ctx context.Context) ([]TSAuthAPIKey, error) {
	var out tsAuthDataEnvelope[TSAuthAPIKey]
	if err := a.i.tsAuthDo(ctx, http.MethodGet, a.i.v1("api-keys"), nil, nil, &out, "tsauth.api_keys.list"); err != nil {
		return nil, err
	}
	return out.Data, nil
}

func (a *TSAuthAPIKeysClient) Delete(ctx context.Context, keyID string) error {
	if err := requireID("key_id", keyID); err != nil {
		return err
	}
	return a.i.tsAuthDo(ctx, http.MethodDelete, a.i.v1("api-keys", keyID), nil, nil, nil, "tsauth.api_keys.delete")
}

// Organizations family (snake_case, BARE lists).

type TSAuthOrganization struct {
	ID        string `json:"id"`
	Name      string `json:"name"`
	Slug      string `json:"slug"`
	CreatedAt int64  `json:"created_at"`
}

type CreateTSAuthOrganizationRequest struct {
	Name string `json:"name"`
	Slug string `json:"slug,omitempty"`
}

type TSAuthMembershipUser struct {
	ID    string `json:"id"`
	Email string `json:"email"`
	Name  string `json:"name"`
}

type TSAuthMembership struct {
	ID             string                `json:"id"`
	OrganizationID string                `json:"organization_id"`
	UserID         string                `json:"user_id"`
	Role           string                `json:"role"`
	CreatedAt      int64                 `json:"created_at"`
	User           *TSAuthMembershipUser `json:"user"`
}

type TSAuthInvitation struct {
	ID             string `json:"id"`
	OrganizationID string `json:"organization_id"`
	Email          string `json:"email"`
	Role           string `json:"role"`
	Status         string `json:"status"`
	ExpiresAt      int64  `json:"expires_at"`
	CreatedAt      int64  `json:"created_at"`
}

type TSAuthOrgDomain struct {
	ID             string `json:"id"`
	OrganizationID string `json:"organization_id"`
	Domain         string `json:"domain"`
	CreatedAt      int64  `json:"created_at"`
}

type TSAuthDomainEnrollment struct {
	OrgID string `json:"org_id"`
}

type TSAuthOrganizationsClient struct{ i *TSAuthInstanceClient }

func (o *TSAuthOrganizationsClient) Create(ctx context.Context, request CreateTSAuthOrganizationRequest) (*TSAuthOrganization, error) {
	if strings.TrimSpace(request.Name) == "" {
		return nil, &ValidationError{Field: "name", Message: "must be nonblank"}
	}
	var out TSAuthOrganization
	if err := o.i.tsAuthDo(ctx, http.MethodPost, o.i.v1("organizations"), nil, request, &out, "tsauth.organizations.create"); err != nil {
		return nil, err
	}
	return &out, nil
}

func (o *TSAuthOrganizationsClient) List(ctx context.Context) ([]TSAuthOrganization, error) {
	var out []TSAuthOrganization
	if err := o.i.tsAuthDo(ctx, http.MethodGet, o.i.v1("organizations"), nil, nil, &out, "tsauth.organizations.list"); err != nil {
		return nil, err
	}
	return out, nil
}

func (o *TSAuthOrganizationsClient) Get(ctx context.Context, orgID string) (*TSAuthOrganization, error) {
	if err := requireID("org_id", orgID); err != nil {
		return nil, err
	}
	var out TSAuthOrganization
	if err := o.i.tsAuthDo(ctx, http.MethodGet, o.i.v1("organizations", orgID), nil, nil, &out, "tsauth.organizations.get"); err != nil {
		return nil, err
	}
	return &out, nil
}

func (o *TSAuthOrganizationsClient) Rename(ctx context.Context, orgID, name string) (*TSAuthOrganization, error) {
	if err := requireID("org_id", orgID); err != nil {
		return nil, err
	}
	if strings.TrimSpace(name) == "" {
		return nil, &ValidationError{Field: "name", Message: "must be nonblank"}
	}
	body := struct {
		Name string `json:"name"`
	}{Name: name}
	var out TSAuthOrganization
	if err := o.i.tsAuthDo(ctx, http.MethodPatch, o.i.v1("organizations", orgID), nil, body, &out, "tsauth.organizations.rename"); err != nil {
		return nil, err
	}
	return &out, nil
}

func (o *TSAuthOrganizationsClient) Delete(ctx context.Context, orgID string) error {
	if err := requireID("org_id", orgID); err != nil {
		return err
	}
	return o.i.tsAuthDo(ctx, http.MethodDelete, o.i.v1("organizations", orgID), nil, nil, nil, "tsauth.organizations.delete")
}

func (o *TSAuthOrganizationsClient) AddMember(ctx context.Context, orgID, userID, role string) (*TSAuthMembership, error) {
	if err := requireID("org_id", orgID); err != nil {
		return nil, err
	}
	if err := requireID("user_id", userID); err != nil {
		return nil, err
	}
	if err := requireID("role", role); err != nil {
		return nil, err
	}
	body := struct {
		UserID string `json:"user_id"`
		Role   string `json:"role"`
	}{UserID: userID, Role: role}
	var out TSAuthMembership
	if err := o.i.tsAuthDo(ctx, http.MethodPost, o.i.v1("organizations", orgID, "memberships"), nil, body, &out, "tsauth.organizations.members.add"); err != nil {
		return nil, err
	}
	return &out, nil
}

func (o *TSAuthOrganizationsClient) ListMemberships(ctx context.Context, orgID string) ([]TSAuthMembership, error) {
	if err := requireID("org_id", orgID); err != nil {
		return nil, err
	}
	var out []TSAuthMembership
	if err := o.i.tsAuthDo(ctx, http.MethodGet, o.i.v1("organizations", orgID, "memberships"), nil, nil, &out, "tsauth.organizations.members.list"); err != nil {
		return nil, err
	}
	return out, nil
}

func (o *TSAuthOrganizationsClient) UpdateMemberRole(ctx context.Context, orgID, membershipID, role string) (*TSAuthMembership, error) {
	if err := requireID("org_id", orgID); err != nil {
		return nil, err
	}
	if err := requireID("membership_id", membershipID); err != nil {
		return nil, err
	}
	if err := requireID("role", role); err != nil {
		return nil, err
	}
	body := struct {
		Role string `json:"role"`
	}{Role: role}
	var out TSAuthMembership
	if err := o.i.tsAuthDo(ctx, http.MethodPatch, o.i.v1("organizations", orgID, "memberships", membershipID), nil, body, &out, "tsauth.organizations.members.update_role"); err != nil {
		return nil, err
	}
	return &out, nil
}

func (o *TSAuthOrganizationsClient) RemoveMember(ctx context.Context, orgID, membershipID string) error {
	if err := requireID("org_id", orgID); err != nil {
		return err
	}
	if err := requireID("membership_id", membershipID); err != nil {
		return err
	}
	return o.i.tsAuthDo(ctx, http.MethodDelete, o.i.v1("organizations", orgID, "memberships", membershipID), nil, nil, nil, "tsauth.organizations.members.remove")
}

func (o *TSAuthOrganizationsClient) CreateInvitation(ctx context.Context, orgID, email, role string) (*TSAuthInvitation, error) {
	if err := requireID("org_id", orgID); err != nil {
		return nil, err
	}
	if strings.TrimSpace(email) == "" {
		return nil, &ValidationError{Field: "email", Message: "must be nonblank"}
	}
	if err := requireID("role", role); err != nil {
		return nil, err
	}
	body := struct {
		Email string `json:"email"`
		Role  string `json:"role"`
	}{Email: email, Role: role}
	var out TSAuthInvitation
	if err := o.i.tsAuthDo(ctx, http.MethodPost, o.i.v1("organizations", orgID, "invitations"), nil, body, &out, "tsauth.organizations.invitations.create"); err != nil {
		return nil, err
	}
	return &out, nil
}

func (o *TSAuthOrganizationsClient) ListInvitations(ctx context.Context, orgID string) ([]TSAuthInvitation, error) {
	if err := requireID("org_id", orgID); err != nil {
		return nil, err
	}
	var out []TSAuthInvitation
	if err := o.i.tsAuthDo(ctx, http.MethodGet, o.i.v1("organizations", orgID, "invitations"), nil, nil, &out, "tsauth.organizations.invitations.list"); err != nil {
		return nil, err
	}
	return out, nil
}

func (o *TSAuthOrganizationsClient) RevokeInvitation(ctx context.Context, orgID, invitationID string) (*TSAuthInvitation, error) {
	if err := requireID("org_id", orgID); err != nil {
		return nil, err
	}
	if err := requireID("invitation_id", invitationID); err != nil {
		return nil, err
	}
	var out TSAuthInvitation
	if err := o.i.tsAuthDo(ctx, http.MethodPost, o.i.v1("organizations", orgID, "invitations", invitationID, "revoke"), nil, nil, &out, "tsauth.organizations.invitations.revoke"); err != nil {
		return nil, err
	}
	return &out, nil
}

func (o *TSAuthOrganizationsClient) AddDomain(ctx context.Context, orgID, domain string) (*TSAuthOrgDomain, error) {
	if err := requireID("org_id", orgID); err != nil {
		return nil, err
	}
	if err := requireID("domain", domain); err != nil {
		return nil, err
	}
	body := struct {
		Domain string `json:"domain"`
	}{Domain: domain}
	var out TSAuthOrgDomain
	if err := o.i.tsAuthDo(ctx, http.MethodPost, o.i.v1("organizations", orgID, "domains"), nil, body, &out, "tsauth.organizations.domains.add"); err != nil {
		return nil, err
	}
	return &out, nil
}

func (o *TSAuthOrganizationsClient) ListDomains(ctx context.Context, orgID string) ([]TSAuthOrgDomain, error) {
	if err := requireID("org_id", orgID); err != nil {
		return nil, err
	}
	var out []TSAuthOrgDomain
	if err := o.i.tsAuthDo(ctx, http.MethodGet, o.i.v1("organizations", orgID, "domains"), nil, nil, &out, "tsauth.organizations.domains.list"); err != nil {
		return nil, err
	}
	return out, nil
}

func (o *TSAuthOrganizationsClient) RemoveDomain(ctx context.Context, orgID, domainID string) error {
	if err := requireID("org_id", orgID); err != nil {
		return err
	}
	if err := requireID("domain_id", domainID); err != nil {
		return err
	}
	return o.i.tsAuthDo(ctx, http.MethodDelete, o.i.v1("organizations", orgID, "domains", domainID), nil, nil, nil, "tsauth.organizations.domains.remove")
}

func (o *TSAuthOrganizationsClient) EnrollDomainUser(ctx context.Context, domain, userID string) (*TSAuthDomainEnrollment, error) {
	if err := requireID("domain", domain); err != nil {
		return nil, err
	}
	if err := requireID("user_id", userID); err != nil {
		return nil, err
	}
	body := struct {
		UserID string `json:"user_id"`
	}{UserID: userID}
	var out TSAuthDomainEnrollment
	if err := o.i.tsAuthDo(ctx, http.MethodPost, o.i.v1("domains", domain, "enroll"), nil, body, &out, "tsauth.organizations.domains.enroll"); err != nil {
		return nil, err
	}
	return &out, nil
}

// OAuth clients family: request snake_case, response camelCase. The
// client_secret is one-time, returned only by Create (null for public/none).

type CreateTSAuthOAuthClientRequest struct {
	Name                    string   `json:"name"`
	RedirectURIs            []string `json:"redirect_uris"`
	Public                  *bool    `json:"public,omitempty"`
	TokenEndpointAuthMethod string   `json:"token_endpoint_auth_method,omitempty"`
}

type TSAuthOAuthClient struct {
	ID                      string   `json:"id"`
	ClientID                string   `json:"clientId"`
	Name                    string   `json:"name"`
	RedirectURIs            []string `json:"redirectUris"`
	TokenEndpointAuthMethod string   `json:"tokenEndpointAuthMethod"`
	Public                  bool     `json:"public"`
	CreatedAt               int64    `json:"createdAt"`
}

type TSAuthOAuthClientSecret struct {
	Client       TSAuthOAuthClient `json:"client"`
	ClientSecret *string           `json:"client_secret"`
}

type TSAuthOAuthClientsClient struct{ i *TSAuthInstanceClient }

func (o *TSAuthOAuthClientsClient) Create(ctx context.Context, request CreateTSAuthOAuthClientRequest) (*TSAuthOAuthClientSecret, error) {
	if strings.TrimSpace(request.Name) == "" {
		return nil, &ValidationError{Field: "name", Message: "must be nonblank"}
	}
	if len(request.RedirectURIs) == 0 {
		return nil, &ValidationError{Field: "redirect_uris", Message: "must not be empty"}
	}
	var out TSAuthOAuthClientSecret
	if err := o.i.tsAuthDo(ctx, http.MethodPost, o.i.v1("oauth-clients"), nil, request, &out, "tsauth.oauth_clients.create"); err != nil {
		return nil, err
	}
	return &out, nil
}

func (o *TSAuthOAuthClientsClient) List(ctx context.Context) ([]TSAuthOAuthClient, error) {
	var out struct {
		Clients []TSAuthOAuthClient `json:"clients"`
	}
	if err := o.i.tsAuthDo(ctx, http.MethodGet, o.i.v1("oauth-clients"), nil, nil, &out, "tsauth.oauth_clients.list"); err != nil {
		return nil, err
	}
	return out.Clients, nil
}

// Delete removes one OAuth client by its clt_ record id (not the OAuth
// protocol clientId).
func (o *TSAuthOAuthClientsClient) Delete(ctx context.Context, recordID string) error {
	if err := requireID("id", recordID); err != nil {
		return err
	}
	return o.i.tsAuthDo(ctx, http.MethodDelete, o.i.v1("oauth-clients", recordID), nil, nil, nil, "tsauth.oauth_clients.delete")
}

// Connections family (snake_case; secrets are never returned).

type CreateTSAuthOIDCConnectionRequest struct {
	Type                  string   `json:"type"`
	Name                  string   `json:"name"`
	OrgID                 string   `json:"org_id,omitempty"`
	IssuerURL             string   `json:"issuer_url"`
	ClientID              string   `json:"client_id"`
	ClientSecret          string   `json:"client_secret"`
	Scopes                []string `json:"scopes,omitempty"`
	AuthorizationEndpoint string   `json:"authorization_endpoint,omitempty"`
	TokenEndpoint         string   `json:"token_endpoint,omitempty"`
	UserinfoEndpoint      string   `json:"userinfo_endpoint,omitempty"`
}

type CreateTSAuthSAMLConnectionRequest struct {
	Type               string `json:"type"`
	Name               string `json:"name"`
	OrgID              string `json:"org_id,omitempty"`
	IdpEntityID        string `json:"idp_entity_id"`
	SSORedirectURL     string `json:"sso_redirect_url"`
	X509CertificatePEM string `json:"x509_certificate_pem"`
}

type UpdateTSAuthConnectionRequest struct {
	Enabled      *bool   `json:"enabled,omitempty"`
	Name         *string `json:"name,omitempty"`
	ClientSecret *string `json:"client_secret,omitempty"`
}

type TSAuthConnection struct {
	ID                    string  `json:"id"`
	Type                  string  `json:"type"`
	Name                  string  `json:"name"`
	Enabled               bool    `json:"enabled"`
	OrgID                 *string `json:"org_id"`
	IssuerURL             *string `json:"issuer_url"`
	ClientID              *string `json:"client_id"`
	AuthorizationEndpoint *string `json:"authorization_endpoint"`
	TokenEndpoint         *string `json:"token_endpoint"`
	UserinfoEndpoint      *string `json:"userinfo_endpoint"`
	IdpEntityID           *string `json:"idp_entity_id"`
	SSORedirectURL        *string `json:"sso_redirect_url"`
	SPEntityID            *string `json:"sp_entity_id"`
	CreatedAt             int64   `json:"created_at"`
}

type TSAuthConnectionsClient struct{ i *TSAuthInstanceClient }

func (c *TSAuthConnectionsClient) CreateOIDC(ctx context.Context, request CreateTSAuthOIDCConnectionRequest) (*TSAuthConnection, error) {
	if strings.TrimSpace(request.Name) == "" {
		return nil, &ValidationError{Field: "name", Message: "must be nonblank"}
	}
	if err := tsAuthRequireAbsoluteURL("issuer_url", request.IssuerURL); err != nil {
		return nil, err
	}
	if err := requireID("client_id", request.ClientID); err != nil {
		return nil, err
	}
	if err := requireID("client_secret", request.ClientSecret); err != nil {
		return nil, err
	}
	request.Type = "oidc"
	var out TSAuthConnection
	if err := c.i.tsAuthDo(ctx, http.MethodPost, c.i.v1("connections"), nil, request, &out, "tsauth.connections.create_oidc"); err != nil {
		return nil, err
	}
	return &out, nil
}

func (c *TSAuthConnectionsClient) CreateSAML(ctx context.Context, request CreateTSAuthSAMLConnectionRequest) (*TSAuthConnection, error) {
	if strings.TrimSpace(request.Name) == "" {
		return nil, &ValidationError{Field: "name", Message: "must be nonblank"}
	}
	if err := requireID("idp_entity_id", request.IdpEntityID); err != nil {
		return nil, err
	}
	if err := tsAuthRequireAbsoluteURL("sso_redirect_url", request.SSORedirectURL); err != nil {
		return nil, err
	}
	if err := requireID("x509_certificate_pem", request.X509CertificatePEM); err != nil {
		return nil, err
	}
	request.Type = "saml"
	var out TSAuthConnection
	if err := c.i.tsAuthDo(ctx, http.MethodPost, c.i.v1("connections"), nil, request, &out, "tsauth.connections.create_saml"); err != nil {
		return nil, err
	}
	return &out, nil
}

func (c *TSAuthConnectionsClient) List(ctx context.Context) ([]TSAuthConnection, error) {
	var out tsAuthDataEnvelope[TSAuthConnection]
	if err := c.i.tsAuthDo(ctx, http.MethodGet, c.i.v1("connections"), nil, nil, &out, "tsauth.connections.list"); err != nil {
		return nil, err
	}
	return out.Data, nil
}

func (c *TSAuthConnectionsClient) Update(ctx context.Context, connectionID string, request UpdateTSAuthConnectionRequest) (*TSAuthConnection, error) {
	if err := requireID("connection_id", connectionID); err != nil {
		return nil, err
	}
	var out TSAuthConnection
	if err := c.i.tsAuthDo(ctx, http.MethodPatch, c.i.v1("connections", connectionID), nil, request, &out, "tsauth.connections.update"); err != nil {
		return nil, err
	}
	return &out, nil
}

func (c *TSAuthConnectionsClient) Delete(ctx context.Context, connectionID string) error {
	if err := requireID("connection_id", connectionID); err != nil {
		return err
	}
	return c.i.tsAuthDo(ctx, http.MethodDelete, c.i.v1("connections", connectionID), nil, nil, nil, "tsauth.connections.delete")
}

// Directories family (snake_case; the scim token is one-time, returned only
// by Create and RotateToken).

type TSAuthDirectory struct {
	ID        string  `json:"id"`
	OrgID     *string `json:"org_id"`
	Token     string  `json:"token,omitempty"`
	CreatedAt int64   `json:"created_at"`
}

// String redacts the one-time scim token so %+v logging cannot leak it.
func (d TSAuthDirectory) String() string {
	return fmt.Sprintf("TSAuthDirectory{ID:%s OrgID:%v Token:[REDACTED] CreatedAt:%d}", d.ID, d.OrgID, d.CreatedAt)
}

type TSAuthDirectoriesClient struct{ i *TSAuthInstanceClient }

func (d *TSAuthDirectoriesClient) Create(ctx context.Context, orgID string) (*TSAuthDirectory, error) {
	body := struct {
		OrgID string `json:"org_id,omitempty"`
	}{OrgID: orgID}
	var out TSAuthDirectory
	if err := d.i.tsAuthDo(ctx, http.MethodPost, d.i.v1("directories"), nil, body, &out, "tsauth.directories.create"); err != nil {
		return nil, err
	}
	return &out, nil
}

func (d *TSAuthDirectoriesClient) List(ctx context.Context) ([]TSAuthDirectory, error) {
	var out tsAuthDataEnvelope[TSAuthDirectory]
	if err := d.i.tsAuthDo(ctx, http.MethodGet, d.i.v1("directories"), nil, nil, &out, "tsauth.directories.list"); err != nil {
		return nil, err
	}
	return out.Data, nil
}

func (d *TSAuthDirectoriesClient) RotateToken(ctx context.Context, directoryID string) (*TSAuthDirectory, error) {
	if err := requireID("directory_id", directoryID); err != nil {
		return nil, err
	}
	var out TSAuthDirectory
	if err := d.i.tsAuthDo(ctx, http.MethodPost, d.i.v1("directories", directoryID, "rotate"), nil, nil, &out, "tsauth.directories.rotate"); err != nil {
		return nil, err
	}
	return &out, nil
}

func (d *TSAuthDirectoriesClient) Delete(ctx context.Context, directoryID string) error {
	if err := requireID("directory_id", directoryID); err != nil {
		return err
	}
	return d.i.tsAuthDo(ctx, http.MethodDelete, d.i.v1("directories", directoryID), nil, nil, nil, "tsauth.directories.delete")
}

// JWT templates family (snake_case; templates are addressed by name of
// 1-48 [a-z0-9_-] characters, server lowercases).

type CreateTSAuthJWTTemplateRequest struct {
	Name            string            `json:"name"`
	Claims          map[string]string `json:"claims"`
	LifetimeSeconds uint32            `json:"lifetime_seconds,omitempty"`
}

type UpdateTSAuthJWTTemplateRequest struct {
	Name            *string           `json:"name,omitempty"`
	Claims          map[string]string `json:"claims,omitempty"`
	LifetimeSeconds *uint32           `json:"lifetime_seconds,omitempty"`
}

type TSAuthJWTTemplate struct {
	ID              string            `json:"id"`
	Name            string            `json:"name"`
	Claims          map[string]string `json:"claims"`
	LifetimeSeconds int64             `json:"lifetime_seconds"`
	CreatedAt       int64             `json:"created_at"`
}

type TSAuthMintedToken struct {
	JWT string `json:"jwt"`
}

type TSAuthJWTTemplatesClient struct{ i *TSAuthInstanceClient }

func (j *TSAuthJWTTemplatesClient) Create(ctx context.Context, request CreateTSAuthJWTTemplateRequest) (*TSAuthJWTTemplate, error) {
	if err := tsAuthRequireTemplateName(request.Name); err != nil {
		return nil, err
	}
	if len(request.Claims) == 0 {
		return nil, &ValidationError{Field: "claims", Message: "must not be empty"}
	}
	var out TSAuthJWTTemplate
	if err := j.i.tsAuthDo(ctx, http.MethodPost, j.i.v1("jwt-templates"), nil, request, &out, "tsauth.jwt_templates.create"); err != nil {
		return nil, err
	}
	return &out, nil
}

func (j *TSAuthJWTTemplatesClient) List(ctx context.Context) ([]TSAuthJWTTemplate, error) {
	var out []TSAuthJWTTemplate
	if err := j.i.tsAuthDo(ctx, http.MethodGet, j.i.v1("jwt-templates"), nil, nil, &out, "tsauth.jwt_templates.list"); err != nil {
		return nil, err
	}
	return out, nil
}

func (j *TSAuthJWTTemplatesClient) Update(ctx context.Context, name string, request UpdateTSAuthJWTTemplateRequest) (*TSAuthJWTTemplate, error) {
	if err := tsAuthRequireTemplateName(name); err != nil {
		return nil, err
	}
	var out TSAuthJWTTemplate
	if err := j.i.tsAuthDo(ctx, http.MethodPatch, j.i.v1("jwt-templates", name), nil, request, &out, "tsauth.jwt_templates.update"); err != nil {
		return nil, err
	}
	return &out, nil
}

func (j *TSAuthJWTTemplatesClient) Delete(ctx context.Context, name string) error {
	if err := tsAuthRequireTemplateName(name); err != nil {
		return err
	}
	return j.i.tsAuthDo(ctx, http.MethodDelete, j.i.v1("jwt-templates", name), nil, nil, nil, "tsauth.jwt_templates.delete")
}

func (j *TSAuthJWTTemplatesClient) MintToken(ctx context.Context, name, userID string) (*TSAuthMintedToken, error) {
	if err := tsAuthRequireTemplateName(name); err != nil {
		return nil, err
	}
	if err := requireID("user_id", userID); err != nil {
		return nil, err
	}
	body := struct {
		UserID string `json:"user_id"`
	}{UserID: userID}
	var out TSAuthMintedToken
	if err := j.i.tsAuthDo(ctx, http.MethodPost, j.i.v1("jwt-templates", name, "tokens"), nil, body, &out, "tsauth.jwt_templates.mint"); err != nil {
		return nil, err
	}
	return &out, nil
}

func tsAuthRequireTemplateName(name string) error {
	if err := requireID("template_name", name); err != nil {
		return err
	}
	if len(name) > 48 {
		return &ValidationError{Field: "template_name", Message: "must be at most 48 characters"}
	}
	for _, char := range name {
		switch {
		case char >= 'a' && char <= 'z', char >= 'A' && char <= 'Z', char >= '0' && char <= '9', char == '-', char == '_':
		default:
			return &ValidationError{Field: "template_name", Message: "must contain only letters, digits, '-' and '_'"}
		}
	}
	return nil
}

// Webhooks family (snake_case; the signing secret is one-time, returned only
// by Create).

type CreateTSAuthWebhookRequest struct {
	URL    string   `json:"url"`
	Events []string `json:"events"`
}

type TSAuthWebhook struct {
	ID            string   `json:"id"`
	URL           string   `json:"url"`
	Events        []string `json:"events"`
	Enabled       bool     `json:"enabled"`
	CreatedAt     int64    `json:"created_at"`
	SigningSecret string   `json:"signing_secret,omitempty"`
}

// String redacts the one-time signing secret so %+v logging cannot leak it.
func (w TSAuthWebhook) String() string {
	return fmt.Sprintf("TSAuthWebhook{ID:%s URL:%s Enabled:%t SigningSecret:[REDACTED] CreatedAt:%d}", w.ID, w.URL, w.Enabled, w.CreatedAt)
}

type TSAuthWebhookTest struct {
	Queued bool `json:"queued"`
}

type TSAuthWebhookReplay struct {
	Replayed bool `json:"replayed"`
}

type TSAuthDeadDelivery struct {
	ID         string  `json:"id"`
	EndpointID string  `json:"endpoint_id"`
	EventID    string  `json:"event_id"`
	Attempts   int     `json:"attempts"`
	LastError  *string `json:"last_error"`
	UpdatedAt  int64   `json:"updated_at"`
}

type TSAuthWebhooksClient struct{ i *TSAuthInstanceClient }

func (w *TSAuthWebhooksClient) Create(ctx context.Context, request CreateTSAuthWebhookRequest) (*TSAuthWebhook, error) {
	if err := tsAuthRequireWebhookURL("url", request.URL); err != nil {
		return nil, err
	}
	if len(request.Events) == 0 {
		return nil, &ValidationError{Field: "events", Message: "must not be empty"}
	}
	for _, event := range request.Events {
		if strings.TrimSpace(event) == "" {
			return nil, &ValidationError{Field: "events", Message: "must not contain blank entries"}
		}
	}
	var out TSAuthWebhook
	if err := w.i.tsAuthDo(ctx, http.MethodPost, w.i.v1("webhooks"), nil, request, &out, "tsauth.webhooks.create"); err != nil {
		return nil, err
	}
	return &out, nil
}

func (w *TSAuthWebhooksClient) List(ctx context.Context) ([]TSAuthWebhook, error) {
	var out []TSAuthWebhook
	if err := w.i.tsAuthDo(ctx, http.MethodGet, w.i.v1("webhooks"), nil, nil, &out, "tsauth.webhooks.list"); err != nil {
		return nil, err
	}
	return out, nil
}

func (w *TSAuthWebhooksClient) Delete(ctx context.Context, webhookID string) error {
	if err := requireID("webhook_id", webhookID); err != nil {
		return err
	}
	return w.i.tsAuthDo(ctx, http.MethodDelete, w.i.v1("webhooks", webhookID), nil, nil, nil, "tsauth.webhooks.delete")
}

func (w *TSAuthWebhooksClient) SendTestEvent(ctx context.Context, webhookID string) (*TSAuthWebhookTest, error) {
	if err := requireID("webhook_id", webhookID); err != nil {
		return nil, err
	}
	var out TSAuthWebhookTest
	if err := w.i.tsAuthDo(ctx, http.MethodPost, w.i.v1("webhooks", webhookID, "test"), nil, struct{}{}, &out, "tsauth.webhooks.test"); err != nil {
		return nil, err
	}
	return &out, nil
}

func (w *TSAuthWebhooksClient) ListDeadLetter(ctx context.Context) ([]TSAuthDeadDelivery, error) {
	var out []TSAuthDeadDelivery
	if err := w.i.tsAuthDo(ctx, http.MethodGet, w.i.v1("webhooks", "dead-letter"), nil, nil, &out, "tsauth.webhooks.dead_letter.list"); err != nil {
		return nil, err
	}
	return out, nil
}

func (w *TSAuthWebhooksClient) ReplayDeadDelivery(ctx context.Context, deliveryID string) (*TSAuthWebhookReplay, error) {
	if err := requireID("delivery_id", deliveryID); err != nil {
		return nil, err
	}
	var out TSAuthWebhookReplay
	if err := w.i.tsAuthDo(ctx, http.MethodPost, w.i.v1("webhooks", "dead-letter", deliveryID, "replay"), nil, nil, &out, "tsauth.webhooks.dead_letter.replay"); err != nil {
		return nil, err
	}
	return &out, nil
}

// Audit events family (snake_case, BARE list, newest first).

type TSAuthAuditEvent struct {
	ID                      string         `json:"id"`
	InstanceID              string         `json:"instance_id"`
	Type                    string         `json:"type"`
	ActorID                 string         `json:"actor_id"`
	Data                    map[string]any `json:"data"`
	CreatedAt               int64          `json:"created_at"`
	IntegrityVersion        int64          `json:"integrity_version"`
	IntegrityHash           string         `json:"integrity_hash"`
	IntegritySignature      *string        `json:"integrity_signature"`
	IntegrityKeyFingerprint *string        `json:"integrity_key_fingerprint"`
}

type TSAuthAuditEventsClient struct{ i *TSAuthInstanceClient }

func (a *TSAuthAuditEventsClient) List(ctx context.Context, limit uint32) ([]TSAuthAuditEvent, error) {
	query := url.Values{}
	if limit > 0 {
		query.Set("limit", strconv.FormatUint(uint64(limit), 10))
	}
	var out []TSAuthAuditEvent
	if err := a.i.tsAuthDo(ctx, http.MethodGet, a.i.v1("audit-events"), query, nil, &out, "tsauth.audit_events.list"); err != nil {
		return nil, err
	}
	return out, nil
}

// Billing family (snake_case).

type TSAuthBillingOverview struct {
	Plan                 string  `json:"plan"`
	Status               string  `json:"status"`
	StripeCustomerID     *string `json:"stripe_customer_id"`
	StripeSubscriptionID *string `json:"stripe_subscription_id"`
	CurrentPeriodEnd     *int64  `json:"current_period_end"`
	UserCount            int64   `json:"user_count"`
	Quota                int64   `json:"quota"`
}

type TSAuthCheckout struct {
	CheckoutURL string `json:"checkout_url"`
}

type TSAuthBillingPortal struct {
	PortalURL string `json:"portal_url"`
}

type TSAuthQuotaDecision struct {
	Allowed bool   `json:"allowed"`
	Plan    string `json:"plan"`
	Quota   int64  `json:"quota"`
}

type TSAuthQuotaReconciliation struct {
	ClaimsExamined int64 `json:"claims_examined"`
	ClaimsRemoved  int64 `json:"claims_removed"`
	ClaimsRetained int64 `json:"claims_retained"`
}

type TSAuthBillingClient struct{ i *TSAuthInstanceClient }

func (b *TSAuthBillingClient) GetOverview(ctx context.Context) (*TSAuthBillingOverview, error) {
	var out TSAuthBillingOverview
	if err := b.i.tsAuthDo(ctx, http.MethodGet, b.i.v1("billing"), nil, nil, &out, "tsauth.billing.get"); err != nil {
		return nil, err
	}
	return &out, nil
}

func (b *TSAuthBillingClient) CreateCheckout(ctx context.Context, plan string) (*TSAuthCheckout, error) {
	if err := requireID("plan", plan); err != nil {
		return nil, err
	}
	body := struct {
		Plan string `json:"plan"`
	}{Plan: plan}
	var out TSAuthCheckout
	if err := b.i.tsAuthDo(ctx, http.MethodPost, b.i.v1("billing", "checkout"), nil, body, &out, "tsauth.billing.checkout"); err != nil {
		return nil, err
	}
	return &out, nil
}

func (b *TSAuthBillingClient) CreatePortalSession(ctx context.Context) (*TSAuthBillingPortal, error) {
	var out TSAuthBillingPortal
	if err := b.i.tsAuthDo(ctx, http.MethodPost, b.i.v1("billing", "portal"), nil, struct{}{}, &out, "tsauth.billing.portal"); err != nil {
		return nil, err
	}
	return &out, nil
}

func (b *TSAuthBillingClient) EnforceQuota(ctx context.Context, currentUsers int64) (*TSAuthQuotaDecision, error) {
	if currentUsers < 0 {
		return nil, &ValidationError{Field: "current_users", Message: "must be non-negative"}
	}
	body := struct {
		CurrentUsers int64 `json:"current_users"`
	}{CurrentUsers: currentUsers}
	var out TSAuthQuotaDecision
	if err := b.i.tsAuthDo(ctx, http.MethodPost, b.i.v1("billing", "enforce-check"), nil, body, &out, "tsauth.billing.enforce_quota"); err != nil {
		return nil, err
	}
	return &out, nil
}

func (b *TSAuthBillingClient) ReconcileQuota(ctx context.Context) (*TSAuthQuotaReconciliation, error) {
	var out TSAuthQuotaReconciliation
	if err := b.i.tsAuthDo(ctx, http.MethodPost, b.i.v1("billing", "quota", "reconcile"), nil, nil, &out, "tsauth.billing.reconcile_quota"); err != nil {
		return nil, err
	}
	return &out, nil
}

// String redacts the one-time api_key so %+v logging cannot leak it.
func (k TSAuthAPIKey) String() string {
	return fmt.Sprintf("TSAuthAPIKey{ID:%s Name:%s APIKey:[REDACTED] Revoked:%t}", k.ID, k.Name, k.Revoked)
}

// Instance family (snake_case; the PATCH response omits created_at).

// TSAuthInstanceRecord is the GET view; the PATCH response omits created_at,
// which decodes to a nil pointer rather than a misleading zero epoch.
type TSAuthInstanceRecord struct {
	ID         string  `json:"id"`
	Slug       string  `json:"slug"`
	Name       string  `json:"name"`
	LogoURL    *string `json:"logo_url"`
	ThemeColor *string `json:"theme_color"`
	CreatedAt  *int64  `json:"created_at"`
}

type TSAuthInstanceSettings struct {
	Name       *string `json:"name,omitempty"`
	LogoURL    *string `json:"logo_url,omitempty"`
	ThemeColor *string `json:"theme_color,omitempty"`
}

type TSAuthInstanceFamilyClient struct{ i *TSAuthInstanceClient }

func (f *TSAuthInstanceFamilyClient) Get(ctx context.Context) (*TSAuthInstanceRecord, error) {
	var out TSAuthInstanceRecord
	if err := f.i.tsAuthDo(ctx, http.MethodGet, f.i.v1("instance"), nil, nil, &out, "tsauth.instance.get"); err != nil {
		return nil, err
	}
	return &out, nil
}

func (f *TSAuthInstanceFamilyClient) Update(ctx context.Context, settings TSAuthInstanceSettings) (*TSAuthInstanceRecord, error) {
	var out TSAuthInstanceRecord
	if err := f.i.tsAuthDo(ctx, http.MethodPatch, f.i.v1("instance"), nil, settings, &out, "tsauth.instance.update"); err != nil {
		return nil, err
	}
	return &out, nil
}

// OIDC family: public routes under /{slug}/... (no /v1, no sk_ key). The
// discovery and JWKS routes are unauthenticated; the token endpoints
// authenticate with client_secret_post form fields; userinfo carries the
// access token as its bearer.

type TSAuthDiscoveryDocument struct {
	Issuer                            string   `json:"issuer"`
	AuthorizationEndpoint             string   `json:"authorization_endpoint"`
	TokenEndpoint                     string   `json:"token_endpoint"`
	UserinfoEndpoint                  string   `json:"userinfo_endpoint"`
	JwksURI                           string   `json:"jwks_uri"`
	RevocationEndpoint                string   `json:"revocation_endpoint"`
	IntrospectionEndpoint             string   `json:"introspection_endpoint"`
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
	Kty string `json:"kty"`
	Crv string `json:"crv"`
	Alg string `json:"alg"`
	Use string `json:"use"`
	Kid string `json:"kid"`
	X   string `json:"x"`
	Y   string `json:"y"`
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

type TSAuthIntrospection struct {
	Active    bool    `json:"active"`
	TokenType *string `json:"token_type"`
	Iss       *string `json:"iss"`
	Sub       *string `json:"sub"`
	Aud       *string `json:"aud"`
	ClientID  *string `json:"client_id"`
	Scope     *string `json:"scope"`
	Exp       *int64  `json:"exp"`
	Iat       *int64  `json:"iat"`
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

func (o *TSAuthOIDCClient) ExchangeAuthorizationCode(ctx context.Context, clientID, clientSecret, code, redirectURI, codeVerifier string) (*TSAuthTokenResponse, error) {
	if err := tsAuthRequireClientCredentials(clientID, clientSecret); err != nil {
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
	form.Set("client_id", clientID)
	form.Set("client_secret", clientSecret)
	form.Set("code", code)
	form.Set("redirect_uri", redirectURI)
	if codeVerifier != "" {
		form.Set("code_verifier", codeVerifier)
	}
	var out TSAuthTokenResponse
	if err := o.i.tsAuthPostForm(ctx, o.i.oidc("oauth/token"), form, &out, "tsauth.oidc.token.code"); err != nil {
		return nil, err
	}
	return &out, nil
}

func (o *TSAuthOIDCClient) RequestClientCredentialsToken(ctx context.Context, clientID, clientSecret, scope string) (*TSAuthTokenResponse, error) {
	if err := tsAuthRequireClientCredentials(clientID, clientSecret); err != nil {
		return nil, err
	}
	form := url.Values{}
	form.Set("grant_type", "client_credentials")
	form.Set("client_id", clientID)
	form.Set("client_secret", clientSecret)
	if scope != "" {
		form.Set("scope", scope)
	}
	var out TSAuthTokenResponse
	if err := o.i.tsAuthPostForm(ctx, o.i.oidc("oauth/token"), form, &out, "tsauth.oidc.token.client_credentials"); err != nil {
		return nil, err
	}
	return &out, nil
}

func (o *TSAuthOIDCClient) RefreshToken(ctx context.Context, clientID, clientSecret, refreshToken string) (*TSAuthTokenResponse, error) {
	if err := tsAuthRequireClientCredentials(clientID, clientSecret); err != nil {
		return nil, err
	}
	if err := requireID("refresh_token", refreshToken); err != nil {
		return nil, err
	}
	form := url.Values{}
	form.Set("grant_type", "refresh_token")
	form.Set("client_id", clientID)
	form.Set("client_secret", clientSecret)
	form.Set("refresh_token", refreshToken)
	var out TSAuthTokenResponse
	if err := o.i.tsAuthPostForm(ctx, o.i.oidc("oauth/token"), form, &out, "tsauth.oidc.token.refresh"); err != nil {
		return nil, err
	}
	return &out, nil
}

func (o *TSAuthOIDCClient) RevokeToken(ctx context.Context, clientID, clientSecret, token string) error {
	if err := tsAuthRequireClientCredentials(clientID, clientSecret); err != nil {
		return err
	}
	if err := requireID("token", token); err != nil {
		return err
	}
	form := url.Values{}
	form.Set("client_id", clientID)
	form.Set("client_secret", clientSecret)
	form.Set("token", token)
	return o.i.tsAuthPostForm(ctx, o.i.oidc("oauth/revoke"), form, tsAuthAnySuccess{}, "tsauth.oidc.token.revoke")
}

func (o *TSAuthOIDCClient) IntrospectToken(ctx context.Context, clientID, clientSecret, token string) (*TSAuthIntrospection, error) {
	if err := tsAuthRequireClientCredentials(clientID, clientSecret); err != nil {
		return nil, err
	}
	if err := requireID("token", token); err != nil {
		return nil, err
	}
	form := url.Values{}
	form.Set("client_id", clientID)
	form.Set("client_secret", clientSecret)
	form.Set("token", token)
	var out TSAuthIntrospection
	if err := o.i.tsAuthPostForm(ctx, o.i.oidc("oauth/introspect"), form, &out, "tsauth.oidc.token.introspect"); err != nil {
		return nil, err
	}
	return &out, nil
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

func tsAuthRequireClientCredentials(clientID, clientSecret string) error {
	if err := requireID("client_id", clientID); err != nil {
		return err
	}
	return requireID("client_secret", clientSecret)
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

func tsAuthRequireWebhookURL(field, value string) error {
	if err := tsAuthRequireAbsoluteURL(field, value); err != nil {
		return err
	}
	parsed, _ := url.Parse(strings.TrimSpace(value))
	if parsed.Scheme == "http" && !tsAuthWebhookHTTPHost(parsed.Hostname()) {
		return &ValidationError{Field: field, Message: "must use https outside loopback hosts"}
	}
	return nil
}

// tsAuthWebhookHTTPHost matches the service preflight exactly: plain http is
// allowed only for 127.0.0.1, localhost, and ::1.
func tsAuthWebhookHTTPHost(hostname string) bool {
	switch strings.ToLower(hostname) {
	case "127.0.0.1", "localhost", "::1":
		return true
	default:
		return false
	}
}

// Webhook signature verification. Each delivery POSTs the raw JSON payload
// with X-TsAuth-Timestamp and X-TsAuth-Signature headers, where the signature
// is "sha256=" + lowercaseHex(HMAC-SHA256(key = signing secret, message =
// UTF8(timestamp + "." + raw body))). Verify the raw body bytes: re-serialized
// JSON does not verify.

// VerifyTSAuthWebhook verifies a ts-auth webhook delivery signature.
func VerifyTSAuthWebhook(signingSecret, timestamp, signature string, payload []byte) bool {
	return VerifyTSAuthWebhookWithTolerance(signingSecret, timestamp, signature, payload, 0, time.Time{})
}

// VerifyTSAuthWebhookWithTolerance additionally rejects timestamps further
// than tolerance from now (replay protection). Malformed inputs return false.
func VerifyTSAuthWebhookWithTolerance(signingSecret, timestamp, signature string, payload []byte, tolerance time.Duration, now time.Time) bool {
	if signingSecret == "" || timestamp == "" || signature == "" {
		return false
	}
	// A non-numeric timestamp is malformed in every variant; reject it before
	// the signature check so it can never verify, with or without tolerance.
	seconds, err := strconv.ParseInt(strings.TrimSpace(timestamp), 10, 64)
	if err != nil {
		return false
	}
	mac := hmac.New(sha256.New, []byte(signingSecret))
	mac.Write([]byte(timestamp))
	mac.Write([]byte("."))
	mac.Write(payload)
	expected := "sha256=" + hex.EncodeToString(mac.Sum(nil))
	if subtle.ConstantTimeCompare([]byte(expected), []byte(signature)) != 1 {
		return false
	}
	if tolerance > 0 {
		if now.IsZero() {
			now = time.Now()
		}
		delta := now.Sub(time.Unix(seconds, 0))
		if delta < 0 {
			delta = -delta
		}
		if delta > tolerance {
			return false
		}
	}
	return true
}
