// Package v1 is the name-based TerraBase v1 client (terrascale.tech/sdk/v1).
//
// It talks to the C# TerraBase server: the Documents API v1 (databases,
// collections, documents, queries and transactions under
// /v1/projects/{project}/databases/{database}) with a ts-auth project access
// token. SQL and database tooling use the same hierarchy. The legacy
// Management API (/v1/management) acts as a ts-auth user.
//
// Document values are never decoded through float64: documents are returned
// as json.RawMessage, and Decode helpers use json.Decoder.UseNumber, so
// numbers keep their exact decimal text as json.Number.
//
// A public client is created with New and a project token or TokenSource; the Management API
// uses NewManagement and a user's ts-auth access token:
//
//	c, err := v1.New(v1.Config{Token: projectAccessToken})
//	users := c.Database(projectID, "main").Collection("users")
//	doc, err := users.Get(ctx, id)
package v1

import (
	"bytes"
	"cmp"
	"context"
	"crypto/tls"
	"encoding/json"
	"errors"
	"fmt"
	"io"
	mrand "math/rand/v2"
	"net"
	"net/http"
	"net/url"
	"regexp"
	"strconv"
	"strings"
	"time"

	"terrascale.tech/sdk/internal/entityid"
)

// Protocol limits and client defaults.
const (
	// DefaultBaseURL is the fixed global TerraBase API origin.
	DefaultBaseURL = "https://api.terrascale.tech"
	// MaxRequestBytes is the server's request body limit (api-v1.md).
	MaxRequestBytes = 4 << 20
	// DefaultMaxResponseBytes bounds how much of one response body is read.
	DefaultMaxResponseBytes int64 = 64 << 20
	// DefaultMaxRetries is the retry budget for 429 and retryable 503s.
	DefaultMaxRetries = 3
	// OperationTimeout bounds ordinary requests, including auth, resolution and retries.
	OperationTimeout = 30 * time.Second
)

// Config configures a Client or a Management client.
type Config struct {
	// BaseURL is the TerraBase origin; empty uses DefaultBaseURL.
	// It must be HTTPS; plain HTTP is accepted only for loopback hosts. A path
	// prefix is kept; a query, fragment or user info is rejected.
	BaseURL string
	// Resolver supplies the endpoint for each call. Set either Resolver or
	// BaseURL; BaseURL uses a StaticResolver. Resolver must be safe for
	// concurrent use. Resolved URLs follow the same rules as BaseURL.
	Resolver Resolver
	// Token is the tb_ API key for Client, or a ts-auth project access token
	// for the project-management routes (unique keys). For Management it is
	// empty, or the peer credential (internal listener only, see As).
	Token string
	// TokenSource supplies a current project access token, for example a
	// tsauth API key or token exchange source. Set either Token or TokenSource.
	// A Client represents one principal; its source must not switch principals.
	TokenSource TokenSource
	// HTTPClient is used for every request. Nil uses a client that requires
	// TLS 1.3. The client is copied and its redirect policy replaced, so the
	// bearer token is never forwarded to a redirect target.
	HTTPClient *http.Client
}

// Client is a data-plane client for one API key. It is safe for concurrent use.
type Client struct {
	resolver    Resolver
	token       string
	tokenSource TokenSource
	hc          *http.Client
	maxRetries  int
	maxResponse int64
	shapeKeys   shapeKeyCache
}

// TokenSource supplies access tokens; TerraBase validates their claims.
// Sources also implementing Invalidate(string) refresh once on a non-permanent 401.
// Key refusals call Reject(string, error), when supported, and never refresh.
type TokenSource interface {
	Token(context.Context) (string, error)
}

// Resolver resolves a TerraBase endpoint. Resolution errors stop the call;
// the client never falls back to another endpoint. A retry keeps the endpoint
// resolved for the original call.
type Resolver interface {
	Resolve(context.Context) (string, error)
}

// StaticResolver always resolves to the configured base URL.
type StaticResolver string

func (r StaticResolver) Resolve(ctx context.Context) (string, error) {
	return string(r), ctx.Err()
}

var (
	tokenPattern = regexp.MustCompile(`^tb_[A-Za-z0-9_-]{43}$`)
	// jwtPattern is the shape of a ts-auth access token; TerraBase verifies its signature and claims.
	jwtPattern = regexp.MustCompile(`^[A-Za-z0-9_-]+\.[A-Za-z0-9_-]+\.[A-Za-z0-9_-]+$`)
)

