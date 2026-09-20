// Package sdk is the TerraScale Go SDK (terrascale.tech/sdk).
//
// Document, schema, SQL, reactive, tenant, and management operations use the
// pinned TerraBase public API contract; see contract.go.
package sdk

import (
	"bytes"
	"context"
	"encoding/json"
	"fmt"
	"io"
	"math"
	"net"
	"net/http"
	"net/url"
	"strconv"
	"strings"
	"sync"
	"time"
	"unicode"
)

// Public protocol limits (GOAL D-32). These limits are applied before a
// request is sent and while a response is read, so a peer cannot make the
// SDK allocate an unbounded buffer.
const (
	MaxRequestBodyBytes  int64 = 16 << 20
	MaxResponseBodyBytes int64 = 16 << 20
	MaxSessionTokenBytes int   = 16 << 10
	MaxRequestTimeout          = 30 * time.Second
	MaxRetryAfter              = 30 * time.Second
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
	// AffinityFailover routes through discovery with per-scope regional
	// failover on the public origins (ADR 0050); on custom origins it sends
	// a preferred-region hint and follows one 421 Misdirected Request with
	// X-TerraScale-Placement to repair pinning.
	AffinityFailover RegionAffinity = "failover"
	// AffinityStrict pins REST to <region>.api.terrascale.tech and never
	// discovers.
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

// DefaultAPIBaseURL is the dashboard-owned product API origin (ADR 0050 §8):
// product routes only; member traffic routes through discovery to regional
// endpoints (GOAL D-24/D-58).
const DefaultAPIBaseURL = "https://" + publicAPIHostname

// Config configures a Client.
type Config struct {
	// APIBaseURL overrides the default product API origin (tests, custom
	// origins).
	APIBaseURL string
	// TSAuthBaseURL is the ts-auth deployment origin used by the TSAuth
	// family. When empty, TSAuth uses APIBaseURL and requires it to not be
	// the default product origin (DefaultAPIBaseURL), which never serves
	// ts-auth routes.
	TSAuthBaseURL string
	// DiscoveryBaseURL overrides the metadata origin — discovery and the
	// region catalog (ADR 0050) — for tests and development. It defaults to
	// https://atlas.terrascale.tech and is independent of APIBaseURL.
	// Discovery routing is active when it is set or the API base is the
	// public global origin, and never with strict affinity.
	DiscoveryBaseURL string
	// AccessToken is a static credential. TokenProvider takes precedence.
	AccessToken string
	// TokenProvider returns the credential dynamically, if set.
	TokenProvider func() string
	// TenantID and ProjectID select an authorized scope; they do not grant access.
	TenantID string
	// ProjectID is sent as X-TerraScale-Project-ID when non-empty.
	ProjectID string
	// PreferredRegion is the failover hint or strict pin target.
	PreferredRegion RegionCode
	// Affinity defaults to failover.
	Affinity RegionAffinity
	// SessionToken is the client's causal frontier (GOAL D-37).
	SessionToken string
	// HTTPClient defaults to one with a 30s timeout. The SDK copies it and
	// disables redirects; explicit 421 placement repair remains supported.
	HTTPClient *http.Client
	// MaxRetryAttempts is retries for idempotent mutations on transient
	// statuses (0-8, default 0).
	MaxRetryAttempts int
	// RetryDelay is the base retry backoff (default 250ms, max 30s).
	RetryDelay time.Duration
	// RequestTimeout bounds the complete request, including retries (default
	// 30s, maximum 30s). A shorter context deadline still wins.
	RequestTimeout time.Duration
}

// Client is the TerraScale SDK entry point.
type Client struct {
	cfg  Config
	http *http.Client
	// router resolves regional endpoints through discovery and the region
	// catalog (ADR 0050). It is nil when discovery is inactive: custom
	// origins and strict affinity keep direct configured routing.
	router *discoveryRouter

	mu     sync.RWMutex
	pinned RegionCode
	// sessionToken is the canonical frontier shared by REST and subscriptions.
	sessionToken string
	// regional caches the regionalized origin per region. It is computed once
	// at construction and never mutated, so base selection needs no lock and
	// no per-request URL parse.
	regional map[RegionCode]string

	AuthPersistence  *AuthPersistenceClient
	ProjectAPIKeys   *ProjectAPIKeysClient
	TSAuth           *TSAuthClient
	Operations       *OperationsClient
	Documents        *DocumentsClient
	Regions          *RegionsClient
	Databases        *DatabasesClient
	DatabaseBranches *DatabaseBranchesClient
	Backups          *BackupsClient
	Usage            *UsageClient
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
	if (u.Scheme != "http" && u.Scheme != "https") || u.Host == "" {
		return nil, fmt.Errorf("sdk: API base URL must be an absolute http or https URL")
	}
	if u.RawQuery != "" || u.Fragment != "" {
		return nil, fmt.Errorf("sdk: API base URL must not contain a query or fragment")
	}
	if u.User != nil {
		return nil, fmt.Errorf("sdk: API base URL must not contain userinfo")
	}
	if u.Scheme == "http" {
		host := strings.ToLower(u.Hostname())
		loopback := host == "localhost"
		if ip := net.ParseIP(host); ip != nil {
			loopback = ip.IsLoopback()
		}
		testHost := host == "test" || strings.HasSuffix(host, ".test")
		if !loopback && !(cfg.HTTPClient != nil && testHost) {
			return nil, fmt.Errorf("sdk: API endpoints must use HTTPS, except explicit loopback/test transports")
		}
	}
	lowerHost := strings.ToLower(u.Hostname())
	if strings.Contains(lowerHost, "tbep") || strings.Contains(lowerHost, "terrabase") {
		return nil, fmt.Errorf("sdk: clients may target only the public API, not TBEP or TerraBase")
	}
	lowerPath := strings.ToLower(u.EscapedPath())
	if strings.Contains(lowerPath, "tbep") || strings.Contains(lowerPath, "terrabase") {
		return nil, fmt.Errorf("sdk: clients may target only the public API, not TBEP or TerraBase")
	}

	discoveryBase := strings.TrimSpace(cfg.DiscoveryBaseURL)
	if discoveryBase != "" {
		discoveryBase = strings.TrimSuffix(discoveryBase, "/")
		discoveryURL, err := url.Parse(discoveryBase + "/")
		if err != nil || (discoveryURL.Scheme != "http" && discoveryURL.Scheme != "https") || discoveryURL.Host == "" {
			return nil, fmt.Errorf("sdk: discovery base URL must be an absolute http or https URL")
		}
		if discoveryURL.RawQuery != "" || discoveryURL.Fragment != "" || discoveryURL.User != nil {
			return nil, fmt.Errorf("sdk: discovery base URL must not contain a query, fragment, or userinfo")
		}
		if discoveryURL.Scheme == "http" && !isDevOrigin(discoveryURL.Hostname()) {
			return nil, fmt.Errorf("sdk: the discovery origin must use HTTPS, except loopback dev origins")
		}
		discoveryHost := strings.ToLower(discoveryURL.Hostname())
		if strings.Contains(discoveryHost, "tbep") || strings.Contains(discoveryHost, "terrabase") {
			return nil, fmt.Errorf("sdk: clients may target only the public API, not TBEP or TerraBase")
		}
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
	requestTimeout := cfg.RequestTimeout
	if requestTimeout == 0 {
		requestTimeout = MaxRequestTimeout
	}
	if requestTimeout < 0 || requestTimeout > MaxRequestTimeout {
		return nil, fmt.Errorf("sdk: request timeout must be between 0 and 30s")
	}
	if cfg.SessionToken != "" {
		if err := validateSessionToken(cfg.SessionToken); err != nil {
			return nil, err
		}
	}

	for name, selector := range map[string]string{"tenant ID": cfg.TenantID, "project ID": cfg.ProjectID} {
		for _, char := range selector {
			if char < 0x20 || char == 0x7f {
				return nil, fmt.Errorf("sdk: %s must contain no control characters", name)
			}
		}
	}

	httpClient := cfg.HTTPClient
	if httpClient == nil {
		httpClient = &http.Client{Timeout: 30 * time.Second}
	}
	// Placement repair uses explicit 421 responses. Ordinary redirects must
	// never forward credentials or replay writes outside that contract.
	ownedHTTPClient := *httpClient
	ownedHTTPClient.CheckRedirect = func(*http.Request, []*http.Request) error { return http.ErrUseLastResponse }
	httpClient = &ownedHTTPClient

	c := &Client{cfg: Config{
		APIBaseURL:       base,
		TSAuthBaseURL:    strings.TrimSpace(cfg.TSAuthBaseURL),
		DiscoveryBaseURL: discoveryBase,
		AccessToken:      cfg.AccessToken,
		TokenProvider:    cfg.TokenProvider,
		ProjectID:        cfg.ProjectID,
		TenantID:         cfg.TenantID,
		PreferredRegion:  preferred,
		Affinity:         affinity,
		HTTPClient:       httpClient,
		MaxRetryAttempts: cfg.MaxRetryAttempts,
		RetryDelay:       retryDelay,
		RequestTimeout:   requestTimeout,
	}, http: httpClient, sessionToken: strings.TrimSpace(cfg.SessionToken)}
	// Discovery routing (ADR 0050 §5.1) applies only to the public origins:
	// custom origins and strict affinity keep direct configured routing.
	if affinity != AffinityStrict {
		if discoveryBase == "" && strings.EqualFold(lowerHost, publicAPIHostname) {
			discoveryBase = DefaultDiscoveryBaseURL
		}
		if discoveryBase != "" {
			c.router = newDiscoveryRouter(discoveryBase, base, preferred, httpClient, cfg.ProjectID, cfg.TenantID)
		}
	}
	regional := make(map[RegionCode]string, 3)
	for _, region := range []RegionCode{RegionGRU, RegionSFO, RegionFRA} {
		regional[region] = regionalizeHost(base, region)
	}
	c.regional = regional
	c.AuthPersistence = &AuthPersistenceClient{c: c}
	c.ProjectAPIKeys = &ProjectAPIKeysClient{c: c}
	c.TSAuth = &TSAuthClient{c: c}
	c.Operations = &OperationsClient{c: c}
	c.Documents = &DocumentsClient{c: c}
	c.Regions = &RegionsClient{c: c}
	c.Databases = &DatabasesClient{c: c}
	c.DatabaseBranches = &DatabaseBranchesClient{c: c}
	c.Backups = &BackupsClient{c: c}
	c.Usage = &UsageClient{c: c}
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

// SessionToken returns the latest causal frontier observed by this client.
// The returned token is opaque and safe to pass to another client as its
// initial SessionToken configuration.
func (c *Client) SessionToken() string {
	c.mu.RLock()
	defer c.mu.RUnlock()
	return c.sessionToken
}

// SetSessionToken installs an opaque causal frontier for subsequent requests.
func (c *Client) SetSessionToken(token string) error {
	if token != "" {
		if err := validateSessionToken(token); err != nil {
			return err
		}
	}
	c.mu.Lock()
	c.sessionToken = strings.TrimSpace(token)
	c.mu.Unlock()
	return nil
}

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

func validateSessionToken(token string) error {
	token = strings.TrimSpace(token)
	if token == "" {
		return nil
	}
	if len(token) > MaxSessionTokenBytes || strings.ContainsAny(token, "\r\n") {
		return fmt.Errorf("sdk: session token must be at most %d bytes and contain no control characters", MaxSessionTokenBytes)
	}
	return nil
}

// observeSession records a server frontier. Session tokens are intentionally
// opaque: the server contract guarantees that a response token dominates the
// token carried by that request. Empty and malformed headers never clear an
// already observed frontier, which keeps concurrent requests monotonic from
// the client's perspective.
func (c *Client) observeSession(headers http.Header) {
	token := strings.TrimSpace(headers.Get("X-TerraScale-Session"))
	if token == "" || validateSessionToken(token) != nil {
		return
	}
	c.mu.Lock()
	if c.sessionToken == "" {
		c.sessionToken = token
	} else if c.sessionToken != token && sessionTokenAdvances(c.sessionToken, token) {
		// Tokens are opaque and cannot be safely compared or merged by an SDK.
		// A successful response is therefore the authoritative next frontier;
		// importantly, empty responses above cannot regress it.
		c.sessionToken = token
	}
	c.mu.Unlock()
}

// sessionTokenAdvances compares only TerraBase's current numeric token form.
// Other token forms are opaque by contract and are accepted as the server's
// next frontier without attempting lexical or structural ordering.
func sessionTokenAdvances(current, candidate string) bool {
	if currentSeq, currentOK := numericToken(current); currentOK {
		if candidateSeq, candidateOK := numericToken(candidate); candidateOK {
			return candidateSeq >= currentSeq
		}
	}
	return true
}

func numericToken(token string) (uint64, bool) {
	if !isDecimal(token) {
		return 0, false
	}
	sequence, err := strconv.ParseUint(token, 10, 64)
	return sequence, err == nil
}

// requestBase returns the origin for a client without discovery routing: a
// learned or explicit pin's regional origin, else the configured origin.
func (c *Client) requestBase() string {
	c.mu.RLock()
	pinned, cfg := c.pinned, c.cfg
	c.mu.RUnlock()
	switch cfg.Affinity {
	case AffinityStrict:
		region := pinned
		if region == "" {
			region = cfg.PreferredRegion
		}
		return c.regional[region]
	default:
		if pinned != "" {
			return c.regional[pinned]
		}
		return cfg.APIBaseURL
	}
}

// routeClass classifies one request path for discovery routing.
type routeClass int

const (
	// routeMember covers member-served routes: database-scoped requests and
	// database-less routes such as database create/list and reactive attach.
	routeMember routeClass = iota
	// routeCatalog is /api/v1/regions exactly, served by the atlas origin.
	routeCatalog
	// routeProduct covers dashboard-owned product routes on the product origin.
	routeProduct
)

// classifyRoute applies the fixed route table (ADR 0050 §1/§8). It is called
// only when discovery routing is active.
func classifyRoute(path string) routeClass {
	switch {
	case isRegionCatalogRoute(path):
		return routeCatalog
	case isProductRoute(path):
		return routeProduct
	default:
		return routeMember
	}
}

// resolveAttemptBase returns the origin for one attempt and the region that
// origin was routed to (empty when the origin was not discovery-selected).
// Member routes with no resolvable regional endpoint return a typed
// RoutingError: there is no proxy to fall back to (ADR 0050 §5.8).
func (c *Client) resolveAttemptBase(ctx context.Context, method, path, route string, class routeClass, database string, needsWrite bool) (string, string, error) {
	if c.router == nil {
		return c.requestBase(), "", nil
	}
	switch class {
	case routeCatalog:
		return c.router.origin, "", nil
	case routeProduct:
		return c.cfg.APIBaseURL, "", nil
	default:
		if database != "" {
			if base, ok := c.router.resolveBase(ctx, database, needsWrite); ok {
				return base.baseURL, base.region, nil
			}
		}
		base, err := c.router.resolveCatalogBase(ctx)
		if err != nil {
			return "", "", &RoutingError{Route: route, Method: method, Path: path, Err: err}
		}
		return base, "", nil
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
	ifMatch            string
	documentIfMatch    string
	hasDocumentIfMatch bool
	readAt             int64
	hasReadAt          bool
	responseMediaType  string
	requestMediaType   string
	responseHeaders    *http.Header
	createOnly         bool
	authTransaction    string
	noRetry            bool
	// routingDatabase routes a database-less member route (reactive attach)
	// through the database's discovered regional endpoint (ADR 0050 §5.7).
	routingDatabase string
	// retryableRead marks a named read operation whose POST body is safe to
	// repeat (reactive attach); it selects a read-capable endpoint.
	retryableRead bool
	// capturedBase receives the origin each attempt used.
	capturedBase *string
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

// WithReadAt reads one document as of a point in time. at is epoch
// milliseconds and travels as the at query parameter of the point read.
// Negative values, and use outside document point reads, are rejected before
// any request is sent.
func WithReadAt(millis int64) RequestOption {
	return func(r *requestConfig) { r.readAt = millis; r.hasReadAt = true }
}

// WithCreateOnly requires a document PUT to create a new identity atomically.
// It is rejected for PATCH, DELETE and other operations.
func WithCreateOnly() RequestOption { return func(r *requestConfig) { r.createOnly = true } }

// WithIfMatch requires a document PUT, PATCH, or DELETE to match the strong
// ETag returned by Documents.Get. Use "*" to require an existing document.
// Keep the same condition and idempotency key when retrying an uncertain write.
func WithIfMatch(etag string) RequestOption {
	return func(r *requestConfig) { r.documentIfMatch = etag; r.hasDocumentIfMatch = true }
}

// WithSchemaGeneration applies a schema only if its strong generation matches.
// Pass the Generation returned by GetCollectionSchema.
func WithSchemaGeneration(generation uint64) RequestOption {
	return func(r *requestConfig) { r.ifMatch = "\"" + strconv.FormatUint(generation, 10) + "\"" }
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
	Fix       string
	Placement string
	RequestID string
	Body      string
	// RetryAfter exposes the server-sent Retry-After header (delta-seconds
	// or HTTP-date) when present, uncapped; nil when absent or unparseable.
	RetryAfter *time.Duration
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
	520:                            true,
	521:                            true,
	522:                            true,
	523:                            true,
	524:                            true,
}

// do performs one API call: path must stay under /api/v1.
func (c *Client) do(ctx context.Context, method, path string, query url.Values, body any, out any, route string, opts ...RequestOption) error {
	if ctx == nil {
		return fmt.Errorf("sdk: request context cannot be nil")
	}
	if path != "/api/v1" && !strings.HasPrefix(path, "/api/v1/") {
		return fmt.Errorf("sdk: path %q is outside the public /api/v1 contract", path)
	}
	rc := resolveRequestOptions(opts)
	if rc.hasDocumentIfMatch {
		if rc.ifMatch != "" || (route != "documents.put" && route != "documents.patch" && route != "documents.delete") {
			return &ValidationError{Field: "if-match", Message: "supported only for document PUT, PATCH, or DELETE without a schema generation"}
		}
		etag := rc.documentIfMatch
		valid := etag == "*"
		if len(etag) >= 2 && etag[0] == '"' && etag[len(etag)-1] == '"' {
			valid = true
			for i := 1; i < len(etag)-1; i++ {
				if etag[i] < 0x21 || etag[i] == '"' || etag[i] == 0x7f {
					valid = false
				}
			}
		}
		if !valid || len(etag) > MaxSessionTokenBytes {
			return &ValidationError{Field: "if-match", Message: "must be one strong quoted ETag or *"}
		}
		rc.ifMatch = etag
	}
	if rc.createOnly && (method != http.MethodPut || route != "documents.put" || rc.ifMatch != "") {
		return &ValidationError{Field: "create-only", Message: "supported only for document PUT without If-Match"}
	}
	if rc.hasReadAt {
		if rc.readAt < 0 {
			return &ValidationError{Field: "at", Message: "must be non-negative epoch milliseconds"}
		}
		if route != "documents.get" {
			return &ValidationError{Field: "at", Message: "supported only for document point reads"}
		}
		if query == nil {
			query = url.Values{}
		}
		query.Set("at", strconv.FormatInt(rc.readAt, 10))
	}
	if rc.regionHint != "" {
		region, err := ParseRegionCode(string(rc.regionHint))
		if err != nil {
			return fmt.Errorf("sdk: invalid region hint: %w", err)
		}
		rc.regionHint = region
	}
	if rc.sessionToken != "" {
		if err := validateSessionToken(rc.sessionToken); err != nil {
			return err
		}
		rc.sessionToken = strings.TrimSpace(rc.sessionToken)
	}
	var idem string
	if isMutation(method) && !rc.noIdempotency && rc.idempotencyKey != "" {
		key := strings.TrimSpace(rc.idempotencyKey)
		if key == "" || len(key) > 128 || strings.ContainsAny(key, "\r\n") {
			return fmt.Errorf("sdk: idempotency keys must be nonblank, at most 128 bytes, and contain no control characters")
		}
		idem = key
	}
	var payload []byte
	if body != nil {
		var err error
		switch raw := body.(type) {
		case importPayload:
			payload = bytes.Clone(raw)
			if payload == nil {
				payload = []byte{}
			}
			if rc.requestMediaType == "" {
				rc.requestMediaType = "application/octet-stream"
			}
		case json.RawMessage:
			// Pre-encoded JSON from the SDK's own validators. Passing it
			// through avoids a second marshal of every write; an empty
			// message still falls back so invalid JSON is rejected here.
			if len(raw) > 0 {
				payload = raw
			} else {
				payload, err = json.Marshal(raw)
				if err != nil {
					return fmt.Errorf("sdk: marshal %s request body: %w", route, err)
				}
			}
		default:
			payload, err = json.Marshal(body)
			if err != nil {
				return fmt.Errorf("sdk: marshal %s request body: %w", route, err)
			}
		}
		if int64(len(payload)) > MaxRequestBodyBytes {
			return fmt.Errorf("sdk: %s request body exceeds %d bytes", route, MaxRequestBodyBytes)
		}
	}

	requestCtx := ctx
	cancel := func() {}
	if timeout := c.cfg.RequestTimeout; timeout > 0 {
		requestCtx, cancel = context.WithTimeout(ctx, timeout)
	}
	defer cancel()
	// ADR 0050 §1/§8 route classification: database-scoped requests route
	// through discovery to a direct regional endpoint, product routes target
	// the product origin, and every other member route selects a regional
	// endpoint from the catalog. There is no proxy fallback anymore.
	routingDatabase := rc.routingDatabase
	if routingDatabase == "" {
		routingDatabase, _ = parseRoutingDatabase(path)
	}
	class := routeMember
	if c.router != nil {
		class = classifyRoute(path)
	}
	needsWrite := isMutation(method) && !(method == http.MethodPost && rc.retryableRead)
	attempt := 0
	followed421 := false
	recoveredPin := false
	for {
		base, routedRegion, routingErr := c.resolveAttemptBase(requestCtx, method, path, route, class, routingDatabase, needsWrite)
		if routingErr != nil {
			return routingErr
		}
		if rc.capturedBase != nil {
			*rc.capturedBase = base
		}
		attemptStart := time.Now()
		status, headers, respBody, transportErr := c.roundTrip(requestCtx, method, base, path, query, payload, rc, idem)
		if routedRegion != "" && c.router != nil && transportErr == nil && status < 500 {
			// Measured latency outranks geography (ADR 0050 §2): every
			// successful round trip to a discovered regional endpoint feeds
			// the per-region estimate the next discovery request reports.
			c.router.observeRtt(routedRegion, time.Since(attemptStart))
		}
		if rc.responseHeaders != nil {
			*rc.responseHeaders = headers.Clone()
		}
		if transportErr != nil {
			if routingDatabase != "" && c.router != nil && routedRegion != "" {
				// A regional endpoint implicated by a transport failure moves
				// behind the eligible alternatives (ADR 0050 §5.3).
				c.router.reportRegionFailure(routingDatabase, routedRegion)
			}
			if !recoveredPin && requestCtx.Err() == nil && routedRegion != "" && canRecoverRead(method) {
				// The routed endpoint failed to answer; the next attempt
				// selects the next endpoint in the document.
				recoveredPin = true
				continue
			}
			if !rc.noRetry && idem != "" && attempt < c.cfg.MaxRetryAttempts {
				if sleepErr := sleepContext(requestCtx, c.backoff(nil, attempt)); sleepErr != nil {
					return sleepErr
				}
				attempt++
				continue
			}
			return fmt.Errorf("sdk: %s %s (%s): %w", method, path, route, transportErr)
		}
		if status == http.StatusMisdirectedRequest && !followed421 && c.cfg.Affinity != AffinityStrict {
			if routingDatabase != "" && c.router != nil {
				// The member is authoritative (ADR 0043 §2): adopt its
				// placement immediately and schedule a corrective
				// rediscovery (ADR 0050 §5.4).
				c.router.notePlacement(routingDatabase, placementRegions(headers))
			}
			if code, ok := placementRegionExcluding(headers, c.PinnedRegion()); ok {
				c.setPinned(code)
				if canRepairPlacement(method, idem) {
					// The placement in a 421 is authoritative for this retry,
					// including when the original request supplied a hint.
					rc.regionHint = code
					followed421 = true
					continue
				}
				// A 421 may arrive after an unkeyed mutation reached the
				// server. Learn the advertised placement, but never replay
				// an operation whose effect cannot be deduplicated.
			}
		}
		if status >= 200 && status < 300 {
			// A successful response advances the causal frontier even when the
			// caller requested no response body (for example DELETE/204).
			c.observeSession(headers)
			if decoder, ok := out.(interface{ decodeHTTPResponse(int, []byte) error }); ok {
				return decoder.decodeHTTPResponse(status, respBody)
			}
			return decodeSuccess(out, respBody, headers.Get("Content-Type"))
		}
		// Health is a liveness response, so an unhealthy member still returns
		// the typed HealthResponse body with HTTP 503. Preserve that body for
		// callers; the `healthy` field carries the verdict.
		if route == "health" && status == http.StatusServiceUnavailable {
			return decodeSuccess(out, respBody, headers.Get("Content-Type"))
		}
		if routingDatabase != "" && c.router != nil && routedRegion != "" && retryableStatuses[status] {
			// A regional endpoint implicated by a retryable failure moves
			// behind the eligible alternatives (ADR 0050 §5.3).
			c.router.reportRegionFailure(routingDatabase, routedRegion)
		}
		apiErr := errorFromResponse(route, method, path, status, headers, respBody)
		if !recoveredPin && requestCtx.Err() == nil && routedRegion != "" && canRecoverRead(method) && originFailureStatus(status) {
			if err := sleepContext(requestCtx, c.retryDelayFor(headers, 0)); err != nil {
				return err
			}
			recoveredPin = true
			continue
		}
		if !rc.noRetry && idem != "" && attempt < c.cfg.MaxRetryAttempts && retryableStatuses[status] {
			if sleepErr := sleepContext(requestCtx, c.retryDelayFor(headers, attempt)); sleepErr != nil {
				return sleepErr
			}
			attempt++
			continue
		}
		return apiErr
	}
}

func canRecoverRead(method string) bool {
	return method == http.MethodGet || method == http.MethodHead
}

func canRepairPlacement(method, idempotencyKey string) bool {
	return method == http.MethodGet || (isMutation(method) && idempotencyKey != "")
}

// originFailureStatus reports the origin-failure statuses a bounded read
// failover may repair by moving to the next endpoint: the standard gateway
// failures plus Cloudflare's 52x origin connectivity class (520 unknown,
// 521 origin down, 522 connection timed out, 523 origin unreachable, 524
// origin timeout), which are returned before the request reaches the member.
func originFailureStatus(status int) bool {
	switch status {
	case http.StatusBadGateway, http.StatusServiceUnavailable, http.StatusGatewayTimeout,
		520, 521, 522, 523, 524:
		return true
	default:
		return false
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
	if rc.responseMediaType != "" {
		h.Set("Accept", rc.responseMediaType)
	}
	if payload != nil {
		contentType := rc.requestMediaType
		if contentType == "" {
			contentType = "application/json"
		}
		h.Set("Content-Type", contentType)
	}
	c.decorateScopeHeaders(h)
	c.mu.RLock()
	pinned, session := c.pinned, rc.sessionToken
	if session == "" {
		session = c.sessionToken
	}
	c.mu.RUnlock()
	hint := rc.regionHint
	if hint == "" && (c.cfg.Affinity == AffinityFailover || c.cfg.Affinity == AffinityStrict) {
		// A learned 421 pin wins over the configured preferred region. Strict
		// affinity also needs this header when APIBaseURL is a custom origin,
		// because regionalizeHost cannot rewrite a non-canonical hostname.
		if pinned != "" {
			hint = pinned
		} else if c.cfg.PreferredRegion != "" {
			hint = c.cfg.PreferredRegion
		}
	}
	if hint != "" {
		h.Set("X-TerraScale-Preferred-Region", string(hint))
	}
	if token := c.token(); token != "" {
		if err := setBearerAuthorization(h, token); err != nil {
			return 0, nil, nil, err
		}
	}
	if session != "" {
		h.Set("X-TerraScale-Session", session)
	}
	if rc.consistency != "" && rc.consistency != ConsistencyDefault {
		h.Set("X-TerraScale-Consistency", string(rc.consistency))
	}
	if rc.deleteConfirmation != "" {
		h.Set("X-TerraScale-Delete-Confirmation", rc.deleteConfirmation)
	}
	if rc.authTransaction != "" {
		h.Set("X-TerraScale-Auth-Transaction", rc.authTransaction)
	}
	if rc.createOnly {
		h.Set("If-None-Match", "*")
	}
	if rc.ifMatch != "" {
		h.Set("If-Match", rc.ifMatch)
	}
	if idem != "" {
		h.Set("Idempotency-Key", idem)
	}

	resp, err := c.http.Do(req)
	if err != nil {
		return 0, nil, nil, err
	}
	defer resp.Body.Close()
	if resp.ContentLength > MaxResponseBodyBytes {
		return 0, nil, nil, fmt.Errorf("sdk: response body exceeds %d bytes", MaxResponseBodyBytes)
	}
	respBody, err := io.ReadAll(io.LimitReader(resp.Body, MaxResponseBodyBytes+1))
	if err != nil {
		return 0, nil, nil, err
	}
	if int64(len(respBody)) > MaxResponseBodyBytes {
		return 0, nil, nil, fmt.Errorf("sdk: response body exceeds %d bytes", MaxResponseBodyBytes)
	}
	return resp.StatusCode, resp.Header, respBody, nil
}

func bearerAuthorization(value string) (string, error) {
	// Validate before trimming so a control character cannot be hidden by
	// normalization (for example, "\n" must not become an empty token).
	if strings.ContainsFunc(value, unicode.IsControl) {
		return "", fmt.Errorf("sdk: access token contains invalid control characters")
	}
	value = strings.TrimSpace(value)
	if value == "" {
		return "", nil
	}
	if strings.ContainsAny(value, "\r\n") {
		return "", fmt.Errorf("sdk: access token contains invalid control characters")
	}
	if strings.EqualFold(value, "Bearer") {
		return "", fmt.Errorf("sdk: bearer token cannot be empty")
	}
	if len(value) >= len("Bearer ") && strings.EqualFold(value[:len("Bearer ")], "Bearer ") {
		credential := strings.TrimSpace(value[len("Bearer "):])
		if credential == "" {
			return "", fmt.Errorf("sdk: bearer token cannot be empty")
		}
		if strings.ContainsFunc(credential, unicode.IsSpace) {
			return "", fmt.Errorf("sdk: bearer token contains whitespace")
		}
		return "Bearer " + credential, nil
	}
	if strings.ContainsFunc(value, unicode.IsSpace) {
		return "", fmt.Errorf("sdk: bearer token contains whitespace")
	}
	return "Bearer " + value, nil
}

func setBearerAuthorization(header http.Header, token string) error {
	authorization, err := bearerAuthorization(token)
	if err != nil {
		return err
	}
	if authorization != "" {
		header.Set("Authorization", authorization)
	}
	return nil
}

type binaryResponse []byte

func decodeSuccess(out any, body []byte, contentType string) error {
	if raw, ok := out.(*binaryResponse); ok {
		*raw = bytes.Clone(body)
		return nil
	}
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
	var doc errorDocument
	if err := json.Unmarshal(body, &doc); err == nil {
		sourceCode, sourceMessage, sourceFix := doc.Code, doc.Message, doc.Fix
		var nested errorDetail
		nestedOK := len(doc.Error) > 0 && json.Unmarshal(doc.Error, &nested) == nil && (nested.Code != "" || nested.Message != "" || nested.Fix != "")
		if nestedOK {
			sourceCode, sourceMessage, sourceFix = nested.Code, nested.Message, nested.Fix
		}
		if sourceCode != "" {
			apiErr.Code = sourceCode
		} else if !nestedOK {
			if v := firstString(doc.Code, doc.ErrorCode, doc.Error_Code); v != "" {
				apiErr.Code = v
			}
		}
		if sourceMessage != "" {
			apiErr.Message = sourceMessage
		} else if !nestedOK {
			var errorText string
			_ = json.Unmarshal(doc.Error, &errorText)
			apiErr.Message = firstString(doc.Message, errorText, doc.Detail, doc.Title)
		}
		apiErr.Fix = sourceFix
		apiErr.RequestID = firstString(doc.RequestID, doc.Request_ID, doc.TraceID)
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
	if placement, ok := placementRegion(headers); ok {
		apiErr.Placement = string(placement)
	}
	apiErr.RetryAfter = retryAfterFromHeader(headers)
	return apiErr
}

// errorDocument deliberately models only bounded diagnostic fields. The
// response itself has already been capped by roundTrip before this decoder is
// called, and unknown fields are ignored for forward compatibility.
type errorDocument struct {
	Code       string          `json:"code"`
	ErrorCode  string          `json:"errorCode"`
	Error_Code string          `json:"error_code"`
	Message    string          `json:"message"`
	Detail     string          `json:"detail"`
	Title      string          `json:"title"`
	RequestID  string          `json:"requestId"`
	Request_ID string          `json:"request_id"`
	TraceID    string          `json:"traceId"`
	Fix        string          `json:"fix"`
	Error      json.RawMessage `json:"error"`
}

type errorDetail struct {
	Code    string `json:"code"`
	Message string `json:"message"`
	Fix     string `json:"fix"`
}

func firstString(values ...string) string {
	for _, value := range values {
		if value != "" {
			return value
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
	case status == http.StatusMisdirectedRequest:
		return "misdirected_region"
	case status == http.StatusTooManyRequests:
		return "rate_limited"
	case status >= 500:
		return "server_error"
	default:
		return "request_failed"
	}
}

// placementRegions returns every known region in the authoritative placement
// header, order preserved (home first, ADR 0043 §1). The member's placement
// teaches the routing cache without a discovery round trip (ADR 0050 §5.4).
func placementRegions(h http.Header) []RegionCode {
	var regions []RegionCode
	for _, key := range []string{"X-TerraScale-Placement", "X-TerraScale-Region"} {
		for _, value := range headerValuesFold(h, key) {
			for _, candidate := range strings.Split(value, ",") {
				region, err := ParseRegionCode(candidate)
				if err != nil {
					continue
				}
				known := false
				for _, seen := range regions {
					if seen == region {
						known = true
						break
					}
				}
				if !known {
					regions = append(regions, region)
				}
			}
		}
	}
	return regions
}

func headerValuesFold(h http.Header, name string) []string {
	if h == nil {
		return nil
	}
	// Wire and Header.Set keys are canonical, so the common case is a direct
	// map hit. The fold scan remains only for directly constructed maps that
	// bypass canonicalization; a canonical key is authoritative when both
	// spellings are present.
	if values, ok := h[http.CanonicalHeaderKey(name)]; ok {
		return values
	}
	var values []string
	for key, entries := range h {
		if strings.EqualFold(key, name) {
			values = append(values, entries...)
		}
	}
	return values
}

// placementRegion accepts only a single known region from the authoritative
// placement header. It never reflects arbitrary header text into a hostname
// or a follow-up request.
func placementRegion(h http.Header) (RegionCode, bool) {
	return placementRegionExcluding(h, "")
}

func placementRegionExcluding(h http.Header, excluded RegionCode) (RegionCode, bool) {
	for _, key := range []string{"X-TerraScale-Placement", "X-TerraScale-Region"} {
		for _, value := range headerValuesFold(h, key) {
			value = strings.TrimSpace(value)
			if value == "" {
				continue
			}
			for _, candidate := range strings.Split(value, ",") {
				region, err := ParseRegionCode(candidate)
				if err == nil && region != excluded {
					return region, true
				}
			}
		}
	}
	return "", false
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
	if d > MaxRetryAfter {
		return MaxRetryAfter
	}
	return d
}

// retryAfterFromHeader exposes the Retry-After the server sent (delta-seconds
// or HTTP-date) on APIError, uncapped, unlike the capped retry scheduling in
// parseRetryAfter. Nil when absent or unparseable.
func retryAfterFromHeader(headers http.Header) *time.Duration {
	value := strings.TrimSpace(headers.Get("Retry-After"))
	if value == "" {
		return nil
	}
	if secs, err := strconv.ParseUint(value, 10, 64); err == nil || isDecimal(value) {
		// An out-of-range delta is still a delta: clamp to the largest
		// representable duration instead of wrapping.
		if err != nil || secs > uint64(math.MaxInt64)/uint64(time.Second) {
			secs = uint64(math.MaxInt64 / int64(time.Second))
		}
		d := time.Duration(secs) * time.Second
		return &d
	}
	if t, err := http.ParseTime(value); err == nil {
		d := time.Until(t)
		if d < 0 {
			d = 0
		}
		return &d
	}
	return nil
}

func parseRetryAfter(value string) (time.Duration, bool) {
	v := strings.TrimSpace(value)
	if v == "" {
		return 0, false
	}
	if secs, err := strconv.ParseUint(v, 10, 64); err == nil {
		if secs >= uint64(MaxRetryAfter/time.Second) {
			return MaxRetryAfter, true
		}
		return time.Duration(secs) * time.Second, true
	} else if isDecimal(v) {
		// ParseUint reports ErrRange for a hostile but otherwise valid
		// delta-seconds value. It is still a valid value to cap safely.
		return MaxRetryAfter, true
	}
	if t, err := http.ParseTime(v); err == nil {
		d := time.Until(t)
		if d < 0 {
			d = 0
		}
		if d > MaxRetryAfter {
			d = MaxRetryAfter
		}
		return d, true
	}
	return 0, false
}

func isDecimal(value string) bool {
	if value == "" {
		return false
	}
	for _, r := range value {
		if r < '0' || r > '9' {
			return false
		}
	}
	return true
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

func (c *Client) decorateScopeHeaders(headers http.Header) {
	if c.cfg.TenantID != "" {
		headers.Set("X-TerraScale-Tenant-ID", c.cfg.TenantID)
	}
	if c.cfg.ProjectID != "" {
		headers.Set("X-TerraScale-Project-ID", c.cfg.ProjectID)
	}
}
