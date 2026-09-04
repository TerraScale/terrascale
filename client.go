// Package sdk is the TerraScale Go SDK (terrascale.tech/sdk).
//
// Core surface (GOAL D-13): document CRUD, typed schema builder, typed SQL
// builder plus raw parameterized SQL, subscribe/execute with checkpoints and
// typed resets, region pinning, tenants, and management.
//
// Management routes mirror the ts-typescript src/client.ts path templates
// (sdk-route-manifest.json, routeCount 60). Document, schema, SQL, reactive,
// and tenant paths are a hand-written stub against /api/v1 pending the
// published terrabase openapi.json; see contract.go.
package sdk

import (
	"bytes"
	"context"
	"encoding/json"
	"fmt"
	"io"
	"net/http"
	"net/url"
	"strconv"
	"strings"
	"sync"
	"time"
)

// RegionCode is a TerraScale region identifier.
type RegionCode string

// Launch regions (GOAL D-05).
const (
	RegionGRU RegionCode = "GRU"
	RegionSFO RegionCode = "SFO"
	RegionFRA RegionCode = "FRA"
)

// IsRegionCode reports whether s is a known region code (case-insensitive).
func IsRegionCode(s string) bool {
	switch RegionCode(strings.ToUpper(strings.TrimSpace(s))) {
	case RegionGRU, RegionSFO, RegionFRA:
		return true
	default:
		return false
	}
}

// ParseRegionCode normalizes s to a canonical RegionCode or returns an error.
func ParseRegionCode(s string) (RegionCode, error) {
	code := RegionCode(strings.ToUpper(strings.TrimSpace(s)))
	if !IsRegionCode(string(code)) {
		return "", fmt.Errorf("sdk: region must be exactly one of GRU, SFO, FRA, got %q", s)
	}
	return code, nil
}

// RegionAffinity controls how the client routes requests across regions.
type RegionAffinity string

const (
	// AffinityFailover sends a preferred-region hint and follows one 421
	// Misdirected Request with X-TerraScale-Placement to repair pinning.
	AffinityFailover RegionAffinity = "failover"
	// AffinityStrict pins REST to <region>.api.terrascale.tech.
	AffinityStrict RegionAffinity = "strict"
)

// Consistency selects per-request read consistency (GOAL D-37).
type Consistency string

const (
	// ConsistencyDefault is read-your-writes + monotonic reads.
	ConsistencyDefault Consistency = ""
	// ConsistencyEventual allows stale replica reads.
	ConsistencyEventual Consistency = "eventual"
	// ConsistencyWritesFollowReads requires the serving replica to have
	// applied the client's session frontier.
	ConsistencyWritesFollowReads Consistency = "writes-follow-reads"
)

// DefaultAPIBaseURL is the first-contact REST entry point (GOAL D-24/D-58).
const DefaultAPIBaseURL = "https://api.terrascale.tech"

// Config configures a Client.
type Config struct {
	// APIBaseURL overrides the default first-contact URL (tests, proxies).
	APIBaseURL string
	// AccessToken is a static credential. TokenProvider takes precedence.
	AccessToken string
	// TokenProvider returns the credential dynamically, if set.
	TokenProvider func() string
	// ProjectID is sent as X-TerraScale-Project-ID when non-empty.
	ProjectID string
	// PreferredRegion is the failover hint or strict pin target.
	PreferredRegion RegionCode
	// Affinity defaults to failover.
	Affinity RegionAffinity
	// SessionToken is the client's causal frontier (GOAL D-37).
	SessionToken string
	// HTTPClient defaults to one with a 30s timeout.
	HTTPClient *http.Client
	// MaxRetryAttempts is retries for idempotent mutations on transient
	// statuses (0-8, default 0).
	MaxRetryAttempts int
	// RetryDelay is the base retry backoff (default 250ms, max 30s).
	RetryDelay time.Duration
}

// Client is the TerraScale SDK entry point.
type Client struct {
	cfg  Config
	http *http.Client

	mu     sync.RWMutex
	pinned RegionCode

	Documents        *DocumentsClient
	Tenants          *TenantsClient
	Onboarding       *OnboardingClient
	Organizations    *OrganizationsClient
	Regions          *RegionsClient
	SavedQueries     *SavedQueriesClient
	Databases        *DatabasesClient
	DatabaseBranches *DatabaseBranchesClient
	Backups          *BackupsClient
	Access           *AccessClient
	Usage            *UsageClient
	Audit            *AuditClient
	Notifications    *NotificationsClient
}

