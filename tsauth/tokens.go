// Package tsauth is the client of ts-auth's platform identity contract
// (ts-auth docs/platform-contract.md, TS-598): the token sources that turn an
// API key or a user's sign-in into short-lived access tokens for TerraBase
// and other resource servers.
//
//	tokens, err := tsauth.NewAPIKeySource(tsauth.APIKeyConfig{Key: apiKey, Project: projectID})
//	tb, err := v1.New(v1.Config{BaseURL: origin, TokenSource: tokens})
package tsauth

import (
	"context"
	"encoding/base64"
	"encoding/json"
	"errors"
	"fmt"
	"io"
	"math"
	"net"
	"net/http"
	"net/url"
	"regexp"
	"slices"
	"strconv"
	"strings"
	"sync"
	"time"

	"terrascale.tech/sdk/internal/entityid"
)

// DefaultIssuer is the terrascale tenant, which issues every platform token.
const DefaultIssuer = "https://auth.terrascale.tech/terrascale"

// TerraBase scopes a token can carry (contract section 3).
const (
	ScopeDatabasesRead  = "databases:read"
	ScopeDatabasesWrite = "databases:write"
	ScopeDocumentsRead  = "documents:read"
	ScopeDocumentsWrite = "documents:write"
)

// APIKeyConfig configures an API key token source (contract section 6.1).
type APIKeyConfig struct {
	// Issuer is the tenant issuer; empty means DefaultIssuer.
	Issuer string
	// Key is the whole API key, <ULID>_<52>. It is the client secret; its
	// leading 26-character ULID is the client id.
	Key string
	// Project is the project an organization key's token acts in; empty
	// yields an organization token. A project key may name its own project.
	Project string
	// Scopes narrows the key's scopes to some of the four Scope constants;
	// empty requests none, so ts-auth grants the key's own scopes.
	Scopes []string
	// HTTPClient nil uses a client with a 30 s timeout. It is copied and
	// never follows redirects.
	HTTPClient *http.Client
}

// ExchangeConfig configures a token exchange source (contract section 6.2):
// a signed-in user's token becomes an organization or project token.
type ExchangeConfig struct {
	// Issuer is the tenant issuer; empty means DefaultIssuer.
	Issuer string
	// ClientID is the exchanging OAuth client, the one the subject token was
	// issued to, such as ts-dashboard or ts-cli.
	ClientID string
	// ClientSecret authenticates a confidential client; empty for a public one.
	ClientSecret string
	// SubjectToken returns the user's current sign-in access token.
	SubjectToken func(context.Context) (string, error)
	// Organization is required; Project, when set, yields a project token.
	Organization string
	Project      string
	// Scopes narrows what the user's role allows to some of the four Scope
	// constants; empty requests none, so ts-auth grants the role's scopes.
	Scopes []string
	// HTTPClient nil uses a client with a 30 s timeout. It is copied and
	// never follows redirects.
	HTTPClient *http.Client
}

var keyPattern = regexp.MustCompile(`^[0-7][0-9A-HJKMNP-TV-Z]{25}_[0-9a-hjkmnp-tv-z]{52}$`)

// checkScopes accepts only the four TerraBase scopes: ts-auth issues no other.
func checkScopes(scopes []string) error {
	for _, scope := range scopes {
		if !slices.Contains([]string{ScopeDatabasesRead, ScopeDatabasesWrite, ScopeDocumentsRead, ScopeDocumentsWrite}, scope) {
			return fmt.Errorf("tsauth: Scopes: %q is not a TerraBase scope", scope)
		}
	}
	return nil
}