// New returns a data-plane client.
func New(cfg Config) (*Client, error) {
	if cfg.TokenSource == nil && !tokenPattern.MatchString(cfg.Token) && !jwtPattern.MatchString(cfg.Token) {
		return nil, errors.New("terrabase: Token must be a tb_ API key or a ts-auth project access token")
	}
	return newClient(cfg)
}

func newClient(cfg Config) (*Client, error) {
	if cfg.Token != "" && cfg.TokenSource != nil {
		return nil, errors.New("terrabase: set either Token or TokenSource")
	}
	if cfg.Resolver != nil && cfg.BaseURL != "" {
		return nil, errors.New("terrabase: set either Resolver or BaseURL")
	}
	if cfg.Resolver == nil {
		if cfg.BaseURL == "" {
			cfg.BaseURL = DefaultBaseURL
		}
		if _, err := parseBaseURL(cfg.BaseURL); err != nil {
			return nil, err
		}
		cfg.Resolver = StaticResolver(cfg.BaseURL)
	}
	hc := &http.Client{Transport: &http.Transport{
		Proxy:                 http.ProxyFromEnvironment,
		DialContext:           (&net.Dialer{Timeout: 10 * time.Second, KeepAlive: 30 * time.Second}).DialContext,
		TLSHandshakeTimeout:   10 * time.Second,
		ResponseHeaderTimeout: 30 * time.Second,
		TLSClientConfig:       &tls.Config{MinVersion: tls.VersionTLS13},
		ForceAttemptHTTP2:     true,
		IdleConnTimeout:       90 * time.Second,
	}}
	if cfg.HTTPClient != nil {
		copied := *cfg.HTTPClient
		hc = &copied
	}
	hc.CheckRedirect = func(*http.Request, []*http.Request) error { return http.ErrUseLastResponse }
	c := &Client{resolver: cfg.Resolver, token: cfg.Token, tokenSource: cfg.TokenSource, hc: hc, maxRetries: DefaultMaxRetries, maxResponse: DefaultMaxResponseBytes}
	return c, nil
}

func (c *Client) bearerToken(ctx context.Context, override string) (string, error) {
	if err := ctx.Err(); err != nil {
		return "", err
	}
	if override != "" || c.tokenSource == nil {
		return cmp.Or(override, c.token), nil
	}
	token, err := c.tokenSource.Token(ctx)
	if err != nil {
		return "", fmt.Errorf("terrabase: acquire access token: %w", err)
	}
	if !jwtPattern.MatchString(token) {
		return "", errors.New("terrabase: TokenSource must return a ts-auth project access token")
	}
	return token, nil
}

func (c *Client) invalidateToken(token string, refusal *APIError, refreshed bool) bool {
	if refusal.Code == ErrKeyRevoked || refusal.Code == ErrKeyExpired {
		if source, ok := c.tokenSource.(interface{ Reject(string, error) }); ok {
			source.Reject(token, refusal)
		} else if source, ok := c.tokenSource.(interface{ Invalidate(string) }); ok {
			source.Invalidate(token)
		}
		return false
	}
	if refreshed {
		return false
	}
	if source, ok := c.tokenSource.(interface{ Invalidate(string) }); ok {
		source.Invalidate(token)
		return true
	}
	return false
}

func parseBaseURL(raw string) (*url.URL, error) {
	u, err := url.Parse(raw)
	if err != nil || u.Host == "" {
		return nil, fmt.Errorf("terrabase: BaseURL %q is not an absolute URL", raw)
	}
	if u.User != nil || u.RawQuery != "" || u.ForceQuery || u.Fragment != "" {
		return nil, errors.New("terrabase: BaseURL must not carry user info, a query or a fragment")
	}
	switch u.Scheme {
	case "https":
	case "http":
		host := u.Hostname()
		if ip := net.ParseIP(host); host != "localhost" && (ip == nil || !ip.IsLoopback()) {
			return nil, errors.New("terrabase: plain HTTP BaseURL is allowed only for loopback hosts")
		}
	default:
		return nil, fmt.Errorf("terrabase: BaseURL scheme %q is not https", u.Scheme)
	}
	escaped := u.EscapedPath()
	if strings.HasSuffix(escaped, "/") {
		u.Path = strings.TrimSuffix(u.Path, "/")
		u.RawPath = strings.TrimSuffix(escaped, "/")
	}
	return u, nil
}

