// Package v1 is the name-based TerraBase v1 client (terrascale.tech/sdk/v1).
//
// It talks to the C# TerraBase server: the Documents API v1 (databases,
// collections, documents, queries and transactions under
// /v1/projects/{project}/databases/{database}) with a tb_ API key, and the
// Management API (/v1/management) as a ts-auth user.
//
// Document values are never decoded through float64: documents are returned
// as json.RawMessage, and Decode helpers use json.Decoder.UseNumber, so
// numbers keep their exact decimal text as json.Number.
//
// A data-plane client is created with New and an API key; the Management API
// uses NewManagement and a user's ts-auth access token:
//
//	c, err := v1.New(v1.Config{BaseURL: "https://gru.terrabase.example", Token: apiKey})
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
	// MaxRequestBytes is the server's request body limit (api-v1.md).
	MaxRequestBytes = 4 << 20
	// DefaultMaxResponseBytes bounds how much of one response body is read.
	DefaultMaxResponseBytes int64 = 64 << 20
	// DefaultMaxRetries is the retry budget for 429 and retryable 503s.
	DefaultMaxRetries = 3
	// MaxRetryWait caps a server-provided Retry-After.
	MaxRetryWait = 30 * time.Second
)

// Config configures a Client or a Management client.
type Config struct {
	// BaseURL is the TerraBase origin, for example https://gru.terrabase.example.
	// It must be HTTPS; plain HTTP is accepted only for loopback hosts. A path
	// prefix is kept; a query, fragment or user info is rejected.
	BaseURL string
	// Resolver supplies the endpoint for each call. Set either Resolver or
	// BaseURL; BaseURL uses a StaticResolver. Resolver must be safe for
	// concurrent use. Resolved URLs follow the same rules as BaseURL.
	Resolver Resolver
	// Token is the tb_ API key for Client. For Management it is empty, or
	// the management service credential (admin listener only, see As).
	Token string
	// HTTPClient is used for every request. Nil uses a client that requires
	// TLS 1.3. The client is copied and its redirect policy replaced, so the
	// bearer token is never forwarded to a redirect target.
	HTTPClient *http.Client
	// MaxRetries bounds retries. TerraBase does not deduplicate retries, so
	// only requests that are safe to replay are resent: reads, queries and
	// writes with an If-Match version, which a replay cannot apply twice
	// (If-None-Match: * is not enough, a tombstone satisfies it). For those,
	// 429 rate_limited is retried after Retry-After, and the transient 503s
	// (capacity, storage_unavailable, dependencies_unavailable) with backoff.
	// Transport errors and commit_uncertain are retried only for reads and
	// queries. A mutation's uncertain outcome is returned without a replay;
	// transport failures also match ErrCommitUncertain through errors.Is.
	// stored_corruption and 500 internal_error are never retried.
	// Zero means DefaultMaxRetries; negative disables retries.
	MaxRetries int
	// MaxResponseBytes bounds one response body. Zero means
	// DefaultMaxResponseBytes.
	MaxResponseBytes int64
}