// NewAPIKeySource returns a source of tokens for an API key.
func NewAPIKeySource(cfg APIKeyConfig) (*TokenSource, error) {
	if !keyPattern.MatchString(cfg.Key) {
		return nil, errors.New("tsauth: Key must be an API key, a canonical ULID, an underscore and 52 base32 secret characters")
	}
	if err := checkScopes(cfg.Scopes); err != nil {
		return nil, err
	}
	if cfg.Project != "" {
		if err := entityid.Check(cfg.Project); err != nil {
			return nil, fmt.Errorf("tsauth: Project: %w", err)
		}
	}
	endpoint, err := tokenEndpoint(cfg.Issuer)
	if err != nil {
		return nil, err
	}
	form := url.Values{"grant_type": {"client_credentials"}}
	if cfg.Project != "" {
		form.Set("project", cfg.Project)
	}
	if len(cfg.Scopes) > 0 {
		form.Set("scope", strings.Join(cfg.Scopes, " "))
	}
	client := cfg.Key[:26]
	return newSource(cfg.HTTPClient, func(ctx context.Context, hc *http.Client) (*tokenResponse, error) {
		return requestToken(ctx, hc, endpoint, form, client, cfg.Key)
	}), nil
}

// NewExchangeSource returns a source of organization or project tokens for
// a signed-in user.
func NewExchangeSource(cfg ExchangeConfig) (*TokenSource, error) {
	if cfg.ClientID == "" || cfg.SubjectToken == nil || cfg.Organization == "" {
		return nil, errors.New("tsauth: ClientID, SubjectToken and Organization are required")
	}
	if err := checkScopes(cfg.Scopes); err != nil {
		return nil, err
	}
	if cfg.Project != "" {
		if err := entityid.Check(cfg.Project); err != nil {
			return nil, fmt.Errorf("tsauth: Project: %w", err)
		}
	}
	endpoint, err := tokenEndpoint(cfg.Issuer)
	if err != nil {
		return nil, err
	}
	if err := entityid.Check(cfg.Organization); err != nil {
		return nil, fmt.Errorf("tsauth: Organization: %w", err)
	}
	return newSource(cfg.HTTPClient, func(ctx context.Context, hc *http.Client) (*tokenResponse, error) {
		subject, err := cfg.SubjectToken(ctx)
		if err != nil {
			return nil, fmt.Errorf("tsauth: subject token: %w", err)
		}
		form := url.Values{
			"grant_type":         {"urn:ietf:params:oauth:grant-type:token-exchange"},
			"subject_token":      {subject},
			"subject_token_type": {"urn:ietf:params:oauth:token-type:access_token"},
			"organization":       {cfg.Organization},
		}
		if cfg.Project != "" {
			form.Set("project", cfg.Project)
		}
		if len(cfg.Scopes) > 0 {
			form.Set("scope", strings.Join(cfg.Scopes, " "))
		}
		if cfg.ClientSecret == "" {
			form.Set("client_id", cfg.ClientID) // a public client
		}
		return requestToken(ctx, hc, endpoint, form, cfg.ClientID, cfg.ClientSecret)
	}), nil
}

// TokenSource caches one access token: Token returns it until 60 s before
// it expires, then requests a new one. Concurrent callers share one request.
// It is safe for concurrent use and satisfies v1.TokenSource.
type TokenSource struct {
	hc    *http.Client
	fetch func(context.Context, *http.Client) (*tokenResponse, error)
	now   func() time.Time

	mu        sync.Mutex
	token     string
	refreshAt time.Time
	flight    *flight
}

type flight struct {
	done    chan struct{}
	token   string
	err     error
	cancel  context.CancelFunc
	waiters int
}

func newSource(hc *http.Client, fetch func(context.Context, *http.Client) (*tokenResponse, error)) *TokenSource {
	return &TokenSource{hc: noRedirects(hc), fetch: fetch, now: time.Now}
}

// noRedirects copies hc, or a client with a 30 s timeout: a redirect would
// carry a secret or token elsewhere.
func noRedirects(hc *http.Client) *http.Client {
	copied := http.Client{Timeout: 30 * time.Second}
	if hc != nil {
		copied = *hc
	}
	copied.CheckRedirect = func(*http.Request, []*http.Request) error { return http.ErrUseLastResponse }
	return &copied
}