type request struct {
	method      string
	path        []string // unescaped path segments after BaseURL
	query       url.Values
	body        []byte
	contentType string
	header      http.Header
	// token replaces the client's bearer token for this request.
	token string
	// replaySafe permits retries after 429 or a known transient rejection:
	// reads, read-only POSTs and If-Match writes.
	replaySafe bool
	// readOnly marks a read-only POST. GET is implicitly read-only. Only
	// reads may retry potentially committed outcomes or transport failures.
	readOnly bool
}

type response struct {
	status int
	header http.Header
	body   []byte
}

// url escapes protocol path segments; document IDs travel in the query.
func (c *Client) url(ctx context.Context, segments []string, query url.Values) (string, error) {
	for _, s := range segments {
		if s == "" || s == "." || s == ".." || strings.Contains(s, "/") {
			return "", fmt.Errorf("terrabase: path segment %q is empty, a dot segment or contains a slash", s)
		}
	}
	raw, err := c.resolver.Resolve(ctx)
	if err != nil {
		return "", fmt.Errorf("terrabase: resolve endpoint: %w", err)
	}
	base, err := parseBaseURL(raw)
	if err != nil {
		return "", err
	}
	u := *base
	u.RawPath = u.EscapedPath()
	for _, s := range segments {
		u.Path += "/" + s
		u.RawPath += "/" + url.PathEscape(s)
	}
	u.RawQuery = query.Encode()
	return u.String(), nil
}

// do sends req with the client's retry policy and returns a 2xx response or
// an error. Non-2xx statuses become *APIError.
func (c *Client) do(ctx context.Context, req request) (*response, error) {
	ctx, cancel := context.WithTimeout(ctx, OperationTimeout)
	defer cancel()
	if len(req.body) > MaxRequestBytes {
		return nil, fmt.Errorf("terrabase: request body is %d bytes, over the %d byte limit", len(req.body), MaxRequestBytes)
	}
	target, err := c.url(ctx, req.path, req.query)
	if err != nil {
		return nil, err
	}
	readOnly := req.method == http.MethodGet || req.readOnly
	refreshed := false
	for attempt := 0; ; attempt++ {
		outbound := req
		outbound.token, err = c.bearerToken(ctx, req.token)
		if err != nil {
			return nil, err
		}
		resp, err := c.send(ctx, outbound, target)
		if err != nil {
			if resp != nil && (resp.status < 200 || resp.status >= 300) {
				apiErr := parseAPIError(resp.status, resp.header, resp.body)
				apiErr.cause = err
				return resp, apiErr
			}
			if !readOnly {
				return nil, fmt.Errorf("%w: %w", ErrCommitUncertain, err)
			}
			if ctx.Err() != nil || !req.replaySafe || attempt >= c.maxRetries {
				return nil, err
			}
			if err := sleep(ctx, backoff(attempt)); err != nil {
				return nil, err
			}
			continue
		}
		if resp.status >= 200 && resp.status < 300 {
			return resp, nil
		}
		// Authentication refuses the request before an operation can execute.
		if resp.status == http.StatusUnauthorized && req.token == "" && c.invalidateToken(outbound.token, parseAPIError(resp.status, resp.header, resp.body), refreshed) {
			refreshed = true
			continue
		}
		apiErr := parseAPIError(resp.status, resp.header, resp.body)
		if !readOnly && errors.Is(apiErr, ErrCommitUncertain) {
			return resp, apiErr
		}
		if delay, ok := retryDelay(apiErr, attempt, req.replaySafe); ok && attempt < c.maxRetries {
			if err := waitRetry(ctx, delay, apiErr); err != nil {
				return nil, err
			}
			continue
		}
		return resp, apiErr
	}
}

// retryDelay implements the retry policy for requests that are safe to
// replay (reads, queries and If-Match writes): 429 and the transient 503s
// capacity, storage_unavailable, dependencies_unavailable, commit_uncertain,
// remote_region_unavailable, confirmation_busy and catalog_stale are retried. do excludes uncertain mutation outcomes.
// stored_corruption and 500 internal_error are never retried.
func retryDelay(e *APIError, attempt int, replaySafe bool) (time.Duration, bool) {
	if !replaySafe || (!(e.Status == http.StatusTooManyRequests && e.Code == ErrRateLimited) && !isTransient503(e)) {
		return 0, false
	}
	if e.retryAfterSet {
		return e.RetryAfter, true
	}
	return backoff(attempt), true
}