// NewClient validates cfg and returns a ready Client.
func NewClient(cfg Config) (*Client, error) {
	base := strings.TrimSpace(cfg.APIBaseURL)
	if base == "" {
		base = DefaultAPIBaseURL
	}
	base = strings.TrimSuffix(base, "/")
	if base == "" {
		return nil, fmt.Errorf("sdk: API base URL cannot be empty")
	}
	u, err := url.Parse(base + "/")
	if err != nil {
		return nil, fmt.Errorf("sdk: invalid API base URL %q: %w", cfg.APIBaseURL, err)
	}
	lowerHost := strings.ToLower(u.Hostname())
	if strings.Contains(lowerHost, "tbep") || strings.Contains(lowerHost, "terrabase") {
		return nil, fmt.Errorf("sdk: clients may target only the public API, not TBEP or TerraBase")
	}
	lowerPath := strings.ToLower(u.EscapedPath())
	if strings.Contains(lowerPath, "tbep") || strings.Contains(lowerPath, "terrabase") {
		return nil, fmt.Errorf("sdk: clients may target only the public API, not TBEP or TerraBase")
	}

	affinity := cfg.Affinity
	if affinity == "" {
		affinity = AffinityFailover
	}
	if affinity != AffinityFailover && affinity != AffinityStrict {
		return nil, fmt.Errorf("sdk: affinity must be %q or %q", AffinityFailover, AffinityStrict)
	}
	preferred := RegionCode(strings.ToUpper(strings.TrimSpace(string(cfg.PreferredRegion))))
	if preferred != "" && !IsRegionCode(string(preferred)) {
		return nil, fmt.Errorf("sdk: preferred region must be exactly one of GRU, SFO, FRA")
	}
	if affinity == AffinityStrict && preferred == "" {
		return nil, fmt.Errorf("sdk: strict affinity requires a preferred region")
	}
	if cfg.MaxRetryAttempts < 0 || cfg.MaxRetryAttempts > 8 {
		return nil, fmt.Errorf("sdk: max retry attempts must be between 0 and 8")
	}
	retryDelay := cfg.RetryDelay
	if retryDelay == 0 {
		retryDelay = 250 * time.Millisecond
	}
	if retryDelay < 0 || retryDelay > 30*time.Second {
		return nil, fmt.Errorf("sdk: retry delay must be between 0 and 30s")
	}

	httpClient := cfg.HTTPClient
	if httpClient == nil {
		httpClient = &http.Client{Timeout: 30 * time.Second}
	}

	c := &Client{cfg: Config{
		APIBaseURL:       base,
		AccessToken:      cfg.AccessToken,
		TokenProvider:    cfg.TokenProvider,
		ProjectID:        cfg.ProjectID,
		PreferredRegion:  preferred,
		Affinity:         affinity,
		SessionToken:     cfg.SessionToken,
		HTTPClient:       httpClient,
		MaxRetryAttempts: cfg.MaxRetryAttempts,
		RetryDelay:       retryDelay,
	}, http: httpClient}
	c.Documents = &DocumentsClient{c: c}
	c.Tenants = &TenantsClient{c: c}
	c.Onboarding = &OnboardingClient{c: c}
	c.Organizations = &OrganizationsClient{c: c}
	c.Regions = &RegionsClient{c: c}
	c.SavedQueries = &SavedQueriesClient{c: c}
	c.Databases = &DatabasesClient{c: c}
	c.DatabaseBranches = &DatabaseBranchesClient{c: c}
	c.Backups = &BackupsClient{c: c}
	c.Access = &AccessClient{c: c}
	c.Usage = &UsageClient{c: c}
	c.Audit = &AuditClient{c: c}
	c.Notifications = &NotificationsClient{c: c}
	return c, nil
}

// PinRegion explicitly pins REST to region.
func (c *Client) PinRegion(region RegionCode) error {
	code, err := ParseRegionCode(string(region))
	if err != nil {
		return err
	}
	c.mu.Lock()
	c.pinned = code
	c.mu.Unlock()
	return nil
}

// ResetPin clears any learned or explicit region pin.
func (c *Client) ResetPin() {
	c.mu.Lock()
	c.pinned = ""
	c.mu.Unlock()
}

