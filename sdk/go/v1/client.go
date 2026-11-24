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
	"maps"
	mrand "math/rand/v2"
	"net"
	"net/http"
	"net/url"
	"os"
	"regexp"
	"strings"
	"sync"
	"time"

	"terrascale.tech/sdk/internal/entityid"
	"terrascale.tech/sdk/internal/httpx"
	"terrascale.tech/sdk/internal/origin"
	"terrascale.tech/sdk/tsauth"
)

const (
	DefaultBaseURL                = "https://api.terrascale.tech"
	MaxRequestBytes               = 4 << 20
	DefaultMaxResponseBytes int64 = 64 << 20
	DefaultMaxRetries             = 3
	OperationTimeout              = 30 * time.Second
)

type Config struct {
	APIKey        string
	BaseURL       string
	Resolver      Resolver
	Token         string
	TokenSource   TokenSource
	HTTPClient    *http.Client
	RegionPin     string
	TSAuthBaseURL string
}

type Client struct {
	apiKey      string
	authOrigin  string
	projects    *sync.Map
	resolver    Resolver
	token       string
	tokenSource TokenSource
	hc          *http.Client
	regionPin   string
	bookmarks   *sync.Map
}

type TokenSource interface {
	Token(context.Context) (string, error)
}

type Resolver interface {
	Resolve(context.Context) (string, error)
}

type StaticResolver string

func (r StaticResolver) Resolve(ctx context.Context) (string, error) {
	return string(r), ctx.Err()
}

var (
	tokenPattern = regexp.MustCompile(`^tb_[A-Za-z0-9_-]{43}$`)
	jwtPattern   = regexp.MustCompile(`^[A-Za-z0-9_-]+\.[A-Za-z0-9_-]+\.[A-Za-z0-9_-]+$`)
)

func New(cfg Config) (*Client, error) {
	if cfg.APIKey == "" && cfg.TokenSource == nil && !tokenPattern.MatchString(cfg.Token) && !jwtPattern.MatchString(cfg.Token) {
		return nil, errors.New("terrabase: Token must be a tb_ API key or a ts-auth project access token")
	}
	return newClient(cfg)
}

func NewFromEnvironment() (*Client, error) {
	key := os.Getenv("TERRASCALE_API_KEY")
	if key == "" {
		return nil, errors.New("terrabase: TERRASCALE_API_KEY is not set")
	}
	cfg := Config{APIKey: key}
	if base := os.Getenv("TERRASCALE_BASE_URL"); base != "" {
		cfg.BaseURL = base
	}
	return New(cfg)
}

func newClient(cfg Config) (*Client, error) {
	if cfg.APIKey != "" && (cfg.Token != "" || cfg.TokenSource != nil) {
		return nil, errors.New("terrabase: set APIKey, Token or TokenSource exclusively")
	}
	if cfg.Token != "" && cfg.TokenSource != nil {
		return nil, errors.New("terrabase: set either Token or TokenSource")
	}
	if cfg.Resolver != nil && cfg.BaseURL != "" {
		return nil, errors.New("terrabase: set either Resolver or BaseURL")
	}
	if cfg.TSAuthBaseURL != "" && cfg.APIKey == "" {
		return nil, errors.New("terrabase: TSAuthBaseURL exchanges an APIKey; set APIKey or remove TSAuthBaseURL")
	}
	authOrigin := ""
	if cfg.APIKey != "" {
		var err error
		if authOrigin, err = keyExchangeOrigin(cfg); err != nil {
			return nil, err
		}
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
		Proxy:               http.ProxyFromEnvironment,
		DialContext:         (&net.Dialer{Timeout: 10 * time.Second, KeepAlive: 30 * time.Second}).DialContext,
		TLSHandshakeTimeout: 10 * time.Second,
		TLSClientConfig:     &tls.Config{MinVersion: tls.VersionTLS13},
		ForceAttemptHTTP2:   true,
		IdleConnTimeout:     90 * time.Second,
	}}
	if cfg.HTTPClient != nil {
		copied := *cfg.HTTPClient
		hc = &copied
	}
	hc.CheckRedirect = func(*http.Request, []*http.Request) error { return http.ErrUseLastResponse }
	c := &Client{resolver: cfg.Resolver, token: cfg.Token, tokenSource: cfg.TokenSource, hc: hc, regionPin: strings.TrimSpace(cfg.RegionPin), bookmarks: new(sync.Map)}
	if cfg.APIKey != "" {
		source, err := tsauth.NewAPIKeySource(tsauth.APIKeyConfig{Issuer: authOrigin + "/terrascale", Key: cfg.APIKey, HTTPClient: hc})
		if err != nil {
			return nil, err
		}
		c.apiKey, c.authOrigin, c.projects, c.tokenSource = cfg.APIKey, authOrigin, new(sync.Map), source
	}
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