// Token returns a valid access token. A request that fails is not cached:
// the next call requests again.
func (s *TokenSource) Token(ctx context.Context) (string, error) {
	if err := ctx.Err(); err != nil {
		return "", err
	}
	s.mu.Lock()
	if s.token != "" && s.now().Before(s.refreshAt) {
		token := s.token
		s.mu.Unlock()
		return token, nil
	}
	f := s.flight
	if f == nil {
		fetchCtx, cancel := context.WithTimeout(context.WithoutCancel(ctx), 30*time.Second)
		f = &flight{done: make(chan struct{}), cancel: cancel}
		s.flight = f
		go s.run(fetchCtx, f)
	}
	f.waiters++
	s.mu.Unlock()
	defer func() {
		s.mu.Lock()
		defer s.mu.Unlock()
		f.waiters--
		if f.waiters == 0 && s.flight == f {
			s.flight = nil
			f.cancel()
		}
	}()
	select {
	case <-f.done:
		if err := ctx.Err(); err != nil {
			return "", err
		}
		return f.token, f.err
	case <-ctx.Done():
		return "", ctx.Err()
	}
}

func (s *TokenSource) run(ctx context.Context, f *flight) {
	defer f.cancel()
	start := s.now()
	res, err := s.fetch(ctx, s.hc)
	var lifetime time.Duration
	if err == nil {
		// Its lifetime counts from before the request: a short-lived token
		// may expire in transit.
		if lifetime = time.Duration(res.ExpiresIn) * time.Second; !s.now().Before(start.Add(lifetime)) {
			err = errors.New("tsauth: the access token expired before it arrived")
		}
	}
	s.mu.Lock()
	defer s.mu.Unlock()
	if err == nil && s.flight == f && ctx.Err() == nil {
		s.token, s.refreshAt = res.AccessToken, start.Add(lifetime-min(time.Minute, lifetime/2))
		f.token = res.AccessToken
	}
	f.err = err
	if s.flight == f {
		s.flight = nil
	}
	close(f.done)
}

// Invalidate drops token when a resource server refused it (401), so the
// next Token requests a new one. Other tokens are kept.
func (s *TokenSource) Invalidate(token string) {
	s.mu.Lock()
	defer s.mu.Unlock()
	if s.token == token {
		s.token = ""
	}
}

// Error is a token endpoint error (RFC 6749 section 5.2), such as 401
// invalid_client (an unknown, wrong, revoked or expired key), 400
// invalid_scope or invalid_target, or 429 rate_limited.
type Error struct {
	Status      int
	Code        string `json:"error"`
	Description string `json:"error_description"`
	// RetryAfter is the parsed Retry-After of a 429.
	RetryAfter time.Duration
}

func (e *Error) Error() string {
	msg := fmt.Sprintf("tsauth: token endpoint answered %d %s", e.Status, e.Code)
	if e.Description != "" {
		msg += ": " + e.Description
	}
	return msg
}

type tokenResponse struct {
	AccessToken  string `json:"access_token"`
	TokenType    string `json:"token_type"`
	ExpiresIn    int64  `json:"expires_in"`
	Scope        string `json:"scope"`
	RefreshToken string `json:"refresh_token"`
	IDToken      string `json:"id_token"`
}

// retryAfter parses Retry-After: delta-seconds or an HTTP-date (RFC 9110
// section 10.2.3); zero when absent, invalid or past.
func retryAfter(v string) time.Duration {
	if s, err := strconv.ParseUint(strings.TrimSpace(v), 10, 32); err == nil {
		return time.Duration(s) * time.Second
	}
	if t, err := http.ParseTime(v); err == nil {
		return max(time.Until(t), 0)
	}
	return 0
}

