// Package v1 is the name-based TerraBase v1 client (terrascale.tech/sdk/v1).
//
// It implements the frozen v1 contracts: the Documents API (documents,
// transactions, queries), shapes (reactive queries served over long-poll, SSE
// or the edge) and the /v1/dashboard control plane. It lives beside the
// UUID/shard native client in terrascale.tech/sdk, which stays until the old
// routes are removed from tb-server.
//
// Document values are never decoded through float64: documents are returned
// as json.RawMessage, and Decode helpers use json.Decoder.UseNumber, so
// numbers keep their exact decimal text as json.Number.
//
// A data-plane client is created with New and a data key; the control plane
// uses NewDashboard with a control key:
//
//	c, err := v1.New(v1.Config{BaseURL: "https://gru.terrabase.example", Token: token})
//	users := c.Database("main").Collection("users")
//	doc, err := users.Get(ctx, id)
package v1

import (
	"bytes"
	"context"
	"crypto/rand"
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

// Config configures a Client or a Dashboard.
type Config struct {
	// BaseURL is the TerraBase origin, for example https://gru.terrabase.example.
	// It must be HTTPS; plain HTTP is accepted only for loopback hosts. A path
	// prefix is kept; a query, fragment or user info is rejected.
	BaseURL string
	// Token is the tbk1 API key: a data key for Client, a control key for
	// Dashboard.
	Token string
	// HTTPClient is used for every request. Nil uses a client that requires
	// TLS 1.3. The client is copied and its redirect policy replaced, so the
	// bearer token is never forwarded to a redirect target.
	HTTPClient *http.Client
	// MaxRetries bounds retries. 429 rate_limited is retried after
	// Retry-After; 503 commit_uncertain and transport errors are retried only
	// when the request is safe to replay (reads, queries and mutations with an
	// Idempotency-Key). Zero means DefaultMaxRetries; negative disables retries.
	MaxRetries int
	// MaxResponseBytes bounds one response body. Zero means
	// DefaultMaxResponseBytes.
	MaxResponseBytes int64
}

// Client is a data-plane client for one data key. It is safe for concurrent use.
type Client struct {
	base        *url.URL
	token       string
	hc          *http.Client
	maxRetries  int
	maxResponse int64
}

var tokenPattern = regexp.MustCompile(`^tbk1\.[A-Za-z0-9_-]{22}\.[A-Za-z0-9_-]{43}$`)

// New returns a data-plane client.
func New(cfg Config) (*Client, error) {
	base, err := parseBaseURL(cfg.BaseURL)
	if err != nil {
		return nil, err
	}
	if !tokenPattern.MatchString(cfg.Token) {
		return nil, errors.New("terrabase: Token must be a tbk1.<id>.<secret> key")
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
	c := &Client{base: base, token: cfg.Token, hc: hc, maxRetries: cfg.MaxRetries, maxResponse: cfg.MaxResponseBytes}
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
	u.Path = strings.TrimSuffix(u.Path, "/")
	u.RawPath = ""
	return u, nil
}

// NewIdempotencyKey returns a random key that satisfies the Idempotency-Key
// grammar. Keep the key and reuse it when retrying the same mutation.
func NewIdempotencyKey() string { return rand.Text() }

var idempotencyPattern = regexp.MustCompile(`^[A-Za-z0-9_.-]{1,64}$`)

func validIdempotencyKey(key string) error {
	if !idempotencyPattern.MatchString(key) {
		return fmt.Errorf("terrabase: Idempotency-Key %q must be 1-64 characters of [A-Za-z0-9_.-]", key)
	}
	return nil
}

type request struct {
	method      string
	path        []string // unescaped path segments after BaseURL
	query       url.Values
	body        []byte
	contentType string
	header      http.Header
	idemKey     string
	// replaySafe marks requests that may be resent after a transport error:
	// reads, read-only POSTs and mutations carrying an Idempotency-Key.
	replaySafe bool
}

type response struct {
	status int
	header http.Header
	body   []byte
}

func (c *Client) url(segments []string, query url.Values) string {
	u := c.base.JoinPath(segments...)
	u.RawQuery = query.Encode()
	return u.String()
}

// do sends req with the client's retry policy and returns a 2xx response or
// an error. Non-2xx statuses become *APIError.
func (c *Client) do(ctx context.Context, req request) (*response, error) {
	if len(req.body) > MaxRequestBytes {
		return nil, fmt.Errorf("terrabase: request body is %d bytes, over the %d byte limit", len(req.body), MaxRequestBytes)
	}
	if req.idemKey != "" {
		if err := validIdempotencyKey(req.idemKey); err != nil {
			return nil, err
		}
		req.replaySafe = true
	}
	target := c.url(req.path, req.query)
	for attempt := 0; ; attempt++ {
		resp, err := c.send(ctx, req.method, target, c.token, req.body, req.contentType, req.header, req.idemKey)
		if err != nil {
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
		if delay, ok := retryDelay(apiErr, attempt, req.idemKey != ""); ok && attempt < c.maxRetries {
			if err := sleep(ctx, delay); err != nil {
				return nil, err
			}
			continue
		}
		return resp, apiErr
	}
}

// retryDelay implements the retry policy: 429 is always retried (the request
// was not admitted); 503 commit_uncertain is retried only with the same
// Idempotency-Key, which makes the retry return the original outcome.
func retryDelay(e *APIError, attempt int, keyed bool) (time.Duration, bool) {
	switch {
	case e.Status == http.StatusTooManyRequests:
	case e.Status == http.StatusServiceUnavailable && e.Code == ErrCommitUncertain && keyed:
	default:
		return 0, false
	}
	if e.retryAfterSet {
		return min(e.RetryAfter, MaxRetryWait), true
	}
	return backoff(attempt), true
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
func (c *Client) send(ctx context.Context, method, target, token string, body []byte, contentType string, header http.Header, idemKey string) (*response, error) {
	var rd io.Reader
	if body != nil {
		rd = bytes.NewReader(body)
	}
	hr, err := http.NewRequestWithContext(ctx, method, target, rd)
	if err != nil {
		return nil, fmt.Errorf("terrabase: build request: %w", err)
	}
	for k, v := range header {
		hr.Header[k] = v
	}
	hr.Header.Set("Authorization", "Bearer "+token)
	hr.Header.Set("Accept", "application/json")
	if body != nil {
		hr.Header.Set("Content-Type", cmpOr(contentType, "application/json"))
	}
	if idemKey != "" {
		hr.Header.Set("Idempotency-Key", idemKey)
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

func cmpOr(a, b string) string {
	if a != "" {
		return a
	}
	return b
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