// PinnedRegion returns the current pin, or "" when unpinned.
func (c *Client) PinnedRegion() RegionCode {
	c.mu.RLock()
	defer c.mu.RUnlock()
	return c.pinned
}

// EffectiveRegion returns the pin, else the preferred region, else "".
func (c *Client) EffectiveRegion() RegionCode {
	c.mu.RLock()
	defer c.mu.RUnlock()
	if c.pinned != "" {
		return c.pinned
	}
	return c.cfg.PreferredRegion
}

// APIBaseURL returns the configured (unpinned) base URL.
func (c *Client) APIBaseURL() string { return c.cfg.APIBaseURL }

func (c *Client) setPinned(region RegionCode) {
	c.mu.Lock()
	c.pinned = region
	c.mu.Unlock()
}

func (c *Client) token() string {
	if c.cfg.TokenProvider != nil {
		if t := c.cfg.TokenProvider(); t != "" {
			return t
		}
	}
	return c.cfg.AccessToken
}

func (c *Client) currentBase() string {
	c.mu.RLock()
	pinned := c.pinned
	cfg := c.cfg
	c.mu.RUnlock()
	switch cfg.Affinity {
	case AffinityStrict:
		region := pinned
		if region == "" {
			region = cfg.PreferredRegion
		}
		return regionalizeHost(cfg.APIBaseURL, region)
	default:
		if pinned != "" {
			return regionalizeHost(cfg.APIBaseURL, pinned)
		}
		return cfg.APIBaseURL
	}
}

// regionalizeHost rewrites api.terrascale.tech to <region>.api.terrascale.tech.
// Custom hosts (tests, proxies) are returned unchanged; the region travels in
// the X-TerraScale-Preferred-Region header instead.
func regionalizeHost(base string, region RegionCode) string {
	if region == "" {
		return base
	}
	u, err := url.Parse(base)
	if err != nil {
		return base
	}
	if !strings.EqualFold(u.Hostname(), "api.terrascale.tech") {
		return base
	}
	host := strings.ToLower(string(region)) + ".api.terrascale.tech"
	if port := u.Port(); port != "" {
		host += ":" + port
	}
	u.Host = host
	return strings.TrimSuffix(u.String(), "/")
}

// requestConfig carries per-request options.
type requestConfig struct {
	idempotencyKey     string
	noIdempotency      bool
	regionHint         RegionCode
	sessionToken       string
	consistency        Consistency
	deleteConfirmation string
}

// RequestOption customizes one request.
type RequestOption func(*requestConfig)

// WithIdempotencyKey sets the idempotency key for a mutation (1-128 chars).
// Only mutations carrying a key are retried.
func WithIdempotencyKey(key string) RequestOption {
	return func(r *requestConfig) { r.idempotencyKey = key }
}

// WithoutIdempotency disables sending any idempotency key (and thus retries).
func WithoutIdempotency() RequestOption {
	return func(r *requestConfig) { r.noIdempotency = true }
}

// WithRegionHint sends a per-request region hint (failover).
func WithRegionHint(region RegionCode) RequestOption {
	return func(r *requestConfig) { r.regionHint = region }
}

// WithSessionToken overrides the client session token for one request.
func WithSessionToken(token string) RequestOption {
	return func(r *requestConfig) { r.sessionToken = token }
}

// WithConsistency overrides read consistency for one request.
func WithConsistency(cs Consistency) RequestOption {
	return func(r *requestConfig) { r.consistency = cs }
}

// WithDeleteConfirmation carries the database delete confirmation identity.
func WithDeleteConfirmation(id string) RequestOption {
	return func(r *requestConfig) { r.deleteConfirmation = id }
}

func resolveRequestOptions(opts []RequestOption) requestConfig {
	rc := requestConfig{}
	for _, opt := range opts {
		if opt != nil {
			opt(&rc)
		}
	}
	return rc
}

// APIError is a typed API failure.
type APIError struct {
	Route     string
	Method    string
	Path      string
	Status    int
	Code      string
	Message   string
	RequestID string
	Body      string
}

func (e *APIError) Error() string {
	return fmt.Sprintf("sdk: %s %s (%s): status %d code %s: %s",
		e.Method, e.Path, e.Route, e.Status, e.Code, e.Message)
}

func isMutation(method string) bool {
	switch method {
	case http.MethodPost, http.MethodPut, http.MethodPatch, http.MethodDelete:
		return true
	default:
		return false
	}
}