func tokenEndpoint(issuer string) (string, error) {
	u, err := parseIssuer(issuer)
	if err != nil {
		return "", err
	}
	u.Path += "/oauth/token"
	return u.String(), nil
}

// parseIssuer validates an issuer, empty meaning DefaultIssuer: an https
// URL (http for loopback) without user info, query, fragment or escaped
// path segments; a trailing slash is dropped.
func parseIssuer(issuer string) (*url.URL, error) {
	return parseIssuerWithInsecureHTTP(issuer, false)
}

func parseIssuerWithInsecureHTTP(issuer string, allowInsecureHTTP bool) (*url.URL, error) {
	if issuer == "" {
		issuer = DefaultIssuer
	}
	u, err := url.Parse(issuer)
	// A RawPath means escapes such as %2F that the decoded Path loses.
	if err != nil || u.Host == "" || u.User != nil || u.RawQuery != "" || u.ForceQuery || u.Fragment != "" || u.RawPath != "" ||
		(u.Scheme != "https" && !(u.Scheme == "http" && (allowInsecureHTTP || loopback(u.Hostname())))) {
		return nil, fmt.Errorf("tsauth: Issuer %q must be an https URL without a query or escaped path", issuer)
	}
	u.Path = strings.TrimSuffix(u.Path, "/")
	return u, nil
}

func loopback(host string) bool {
	ip := net.ParseIP(host)
	return host == "localhost" || ip != nil && ip.IsLoopback()
}

// requestToken posts form to the token endpoint and decodes the token.
func requestToken(ctx context.Context, hc *http.Client, endpoint string, form url.Values, client, secret string) (*tokenResponse, error) {
	body, err := oauthPost(ctx, hc, endpoint, form, client, secret)
	if err != nil {
		return nil, err
	}
	// Decoding errors quote the body's values, which can be credentials:
	// the cause is dropped.
	var tr tokenResponse
	if json.Unmarshal(body, &tr) != nil || tr.AccessToken == "" || !strings.EqualFold(tr.TokenType, "Bearer") ||
		tr.ExpiresIn <= 0 || tr.ExpiresIn > math.MaxInt64/int64(time.Second) {
		return nil, errors.New("tsauth: the token response is not a Bearer access_token with a valid expires_in")
	}
	return &tr, nil
}

// oauthPost posts form to an OAuth endpoint and returns a 200 body; other
// answers are *Error. A client with a secret authenticates with HTTP Basic
// (client_secret_basic).
func oauthPost(ctx context.Context, hc *http.Client, endpoint string, form url.Values, client, secret string) ([]byte, error) {
	req, err := http.NewRequestWithContext(ctx, http.MethodPost, endpoint, strings.NewReader(form.Encode()))
	if err != nil {
		return nil, err
	}
	req.Header.Set("Content-Type", "application/x-www-form-urlencoded")
	req.Header.Set("Accept", "application/json")
	if secret != "" {
		req.Header.Set("Authorization", basicAuth(client, secret))
	}
	res, err := hc.Do(req)
	if err != nil {
		return nil, fmt.Errorf("tsauth: POST %s: %w", req.URL.Path, err)
	}
	defer res.Body.Close()
	body, err := io.ReadAll(io.LimitReader(res.Body, 64<<10))
	if err != nil {
		return nil, fmt.Errorf("tsauth: read %s response: %w", req.URL.Path, err)
	}
	if res.StatusCode != http.StatusOK {
		e := &Error{Status: res.StatusCode}
		json.Unmarshal(body, e) // a malformed body leaves Code empty
		e.RetryAfter = retryAfter(res.Header.Get("Retry-After"))
		return nil, e
	}
	return body, nil
}

// basicAuth is client_secret_basic: both parts form-encoded (RFC 6749
// section 2.3.1), as ts-auth decodes them.
func basicAuth(client, secret string) string {
	return "Basic " + base64.StdEncoding.EncodeToString([]byte(url.QueryEscape(client)+":"+url.QueryEscape(secret)))
}