func keyExchangeOrigin(cfg Config) (string, error) {
	if cfg.TSAuthBaseURL != "" {
		authOrigin, err := origin.Parse(cfg.TSAuthBaseURL, origin.Auth)
		if err != nil {
			return "", fmt.Errorf("terrabase: TSAuthBaseURL %w", err)
		}
		return authOrigin, nil
	}
	if cfg.Resolver == nil {
		base, err := parseBaseURL(cmp.Or(cfg.BaseURL, DefaultBaseURL))
		if err != nil {
			return "", err
		}
		if base.String() == DefaultBaseURL {
			return origin.Auth.Default(), nil
		}
	}
	return "", errors.New("terrabase: set TSAuthBaseURL to the ts-auth that exchanges the APIKey: with a TerraBase origin other than https://api.terrascale.tech the key is not sent to production ts-auth")
}

func parseBaseURL(raw string) (*url.URL, error) {
	base, err := origin.Parse(raw, origin.API)
	if err != nil {
		return nil, fmt.Errorf("terrabase: BaseURL %w", err)
	}
	return url.Parse(base)
}

type request struct {
	method     string
	path       []string
	query      url.Values
	body       []byte
	header     http.Header
	token      string
	replaySafe bool
}

type response struct {
	status int
	header http.Header
	body   []byte
}

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

func (c *Client) do(ctx context.Context, req request) (*response, error) {
	ctx, cancel := context.WithTimeout(ctx, OperationTimeout)
	defer cancel()
	if len(req.path) >= 3 && req.path[0] == "v1" && req.path[1] == "projects" {
		var err error
		c, err = c.projectClient(req.path[2])
		if err != nil {
			return nil, err
		}
	}
	if len(req.body) > MaxRequestBytes {
		return nil, fmt.Errorf("terrabase: request body is %d bytes, over the %d byte limit", len(req.body), MaxRequestBytes)
	}
	target, err := c.url(ctx, req.path, req.query)
	if err != nil {
		return nil, err
	}
	readOnly := req.method == http.MethodGet
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
			if ctx.Err() != nil || !req.replaySafe || attempt >= DefaultMaxRetries {
				return nil, err
			}
			if err := httpx.Wait(ctx, backoff(attempt)); err != nil {
				return nil, err
			}
			continue
		}
		if resp.status >= 200 && resp.status < 300 {
			return resp, nil
		}
		apiErr := parseAPIError(resp.status, resp.header, resp.body)
		if errors.Is(apiErr, ErrCommitUncertain) {
			return resp, apiErr
		}
		if resp.status == http.StatusUnauthorized && req.token == "" && c.invalidateToken(outbound.token, apiErr, refreshed) {
			refreshed = true
			continue
		}
		if delay, ok := retryDelay(apiErr, attempt, req.replaySafe, readOnly); ok && attempt < DefaultMaxRetries {
			if err := waitRetry(ctx, delay, apiErr); err != nil {
				return nil, err
			}
			continue
		}
		return resp, apiErr
	}
}

func retryDelay(e *APIError, attempt int, replaySafe, readOnly bool) (time.Duration, bool) {
	if !replaySafe || (!(e.Status == http.StatusTooManyRequests && (e.Code == ErrRateLimited || e.Code == ErrTenantBusy)) && !isTransient503(e) && !(readOnly && isGatewayFailure(e))) {
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
	case ErrCapacity, ErrStorageUnavailable, ErrDependenciesUnavail, ErrCatalogStale, ErrAuthUnavailable:
		return true
	}
	return false
}

func isGatewayFailure(e *APIError) bool {
	return e.Status == http.StatusBadGateway || e.Status == http.StatusGatewayTimeout
}

func backoff(attempt int) time.Duration {
	d := min(100*time.Millisecond<<min(attempt, 5), 2*time.Second)
	return d/2 + mrand.N(d/2+1)
}

func (c *Client) pin(h http.Header) {
	if c.regionPin != "" && h.Get("X-TerraScale-Region-Pin") == "" {
		h.Set("X-TerraScale-Region-Pin", c.regionPin)
	}
}

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
	maps.Copy(hr.Header, req.header)
	if req.token != "" && req.header.Get("X-TerraBase-Peer-Credential") == "" {
		hr.Header.Set("Authorization", "Bearer "+req.token)
	}
	hr.Header.Set("Accept", "application/json")
	c.pin(hr.Header)
	if req.body != nil {
		hr.Header.Set("Content-Type", "application/json")
	}
	res, err := c.hc.Do(hr)
	if err != nil {
		return nil, fmt.Errorf("terrabase: %s %s: %w", method, hr.URL.Path, err)
	}
	defer res.Body.Close()
	data, err := io.ReadAll(io.LimitReader(res.Body, DefaultMaxResponseBytes+1))
	resp := &response{status: res.StatusCode, header: res.Header, body: data}
	if err != nil {
		return resp, fmt.Errorf("terrabase: read %s %s response: %w", method, hr.URL.Path, err)
	}
	if int64(len(data)) > DefaultMaxResponseBytes {
		resp.body = data[:DefaultMaxResponseBytes]
		return resp, fmt.Errorf("terrabase: %s %s response exceeds %d bytes", method, hr.URL.Path, DefaultMaxResponseBytes)
	}
	return resp, nil
}

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