var retryableStatuses = map[int]bool{
	http.StatusRequestTimeout:      true,
	429:                            true,
	http.StatusInternalServerError: true,
	http.StatusBadGateway:          true,
	http.StatusServiceUnavailable:  true,
	http.StatusGatewayTimeout:      true,
}

// do performs one API call: path must stay under /api/v1.
func (c *Client) do(ctx context.Context, method, path string, query url.Values, body any, out any, route string, opts ...RequestOption) error {
	if !strings.HasPrefix(path, "/api/v1") {
		return fmt.Errorf("sdk: path %q is outside the public /api/v1 contract", path)
	}
	rc := resolveRequestOptions(opts)
	if rc.regionHint != "" && !IsRegionCode(string(rc.regionHint)) {
		return fmt.Errorf("sdk: region hint must be exactly one of GRU, SFO, FRA")
	}
	var idem string
	if isMutation(method) && !rc.noIdempotency && rc.idempotencyKey != "" {
		key := strings.TrimSpace(rc.idempotencyKey)
		if key == "" || len(key) > 128 {
			return fmt.Errorf("sdk: idempotency keys must be nonblank and at most 128 characters")
		}
		idem = key
	}
	var payload []byte
	if body != nil {
		var err error
		payload, err = json.Marshal(body)
		if err != nil {
			return fmt.Errorf("sdk: marshal %s request body: %w", route, err)
		}
	}

	attempt := 0
	followed421 := false
	for {
		base := c.currentBase()
		status, headers, respBody, transportErr := c.roundTrip(ctx, method, base, path, query, payload, rc, idem)
		if transportErr != nil {
			if idem != "" && attempt < c.cfg.MaxRetryAttempts {
				if sleepErr := sleepContext(ctx, c.backoff(nil, attempt)); sleepErr != nil {
					return sleepErr
				}
				attempt++
				continue
			}
			return fmt.Errorf("sdk: %s %s (%s): %w", method, path, route, transportErr)
		}
		if status == http.StatusMisdirectedRequest && !followed421 {
			if placement := placementOf(headers); IsRegionCode(placement) {
				code, _ := ParseRegionCode(placement)
				c.setPinned(code)
				followed421 = true
				continue
			}
		}
		if status >= 200 && status < 300 {
			return decodeSuccess(out, respBody, headers.Get("Content-Type"))
		}
		apiErr := errorFromResponse(route, method, path, status, headers, respBody)
		if idem != "" && attempt < c.cfg.MaxRetryAttempts && retryableStatuses[status] {
			if sleepErr := sleepContext(ctx, c.retryDelayFor(headers, attempt)); sleepErr != nil {
				return sleepErr
			}
			attempt++
			continue
		}
		return apiErr
	}
}

func (c *Client) roundTrip(ctx context.Context, method, base, path string, query url.Values, payload []byte, rc requestConfig, idem string) (int, http.Header, []byte, error) {
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
		h.Set("Content-Type", "application/json")
	}
	if c.cfg.ProjectID != "" {
		h.Set("X-TerraScale-Project-ID", c.cfg.ProjectID)
	}
	hint := rc.regionHint
	if hint == "" && c.cfg.Affinity == AffinityFailover && c.cfg.PreferredRegion != "" {
		hint = c.cfg.PreferredRegion
	}
	if hint != "" {
		h.Set("X-TerraScale-Preferred-Region", string(hint))
	}
	if token := c.token(); token != "" {
		if strings.HasPrefix(token, "Bearer ") {
			h.Set("Authorization", token)
		} else {
			h.Set("Authorization", "Bearer "+token)
		}
	}
	session := rc.sessionToken
	if session == "" {
		session = c.cfg.SessionToken
	}
	if session != "" {
		h.Set("X-TerraScale-Session-Token", session)
	}
	if rc.consistency != "" && rc.consistency != ConsistencyDefault {
		h.Set("X-TerraScale-Consistency", string(rc.consistency))
	}
	if rc.deleteConfirmation != "" {
		h.Set("X-TerraScale-Delete-Confirmation", rc.deleteConfirmation)
	}
	if idem != "" {
		h.Set("Idempotency-Key", idem)
	}

	resp, err := c.http.Do(req)
	if err != nil {
		return 0, nil, nil, err
	}
	defer resp.Body.Close()
	respBody, err := io.ReadAll(io.LimitReader(resp.Body, 8<<20))
	if err != nil {
		return 0, nil, nil, err
	}
	return resp.StatusCode, resp.Header, respBody, nil
}