// Client is a data-plane client for one API key. It is safe for concurrent use.
type Client struct {
	resolver    Resolver
	token       string
	hc          *http.Client
	maxRetries  int
	maxResponse int64
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

var tokenPattern = regexp.MustCompile(`^tb_[A-Za-z0-9_-]{43}$`)

// New returns a data-plane client.
func New(cfg Config) (*Client, error) {
	if !tokenPattern.MatchString(cfg.Token) {
		return nil, errors.New("terrabase: Token must be a tb_ API key")
	}
	return newClient(cfg)
}

func newClient(cfg Config) (*Client, error) {
	if cfg.Resolver != nil && cfg.BaseURL != "" {
		return nil, errors.New("terrabase: set either Resolver or BaseURL")
	}
	if cfg.Resolver == nil {
		if _, err := parseBaseURL(cfg.BaseURL); err != nil {
			return nil, err
		}
		cfg.Resolver = StaticResolver(cfg.BaseURL)
	}
	hc := &http.Client{Transport: &http.Transport{
		Proxy:             http.ProxyFromEnvironment,
		TLSClientConfig:   &tls.Config{MinVersion: tls.VersionTLS13},
		ForceAttemptHTTP2: true,
		IdleConnTimeout:   90 * time.Second,
	}}
	if cfg.HTTPClient != nil {
		copied := *cfg.HTTPClient
		hc = &copied
	}
	hc.CheckRedirect = func(*http.Request, []*http.Request) error { return http.ErrUseLastResponse }
	c := &Client{resolver: cfg.Resolver, token: cfg.Token, hc: hc, maxRetries: cfg.MaxRetries, maxResponse: cfg.MaxResponseBytes}
	switch {
	case c.maxRetries == 0:
		c.maxRetries = DefaultMaxRetries
	case c.maxRetries < 0:
		c.maxRetries = 0
	}
	if c.maxResponse <= 0 {
		c.maxResponse = DefaultMaxResponseBytes
	}
	return c, nil
}

func parseBaseURL(raw string) (*url.URL, error) {
	u, err := url.Parse(raw)
	if err != nil || u.Host == "" {
		return nil, fmt.Errorf("terrabase: BaseURL %q is not an absolute URL", raw)
	}
	if u.User != nil || u.RawQuery != "" || u.Fragment != "" {
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
	if len(req.body) > MaxRequestBytes {
		return nil, fmt.Errorf("terrabase: request body is %d bytes, over the %d byte limit", len(req.body), MaxRequestBytes)
	}
	target, err := c.url(ctx, req.path, req.query)
	if err != nil {
		return nil, err
	}
	readOnly := req.method == http.MethodGet || req.readOnly
	for attempt := 0; ; attempt++ {
		resp, err := c.send(ctx, req, target)
		if err != nil {
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
		apiErr := parseAPIError(resp.status, resp.header, resp.body)
		if !readOnly && apiErr.Code == ErrCommitUncertain {
			return resp, apiErr
		}
		if delay, ok := retryDelay(apiErr, attempt, req.replaySafe); ok && attempt < c.maxRetries {
			if err := sleep(ctx, delay); err != nil {
				return nil, err
			}
			continue
		}
		return resp, apiErr
	}
}

// retryDelay implements the retry policy for requests that are safe to
// replay (reads, queries and If-Match writes): 429 and the transient 503s
// capacity, storage_unavailable, dependencies_unavailable and
// commit_uncertain are retried. do excludes uncertain mutation outcomes.
// stored_corruption and 500 internal_error are never retried.
func retryDelay(e *APIError, attempt int, replaySafe bool) (time.Duration, bool) {
	if !replaySafe || (e.Status != http.StatusTooManyRequests && !isTransient503(e)) {
		return 0, false
	}
	if e.retryAfterSet {
		return min(e.RetryAfter, MaxRetryWait), true
	}
	return backoff(attempt), true
}

func isTransient503(e *APIError) bool {
	if e.Status != http.StatusServiceUnavailable {
		return false
	}
	switch e.Code {
	case ErrCapacity, ErrStorageUnavailable, ErrDependenciesUnavail, ErrCommitUncertain:
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
	hr.Header.Set("Authorization", "Bearer "+cmp.Or(req.token, c.token))
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
	if err != nil {
		return nil, fmt.Errorf("terrabase: read %s %s response: %w", method, hr.URL.Path, err)
	}
	if int64(len(data)) > c.maxResponse {
		return nil, fmt.Errorf("terrabase: %s %s response exceeds %d bytes", method, hr.URL.Path, c.maxResponse)
	}
	return &response{status: res.StatusCode, header: res.Header, body: data}, nil
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
	if out != nil && !(resp.status == http.StatusNoContent && len(bytes.TrimSpace(resp.body)) == 0) {
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
	if dec.More() {
		return errors.New("trailing data after JSON value")
	}
	return nil
}

func parseRetryAfter(h http.Header) (time.Duration, bool) {
	v := strings.TrimSpace(h.Get("Retry-After"))
	if v == "" {
		return 0, false
	}
	if s, err := strconv.ParseUint(v, 10, 32); err == nil {
		return time.Duration(s) * time.Second, true
	}
	if t, err := http.ParseTime(v); err == nil {
		return max(time.Until(t), 0), true
	}
	return 0, false
}