func isTransient503(e *APIError) bool {
	if e.Status != http.StatusServiceUnavailable {
		return false
	}
	switch e.Code {
	case ErrCapacity, ErrStorageUnavailable, ErrDependenciesUnavail, ErrCommitUncertain,
		ErrRemoteRegionUnavailable, ErrConfirmationBusy, ErrCatalogStale, ErrAuthUnavailable:
		return true
	}
	return false
}

func backoff(attempt int) time.Duration {
	d := min(100*time.Millisecond<<min(attempt, 5), 2*time.Second)
	return d/2 + mrand.N(d/2+1)
}

func sleep(ctx context.Context, d time.Duration) error {
	if d <= 0 {
		return ctx.Err()
	}
	t := time.NewTimer(d)
	defer t.Stop()
	select {
	case <-ctx.Done():
		return ctx.Err()
	case <-t.C:
		return nil
	}
}

// send performs one HTTP exchange and reads the whole body (bounded).
func (c *Client) send(ctx context.Context, req request, target string) (*response, error) {
	method := req.method
	var rd io.Reader
	if req.body != nil {
		rd = bytes.NewReader(req.body)
	}
	hr, err := http.NewRequestWithContext(ctx, method, target, rd)
	if err != nil {
		return nil, fmt.Errorf("terrabase: build request: %w", err)
	}
	for k, v := range req.header {
		hr.Header[k] = v
	}
	if req.header.Get("X-TerraBase-Peer-Credential") == "" {
		if token := cmp.Or(req.token, c.token); token != "" {
			hr.Header.Set("Authorization", "Bearer "+token)
		}
	}
	hr.Header.Set("Accept", "application/json")
	if req.body != nil {
		hr.Header.Set("Content-Type", cmp.Or(req.contentType, "application/json"))
	}
	res, err := c.hc.Do(hr)
	if err != nil {
		return nil, fmt.Errorf("terrabase: %s %s: %w", method, hr.URL.Path, err)
	}
	defer res.Body.Close()
	data, err := io.ReadAll(io.LimitReader(res.Body, c.maxResponse+1))
	resp := &response{status: res.StatusCode, header: res.Header, body: data}
	if err != nil {
		return resp, fmt.Errorf("terrabase: read %s %s response: %w", method, hr.URL.Path, err)
	}
	if int64(len(data)) > c.maxResponse {
		resp.body = data[:c.maxResponse]
		return resp, fmt.Errorf("terrabase: %s %s response exceeds %d bytes", method, hr.URL.Path, c.maxResponse)
	}
	return resp, nil
}

// call sends a JSON request and decodes a JSON response into out (if non-nil).
func (c *Client) call(ctx context.Context, req request, in, out any) (*response, error) {
	if in != nil {
		body, err := json.Marshal(in)
		if err != nil {
			return nil, fmt.Errorf("terrabase: encode request: %w", err)
		}
		req.body = body
	}
	resp, err := c.do(ctx, req)
	if err != nil {
		return resp, err
	}
	if out != nil {
		if bytes.Equal(bytes.TrimSpace(resp.body), []byte("null")) {
			return resp, errors.New("terrabase: expected a JSON response, received null")
		}
		if err := decodeExact(resp.body, out); err != nil {
			return resp, fmt.Errorf("terrabase: decode %s response: %w", req.method, err)
		}
		if err := entityid.Model(out); err != nil {
			return resp, fmt.Errorf("terrabase: invalid response: %w", err)
		}
	}
	return resp, nil
}

// decodeExact decodes JSON with UseNumber so numbers stay exact.
func decodeExact(data []byte, v any) error {
	dec := json.NewDecoder(bytes.NewReader(data))
	dec.UseNumber()
	if err := dec.Decode(v); err != nil {
		return err
	}
	var trailing any
	if err := dec.Decode(&trailing); !errors.Is(err, io.EOF) {
		return errors.New("trailing data after JSON value")
	}
	return nil
}

func parseRetryAfter(h http.Header) (time.Duration, bool) {
	v := strings.TrimSpace(h.Get("Retry-After"))
	if v == "" {
		return 0, false
	}
	if s, err := strconv.ParseUint(v, 10, 64); err == nil {
		if s > uint64((1<<63-1)/int64(time.Second)) {
			return time.Duration(1<<63 - 1), true
		}
		return time.Duration(s) * time.Second, true
	}
	if strings.Trim(v, "0123456789") == "" {
		return time.Duration(1<<63 - 1), true
	}
	if t, err := http.ParseTime(v); err == nil {
		return max(time.Until(t), 0), true
	}
	return 0, false
}