func decodeSuccess(out any, body []byte, contentType string) error {
	if out == nil {
		return nil
	}
	if len(bytes.TrimSpace(body)) == 0 {
		return nil
	}
	if sp, ok := out.(*string); ok {
		*sp = string(body)
		return nil
	}
	trimmed := bytes.TrimSpace(body)
	if !strings.Contains(strings.ToLower(contentType), "json") &&
		!bytes.HasPrefix(trimmed, []byte("{")) && !bytes.HasPrefix(trimmed, []byte("[")) {
		return fmt.Errorf("sdk: non-JSON success body (content-type %q)", contentType)
	}
	if err := json.Unmarshal(body, out); err != nil {
		return fmt.Errorf("sdk: decode success body: %w", err)
	}
	return nil
}

func errorFromResponse(route, method, path string, status int, headers http.Header, body []byte) *APIError {
	apiErr := &APIError{
		Route:  route,
		Method: method,
		Path:   path,
		Status: status,
		Code:   defaultErrorCode(status),
		Body:   string(body),
	}
	var doc map[string]any
	if err := json.Unmarshal(body, &doc); err == nil {
		if v := firstString(doc, "code", "errorCode", "error_code"); v != "" {
			apiErr.Code = v
		}
		if v := firstString(doc, "message", "error", "detail", "title"); v != "" {
			apiErr.Message = v
		}
		if v := firstString(doc, "requestId", "request_id"); v != "" {
			apiErr.RequestID = v
		}
	} else if strings.TrimSpace(string(body)) != "" {
		apiErr.Message = strings.TrimSpace(string(body))
	}
	if apiErr.Message == "" {
		apiErr.Message = http.StatusText(status)
		if apiErr.Message == "" {
			apiErr.Message = "the TerraScale API request failed"
		}
	}
	if v := headers.Get("X-Request-ID"); v != "" && apiErr.RequestID == "" {
		apiErr.RequestID = v
	}
	return apiErr
}

func firstString(doc map[string]any, keys ...string) string {
	for _, k := range keys {
		if v, ok := doc[k].(string); ok && v != "" {
			return v
		}
	}
	return ""
}

func defaultErrorCode(status int) string {
	switch {
	case status == http.StatusUnauthorized:
		return "unauthenticated"
	case status == http.StatusForbidden:
		return "forbidden"
	case status == http.StatusNotFound:
		return "not_found"
	case status == http.StatusConflict:
		return "conflict"
	case status == http.StatusTooManyRequests:
		return "rate_limited"
	case status >= 500:
		return "server_error"
	default:
		return "request_failed"
	}
}

func placementOf(h http.Header) string {
	for _, key := range []string{"X-TerraScale-Placement", "X-TerraScale-Region"} {
		if v := strings.TrimSpace(h.Get(key)); v != "" {
			return v
		}
	}
	return ""
}

func (c *Client) backoff(headers http.Header, attempt int) time.Duration {
	return c.retryDelayFor(headers, attempt)
}

func (c *Client) retryDelayFor(headers http.Header, attempt int) time.Duration {
	if headers != nil {
		if d, ok := parseRetryAfter(headers.Get("Retry-After")); ok {
			return d
		}
	}
	d := c.cfg.RetryDelay << attempt
	if d <= 0 {
		return c.cfg.RetryDelay
	}
	if d > 30*time.Second {
		return 30 * time.Second
	}
	return d
}

func parseRetryAfter(value string) (time.Duration, bool) {
	v := strings.TrimSpace(value)
	if v == "" {
		return 0, false
	}
	if secs, err := strconv.Atoi(v); err == nil && secs >= 0 {
		d := time.Duration(secs) * time.Second
		if d > 30*time.Second {
			d = 30 * time.Second
		}
		return d, true
	}
	if t, err := http.ParseTime(v); err == nil {
		d := time.Until(t)
		if d < 0 {
			d = 0
		}
		if d > 30*time.Second {
			d = 30 * time.Second
		}
		return d, true
	}
	return 0, false
}

func sleepContext(ctx context.Context, d time.Duration) error {
	if d <= 0 {
		return ctx.Err()
	}
	timer := time.NewTimer(d)
	defer timer.Stop()
	select {
	case <-ctx.Done():
		return ctx.Err()
	case <-timer.C:
		return nil
	}
}
