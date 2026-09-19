// SDK-driven routing discovery (TerraBase ADR 0050). Discovery is the first
// contact for database-scoped requests against the public origins: one
// metadata GET per routing scope from the atlas origin, then direct regional
// connections. The per-request proxy is retired (ADR 0050 §8): a discovery
// failure degrades to region-catalog selection, never to a proxy.
package sdk

import (
	"bytes"
	"context"
	"encoding/json"
	"fmt"
	"io"
	"math"
	"net/http"
	"net/url"
	"slices"
	"strconv"
	"strings"
	"sync"
	"time"
)

// DefaultDiscoveryBaseURL is the metadata origin (ts-atlas): discovery and
// the region catalog, and nothing else. It is independent of the product
// APIBaseURL.
const DefaultDiscoveryBaseURL = "https://atlas.terrascale.tech"

// publicAPIHostname is the product API origin family; discovered endpoints
// must stay inside https://{region}.<publicAPIHostname>.
const publicAPIHostname = "api.terrascale.tech"

const (
	discoveryPath       = "/api/v1/discovery"
	edgeRegionsPath     = "/api/v1/regions"
	discoveryTimeout    = 3 * time.Second
	discoveryMaxBody    = 1 << 20
	minRefreshSeconds   = 60
	maxRefreshSeconds   = 3600
	maxRoutingEndpoints = 20
	// discoveryFailureBackoff spaces repeated discovery attempts for one
	// scope after a failure so a dead metadata origin cannot be hammered.
	discoveryFailureBackoff = time.Second
	// regionCatalogCacheTTL is the catalog cache lifetime (ADR 0050 §5.8).
	regionCatalogCacheTTL = 5 * time.Minute
	// defaultRoutingBranch is the only branch dimension this SDK addresses.
	defaultRoutingBranch = "main"
	// rttHalflife is the EWMA halflife of the per-region latency estimate
	// (ADR 0050 §2): one slow outlier cannot permanently demote a region.
	rttHalflife = 60 * time.Second
	// maxRegionRttMs bounds one measured round trip and the estimates sent
	// to the discovery origin (milliseconds).
	maxRegionRttMs = 60000
	// maxRegionRttEntries caps the measured-latency header (ADR 0050 §2).
	maxRegionRttEntries = 20
	// regionRttHeader carries measured client-to-region round trips to the
	// discovery origin only; it never travels to member endpoints.
	regionRttHeader = "X-TerraScale-Region-Rtt"
)

// RoutingCapability names what a discovered endpoint currently accepts.
type RoutingCapability string

const (
	CapabilityRead     RoutingCapability = "read"
	CapabilityWrite    RoutingCapability = "write"
	CapabilitySQL      RoutingCapability = "sql"
	CapabilityReactive RoutingCapability = "reactive"
)

var routingCapabilities = map[RoutingCapability]bool{
	CapabilityRead:     true,
	CapabilityWrite:    true,
	CapabilitySQL:      true,
	CapabilityReactive: true,
}

// RoutingEndpoint is one ranked regional endpoint of a routing document.
type RoutingEndpoint struct {
	Region       string
	URL          string
	Priority     int
	Capabilities []RoutingCapability
}

func (e RoutingEndpoint) supports(capability RoutingCapability) bool {
	for _, candidate := range e.Capabilities {
		if candidate == capability {
			return true
		}
	}
	return false
}

// RoutingDocument is a validated discovery response (ADR 0050 §2).
type RoutingDocument struct {
	Database       string
	Branch         string
	Revision       uint64
	Placement      string // "known" or "unknown"
	RefreshSeconds int    // clamped to 60-3600
	Endpoints      []RoutingEndpoint
}

// RoutingError reports that no regional endpoint could be selected for a
// member route: discovery failed and the region catalog could not answer
// either (ADR 0050 §5.8). There is no proxy fallback; retry once the
// metadata origin recovers.
type RoutingError struct {
	Route  string
	Method string
	Path   string
	Err    error
}

func (e *RoutingError) Error() string {
	detail := "discovery and the region catalog are unavailable"
	if e.Err != nil {
		detail = e.Err.Error()
	}
	return fmt.Sprintf("sdk: %s %s (%s): no regional endpoint: %s", e.Method, e.Path, e.Route, detail)
}

func (e *RoutingError) Unwrap() error { return e.Err }

// routingScope keys one cached routing document (ADR 0050 §5.2): routing
// learned for one database, branch, project, or tenant never serves another.
type routingScope struct {
	Origin   string
	Database string
	Branch   string
	Project  string
	Tenant   string
}

func scopeKey(scope routingScope) string {
	return scope.Origin + "|" + scope.Database + "|" + scope.Branch + "|p=" + scope.Project + "|t=" + scope.Tenant
}

// isDevOrigin reports a loopback or *.localhost dev origin: a loopback
// metadata origin may advertise http dev endpoints for `terrascale dev`.
func isDevOrigin(host string) bool {
	return host == "localhost" || strings.HasSuffix(host, ".localhost") ||
		host == "127.0.0.1" || host == "::1" || host == "[::1]"
}

func originIsLoopback(origin string) bool {
	u, err := url.Parse(origin)
	if err != nil {
		return false
	}
	return isDevOrigin(u.Hostname())
}

// validateEndpointURL validates a discovered endpoint URL at the trust
// boundary: https on the regional API family only, or an http dev origin when
// the metadata origin itself is loopback. The returned URL is canonical, so a
// hostile document cannot steer traffic outside the family.
func validateEndpointURL(raw, region string, allowDevHTTP bool) (string, bool) {
	if raw == "" || len(raw) > 2048 {
		return "", false
	}
	u, err := url.Parse(raw)
	if err != nil {
		return "", false
	}
	if u.User != nil || u.Fragment != "" || u.RawQuery != "" {
		return "", false
	}
	expected := strings.ToLower(region) + "." + publicAPIHostname
	if u.Scheme == "https" && strings.EqualFold(u.Hostname(), expected) {
		return "https://" + expected, true
	}
	if allowDevHTTP && u.Scheme == "http" && isDevOrigin(u.Hostname()) {
		return "http://" + u.Host, true
	}
	return "", false
}

// parseRoutingDocument strictly validates a discovery response. Any
// structural doubt rejects the document; routing never widens on malformed
// input. The database and branch must echo the request identity.
func parseRoutingDocument(body []byte, database, branch string, allowDevHTTP bool) (RoutingDocument, error) {
	type endpointWire struct {
		Region       *string  `json:"region"`
		URL          *string  `json:"url"`
		Priority     *int     `json:"priority"`
		Capabilities []string `json:"capabilities"`
	}
	var wire struct {
		V              *int            `json:"v"`
		Database       *string         `json:"database"`
		Branch         *string         `json:"branch"`
		Revision       *uint64         `json:"revision"`
		Updated        *uint64         `json:"updated"`
		Residency      json.RawMessage `json:"residency"`
		Placement      *string         `json:"placement"`
		RefreshSeconds *int            `json:"refreshSeconds"`
		Endpoints      []endpointWire  `json:"endpoints"`
	}
	decoder := json.NewDecoder(bytes.NewReader(body))
	decoder.DisallowUnknownFields()
	if err := decoder.Decode(&wire); err != nil {
		return RoutingDocument{}, fmt.Errorf("sdk: discovery document: %w", err)
	}
	var extra any
	if err := decoder.Decode(&extra); err != io.EOF {
		if err == nil {
			return RoutingDocument{}, fmt.Errorf("sdk: discovery document contains trailing data")
		}
		return RoutingDocument{}, fmt.Errorf("sdk: discovery document: %w", err)
	}
	if wire.V == nil || *wire.V != 1 {
		return RoutingDocument{}, fmt.Errorf("sdk: discovery document version must be 1")
	}
	if wire.Database == nil || *wire.Database != database || wire.Branch == nil || *wire.Branch != branch {
		return RoutingDocument{}, fmt.Errorf("sdk: discovery document identity does not echo the request")
	}
	if wire.Revision == nil {
		return RoutingDocument{}, fmt.Errorf("sdk: discovery document revision is required")
	}
	if err := validateRoutingResidency(wire.Residency); err != nil {
		return RoutingDocument{}, err
	}
	placement := "unknown"
	if wire.Placement != nil {
		switch *wire.Placement {
		case "known", "unknown":
			placement = *wire.Placement
		default:
			return RoutingDocument{}, fmt.Errorf("sdk: discovery document placement must be known or unknown")
		}
	}
	if wire.RefreshSeconds == nil || *wire.RefreshSeconds < 1 || *wire.RefreshSeconds > 86400 {
		return RoutingDocument{}, fmt.Errorf("sdk: discovery document refreshSeconds must be between 1 and 86400")
	}
	if len(wire.Endpoints) == 0 || len(wire.Endpoints) > maxRoutingEndpoints {
		return RoutingDocument{}, fmt.Errorf("sdk: discovery document must list 1 to %d endpoints", maxRoutingEndpoints)
	}
	refresh := *wire.RefreshSeconds
	if refresh < minRefreshSeconds {
		refresh = minRefreshSeconds
	}
	if refresh > maxRefreshSeconds {
		refresh = maxRefreshSeconds
	}
	doc := RoutingDocument{
		Database:       database,
		Branch:         branch,
		Revision:       *wire.Revision,
		Placement:      placement,
		RefreshSeconds: refresh,
		Endpoints:      make([]RoutingEndpoint, 0, len(wire.Endpoints)),
	}
	seenRegions := make(map[string]struct{}, len(wire.Endpoints))
	hasFirstPriority := false
	for i, entry := range wire.Endpoints {
		if entry.Region == nil || entry.URL == nil || entry.Priority == nil {
			return RoutingDocument{}, fmt.Errorf("sdk: discovery endpoint %d is missing a required field", i)
		}
		code, err := parseCatalogRegionCode(*entry.Region)
		if err != nil {
			return RoutingDocument{}, fmt.Errorf("sdk: discovery endpoint %d: %w", i, err)
		}
		region := string(code)
		if _, dup := seenRegions[region]; dup {
			return RoutingDocument{}, fmt.Errorf("sdk: discovery document lists region %q twice", region)
		}
		seenRegions[region] = struct{}{}
		if *entry.Priority < 1 || *entry.Priority > maxRoutingEndpoints {
			return RoutingDocument{}, fmt.Errorf("sdk: discovery endpoint %d priority must be between 1 and %d", i, maxRoutingEndpoints)
		}
		endpointURL, ok := validateEndpointURL(*entry.URL, region, allowDevHTTP)
		if !ok {
			return RoutingDocument{}, fmt.Errorf("sdk: discovery endpoint %d has an untrusted URL", i)
		}
		if len(entry.Capabilities) == 0 {
			return RoutingDocument{}, fmt.Errorf("sdk: discovery endpoint %d has no capabilities", i)
		}
		capabilities := make([]RoutingCapability, 0, len(entry.Capabilities))
		for _, capability := range entry.Capabilities {
			parsed := RoutingCapability(capability)
			if !routingCapabilities[parsed] {
				return RoutingDocument{}, fmt.Errorf("sdk: discovery endpoint %d has unknown capability %q", i, capability)
			}
			if !slices.Contains(capabilities, parsed) {
				capabilities = append(capabilities, parsed)
			}
		}
		if *entry.Priority == 1 {
			hasFirstPriority = true
		}
		doc.Endpoints = append(doc.Endpoints, RoutingEndpoint{
			Region:       region,
			URL:          endpointURL,
			Priority:     *entry.Priority,
			Capabilities: capabilities,
		})
	}
	if !hasFirstPriority {
		return RoutingDocument{}, fmt.Errorf("sdk: discovery document has no first-choice endpoint")
	}
	return doc, nil
}

// validateRoutingResidency checks the residency annotation's bounded shape
// (label, explicit region list, or null). It is a hard filter the server
// already applied to the endpoint list; the SDK does not re-enforce it.
func validateRoutingResidency(raw json.RawMessage) error {
	if len(raw) == 0 || bytes.Equal(raw, []byte("null")) {
		return nil
	}
	var label string
	if err := json.Unmarshal(raw, &label); err == nil {
		if len(label) >= 1 && len(label) <= 256 {
			return nil
		}
		return fmt.Errorf("sdk: discovery residency label is out of range")
	}
	var list []string
	if err := json.Unmarshal(raw, &list); err != nil {
		return fmt.Errorf("sdk: discovery residency must be a label, region list, or null")
	}
	if len(list) == 0 || len(list) > 20 {
		return fmt.Errorf("sdk: discovery residency region list must contain 1 to 20 regions")
	}
	seen := make(map[string]struct{}, len(list))
	for _, item := range list {
		region, err := parseCatalogRegionCode(item)
		if err != nil {
			return fmt.Errorf("sdk: discovery residency region list has an invalid code")
		}
		if _, dup := seen[string(region)]; dup {
			return fmt.Errorf("sdk: discovery residency region list repeats %q", region)
		}
		seen[string(region)] = struct{}{}
	}
	return nil
}

// parseRoutingDatabase mirrors the discovery fixed path table exactly
// (ADR 0050 §2): /api/v1/databases/{name}/... The database name is the
// routing scope; database-less routes never discover.
func parseRoutingDatabase(path string) (string, bool) {
	segments := pathSegments(path)
	if len(segments) < 4 || segments[0] != "api" || segments[1] != "v1" || segments[2] != "databases" {
		return "", false
	}
	database := segments[3]
	if validateDatabaseName(database) != nil {
		return "", false
	}
	return database, true
}

// isRegionCatalogRoute reports the catalog form: /api/v1/regions exactly,
// served by the discovery origin, never the product origin.
func isRegionCatalogRoute(path string) bool {
	segments := pathSegments(path)
	return len(segments) == 3 && segments[0] == "api" && segments[1] == "v1" && segments[2] == "regions"
}

// isProductRoute reports dashboard-owned product routes (ADR 0050 §8):
// everything under /api/v1/organizations and /api/v1/projects, plus
// /api/v1/regions/{code}. They target the product origin.
func isProductRoute(path string) bool {
	segments := pathSegments(path)
	if len(segments) < 3 || segments[0] != "api" || segments[1] != "v1" {
		return false
	}
	if segments[2] == "organizations" || segments[2] == "projects" {
		return true
	}
	return segments[2] == "regions" && len(segments) >= 4
}

func pathSegments(path string) []string {
	trimmed := strings.Trim(path, "/")
	if trimmed == "" {
		return nil
	}
	segments := strings.Split(trimmed, "/")
	out := segments[:0]
	for _, segment := range segments {
		if segment != "" {
			out = append(out, segment)
		}
	}
	return out
}

type routingEntry struct {
	doc       RoutingDocument
	expiresAt time.Time
}

type catalogEntry struct {
	catalog   RegionCatalogResponse
	expiresAt time.Time
}

type discoveryCall struct {
	done chan struct{}
	doc  RoutingDocument
	err  error
}

type catalogCall struct {
	done    chan struct{}
	catalog RegionCatalogResponse
	err     error
}

// resolvedBase is one selected regional endpoint.
type resolvedBase struct {
	baseURL string
	region  string
}

// rttSample is one region's exponentially averaged round-trip estimate.
type rttSample struct {
	valueMs float64
	at      time.Time
}

// regionRttTracker keeps one EWMA latency estimate per region (ADR 0050 §2:
// measured latency outranks geography). Round trips outside 0..60000 ms are
// ignored, and a sample whose timestamp does not advance past the region's
// previous sample is ignored too, so concurrently folded observations cannot
// skew the average backwards. One tracker belongs to one client's router.
type regionRttTracker struct {
	mu      sync.Mutex
	samples map[string]rttSample
}

func newRegionRttTracker() *regionRttTracker {
	return &regionRttTracker{samples: make(map[string]rttSample)}
}

// observe folds one measured round trip into the region's estimate, decaying
// the previous estimate by the elapsed time since its sample (time of sample).
func (t *regionRttTracker) observe(region string, rtt time.Duration, now time.Time) {
	if !IsRegionCode(region) {
		return
	}
	ms := float64(rtt) / float64(time.Millisecond)
	if math.IsNaN(ms) || ms < 0 || ms > maxRegionRttMs {
		return
	}
	t.mu.Lock()
	defer t.mu.Unlock()
	previous, ok := t.samples[region]
	if !ok {
		t.samples[region] = rttSample{valueMs: ms, at: now}
		return
	}
	if !now.After(previous.at) {
		return
	}
	decay := math.Exp2(-now.Sub(previous.at).Seconds() / rttHalflife.Seconds())
	t.samples[region] = rttSample{valueMs: previous.valueMs*decay + ms*(1-decay), at: now}
}

// measuredRttHeader renders the X-TerraScale-Region-Rtt value: at most 20
// "REGION=ms" pairs of whole milliseconds in ascending region order, each
// within 1..60000. Regions without a usable estimate are skipped, so the
// discovery worker parses only well-formed pairs.
func (t *regionRttTracker) measuredRttHeader() string {
	t.mu.Lock()
	defer t.mu.Unlock()
	regions := make([]string, 0, len(t.samples))
	for region := range t.samples {
		regions = append(regions, region)
	}
	slices.Sort(regions)
	parts := make([]string, 0, len(regions))
	for _, region := range regions {
		if len(parts) == maxRegionRttEntries {
			break
		}
		ms := t.samples[region].valueMs
		if ms < 1 || ms > maxRegionRttMs {
			continue
		}
		parts = append(parts, region+"="+strconv.Itoa(int(math.Round(ms))))
	}
	return strings.Join(parts, ",")
}

// discoveryRouter owns discovery and catalog selection for one Client. A
// healthy selection is sticky: refreshes update the fallback order for future
// use, and only a routing failure, a 421, or exit from the eligible set moves
// the client (ADR 0050 §5.5).
type discoveryRouter struct {
	origin    string
	apiBase   string
	preferred RegionCode
	http      *http.Client
	projectID string
	tenantID  string
	// allowDevHTTP admits http dev-origin endpoints when origin is loopback.
	allowDevHTTP bool
	now          func() time.Time

	mu          sync.Mutex
	docs        map[string]*routingEntry
	demoted     map[string]map[string]struct{}
	stale       map[string]struct{}
	failures    map[string]time.Time
	pinned      map[string]string
	generations map[string]uint64
	inflight    map[string]*discoveryCall

	// rtt holds this client's measured regional round trips; it feeds the
	// discovery ranking and nothing else.
	rtt *regionRttTracker

	catalog     *catalogEntry
	catalogWait *catalogCall
}

func newDiscoveryRouter(origin, apiBase string, preferred RegionCode, httpClient *http.Client, projectID, tenantID string) *discoveryRouter {
	return &discoveryRouter{
		origin:       origin,
		apiBase:      apiBase,
		preferred:    preferred,
		http:         httpClient,
		projectID:    projectID,
		tenantID:     tenantID,
		allowDevHTTP: originIsLoopback(origin),
		now:          time.Now,
		docs:         make(map[string]*routingEntry),
		demoted:      make(map[string]map[string]struct{}),
		stale:        make(map[string]struct{}),
		failures:     make(map[string]time.Time),
		pinned:       make(map[string]string),
		generations:  make(map[string]uint64),
		inflight:     make(map[string]*discoveryCall),
		rtt:          newRegionRttTracker(),
	}
}

func (r *discoveryRouter) scopeFor(database string) routingScope {
	return routingScope{
		Origin:   r.origin,
		Database: database,
		Branch:   defaultRoutingBranch,
		Project:  r.projectID,
		Tenant:   r.tenantID,
	}
}

// resolveBase returns the regional endpoint for one database-scoped
// operation, or false when discovery cannot answer: the caller degrades to
// catalog selection for that request (ADR 0050 §5.8). Concurrent discovery
// for one scope shares a single request.
func (r *discoveryRouter) resolveBase(ctx context.Context, database string, needsWrite bool) (resolvedBase, bool) {
	scope := r.scopeFor(database)
	key := scopeKey(scope)
	if doc, ok := r.cachedDocument(key); ok {
		return r.selectEndpoint(key, doc, needsWrite)
	}
	if r.failureBackoffRemaining(key) > 0 {
		return resolvedBase{}, false
	}
	stale := r.consumeStale(key)
	generation := r.generation(key)
	call := r.startDiscovery(scope, stale)
	select {
	case <-call.done:
	case <-ctx.Done():
		return resolvedBase{}, false
	}
	if call.err != nil {
		r.noteFailure(key)
		return resolvedBase{}, false
	}
	r.applyDocument(key, generation, call.doc)
	doc, ok := r.cachedDocument(key)
	if !ok {
		return resolvedBase{}, false
	}
	return r.selectEndpoint(key, doc, needsWrite)
}

// startDiscovery joins or starts the scope's single-flight discovery fetch.
// The fetch is bounded by its own timeout and detached from any one caller's
// cancellation, so a joining caller's cancellation never fails the shared
// request.
func (r *discoveryRouter) startDiscovery(scope routingScope, stale bool) *discoveryCall {
	key := scopeKey(scope)
	r.mu.Lock()
	if call, ok := r.inflight[key]; ok {
		r.mu.Unlock()
		return call
	}
	call := &discoveryCall{done: make(chan struct{})}
	r.inflight[key] = call
	r.mu.Unlock()
	go func() {
		defer close(call.done)
		defer func() {
			r.mu.Lock()
			delete(r.inflight, key)
			r.mu.Unlock()
		}()
		ctx, cancel := context.WithTimeout(context.WithoutCancel(context.Background()), discoveryTimeout)
		defer cancel()
		call.doc, call.err = r.fetchDocument(ctx, scope, stale)
	}()
	return call
}

// fetchDocument performs one unauthenticated discovery GET (ADR 0050 §2). A
// cached revision travels as rev for observability and server-side stale
// detection, and measured regional round trips travel as the region-rtt
// header so the worker can rank measured latency above geography. Neither
// ever reaches a member endpoint: this is the only request that carries them.
func (r *discoveryRouter) fetchDocument(ctx context.Context, scope routingScope, stale bool) (RoutingDocument, error) {
	query := url.Values{}
	query.Set("database", scope.Database)
	query.Set("branch", scope.Branch)
	if revision, ok := r.cachedRevision(scopeKey(scope)); ok && revision > 0 {
		// Revision 0 marks unknown placement (ADR 0050 §2): there is no
		// meaningful cached revision to report.
		query.Set("rev", strconv.FormatUint(revision, 10))
	}
	if stale {
		query.Set("stale", "1")
	}
	req, err := http.NewRequestWithContext(ctx, http.MethodGet, r.origin+discoveryPath+"?"+query.Encode(), nil)
	if err != nil {
		return RoutingDocument{}, err
	}
	req.Header.Set("Accept", "application/json")
	if measured := r.rtt.measuredRttHeader(); measured != "" {
		req.Header.Set(regionRttHeader, measured)
	}
	resp, err := r.http.Do(req)
	if err != nil {
		return RoutingDocument{}, err
	}
	defer resp.Body.Close()
	if resp.StatusCode != http.StatusOK {
		return RoutingDocument{}, fmt.Errorf("discovery answered %d", resp.StatusCode)
	}
	body, err := io.ReadAll(io.LimitReader(resp.Body, discoveryMaxBody+1))
	if err != nil {
		return RoutingDocument{}, err
	}
	if len(body) > discoveryMaxBody {
		return RoutingDocument{}, fmt.Errorf("discovery document exceeds %d bytes", discoveryMaxBody)
	}
	return parseRoutingDocument(body, scope.Database, scope.Branch, r.allowDevHTTP)
}

// applyDocument stores a fetched document only while the scope's routing
// state did not change mid-flight: a fresher 421-learned placement or a
// demotion wins over an older fetch.
func (r *discoveryRouter) applyDocument(key string, generation uint64, doc RoutingDocument) {
	r.mu.Lock()
	defer r.mu.Unlock()
	if r.generations[key] != generation {
		return
	}
	r.docs[key] = &routingEntry{doc: doc, expiresAt: r.now().Add(time.Duration(doc.RefreshSeconds) * time.Second)}
	delete(r.demoted, key)
	r.generations[key]++
}

func (r *discoveryRouter) cachedDocument(key string) (RoutingDocument, bool) {
	r.mu.Lock()
	defer r.mu.Unlock()
	entry, ok := r.docs[key]
	if !ok || !entry.expiresAt.After(r.now()) {
		// An expired entry is not served, but it is kept until replaced or
		// invalidated: its revision is still the client's cached revision and
		// travels as rev on the refresh (ADR 0050 §2).
		return RoutingDocument{}, false
	}
	doc := entry.doc
	if demoted, ok := r.demoted[key]; ok && len(demoted) > 0 {
		filtered := make([]RoutingEndpoint, 0, len(doc.Endpoints))
		for _, endpoint := range doc.Endpoints {
			if _, isDemoted := demoted[endpoint.Region]; !isDemoted {
				filtered = append(filtered, endpoint)
			}
		}
		// Demotion never removes every alternative: the document's ranking
		// remains the only failover set (ADR 0050 §2).
		if len(filtered) > 0 {
			doc.Endpoints = filtered
		}
	}
	return doc, true
}

// selectEndpoint picks the endpoint for one operation: the scope's sticky
// region while it stays in the document and capability-eligible, else the
// configured preference, else the first eligible endpoint. Only a selection
// that actually moves rewrites the sticky region.
func (r *discoveryRouter) selectEndpoint(key string, doc RoutingDocument, needsWrite bool) (resolvedBase, bool) {
	required := CapabilityRead
	if needsWrite {
		required = CapabilityWrite
	}
	var eligible []RoutingEndpoint
	for _, endpoint := range doc.Endpoints {
		if endpoint.supports(required) {
			eligible = append(eligible, endpoint)
		}
	}
	if len(eligible) == 0 {
		return resolvedBase{}, false
	}
	r.mu.Lock()
	defer r.mu.Unlock()
	choose := func(endpoint RoutingEndpoint) resolvedBase {
		r.pinned[key] = endpoint.Region
		return resolvedBase{baseURL: endpoint.URL, region: endpoint.Region}
	}
	if pinned, ok := r.pinned[key]; ok && pinned != "" {
		for _, endpoint := range eligible {
			if endpoint.Region == pinned {
				return resolvedBase{baseURL: endpoint.URL, region: endpoint.Region}, true
			}
		}
	}
	if r.preferred != "" {
		for _, endpoint := range eligible {
			if endpoint.Region == string(r.preferred) {
				return choose(endpoint), true
			}
		}
	}
	return choose(eligible[0]), true
}

// resolveCatalogBase returns the catalog's first available regional host
// (ADR 0050 §5.8): the discovery failure fallback and the base for
// database-less member routes. The catalog is cached five minutes and
// fetched single-flight; a failed fetch caches nothing.
func (r *discoveryRouter) resolveCatalogBase(ctx context.Context) (string, error) {
	r.mu.Lock()
	if r.catalog != nil && r.catalog.expiresAt.After(r.now()) {
		host, err := firstAvailableCatalogHost(r.catalog.catalog)
		r.mu.Unlock()
		return host, err
	}
	wait := r.catalogWait
	started := false
	if wait == nil {
		wait = &catalogCall{done: make(chan struct{})}
		r.catalogWait = wait
		started = true
	}
	r.mu.Unlock()
	if started {
		go func() {
			defer close(wait.done)
			defer func() {
				r.mu.Lock()
				r.catalogWait = nil
				r.mu.Unlock()
			}()
			ctx, cancel := context.WithTimeout(context.WithoutCancel(context.Background()), discoveryTimeout)
			defer cancel()
			wait.catalog, wait.err = r.fetchCatalog(ctx)
			if wait.err == nil {
				r.mu.Lock()
				r.catalog = &catalogEntry{catalog: wait.catalog, expiresAt: r.now().Add(regionCatalogCacheTTL)}
				r.mu.Unlock()
			}
		}()
	}
	select {
	case <-wait.done:
	case <-ctx.Done():
		return "", ctx.Err()
	}
	if wait.err != nil {
		return "", wait.err
	}
	return firstAvailableCatalogHost(wait.catalog)
}

func (r *discoveryRouter) fetchCatalog(ctx context.Context) (RegionCatalogResponse, error) {
	req, err := http.NewRequestWithContext(ctx, http.MethodGet, r.origin+edgeRegionsPath, nil)
	if err != nil {
		return RegionCatalogResponse{}, err
	}
	req.Header.Set("Accept", "application/json")
	resp, err := r.http.Do(req)
	if err != nil {
		return RegionCatalogResponse{}, err
	}
	defer resp.Body.Close()
	if resp.StatusCode != http.StatusOK {
		return RegionCatalogResponse{}, fmt.Errorf("the region catalog answered %d", resp.StatusCode)
	}
	body, err := io.ReadAll(io.LimitReader(resp.Body, discoveryMaxBody+1))
	if err != nil {
		return RegionCatalogResponse{}, err
	}
	if len(body) > discoveryMaxBody {
		return RegionCatalogResponse{}, fmt.Errorf("the region catalog exceeds %d bytes", discoveryMaxBody)
	}
	var catalog RegionCatalogResponse
	if err := json.Unmarshal(body, &catalog); err != nil {
		return RegionCatalogResponse{}, err
	}
	// Hosts obey the same endpoint trust rules as routing documents.
	for _, region := range catalog.Regions {
		host, ok := validateEndpointURL(catalog.Hosts[string(region.Code)], string(region.Code), r.allowDevHTTP)
		if !ok {
			return RegionCatalogResponse{}, fmt.Errorf("the region catalog has an untrusted host for %s", region.Code)
		}
		catalog.Hosts[string(region.Code)] = host
	}
	return catalog, nil
}

func firstAvailableCatalogHost(catalog RegionCatalogResponse) (string, error) {
	for _, region := range catalog.Regions {
		if !region.Available {
			continue
		}
		if host := catalog.Hosts[string(region.Code)]; host != "" {
			return host, nil
		}
	}
	return "", fmt.Errorf("the region catalog lists no available region")
}

// cachedRevision reports the revision of the scope's most recent document,
// expired entries included: a refresh sends it as rev (ADR 0050 §2).
func (r *discoveryRouter) cachedRevision(key string) (uint64, bool) {
	r.mu.Lock()
	defer r.mu.Unlock()
	entry, ok := r.docs[key]
	if !ok {
		return 0, false
	}
	return entry.doc.Revision, true
}

// observeRtt folds one successful round trip to a discovered regional
// endpoint into the client's per-region latency estimates (ADR 0050 §2).
// Requests the router did not route to a region (catalog selection, product,
// and custom origins) have nothing to attribute and are never observed.
func (r *discoveryRouter) observeRtt(region string, rtt time.Duration) {
	r.rtt.observe(region, rtt, r.now())
}

// notePlacement adopts a member's authoritative placement headers (a 421 or
// a rejected route) without a discovery round trip: placement regions are
// fully capable until discovery says otherwise, the remembered region is
// dropped, and the next refresh is corrective (stale=1). Synthesis rewrites
// only the public global origin into its regional form; every other origin
// declines and invalidates instead of caching production URLs.
func (r *discoveryRouter) notePlacement(database string, regions []RegionCode) {
	scope := r.scopeFor(database)
	key := scopeKey(scope)
	r.mu.Lock()
	defer r.mu.Unlock()
	r.pinned[key] = ""
	r.stale[key] = struct{}{}
	u, err := url.Parse(r.apiBase)
	if err != nil || !strings.EqualFold(u.Hostname(), publicAPIHostname) {
		r.invalidateLocked(key)
		return
	}
	unique := make([]RegionCode, 0, len(regions))
	for _, region := range regions {
		if !IsRegionCode(string(region)) {
			continue
		}
		dup := false
		for _, seen := range unique {
			if seen == region {
				dup = true
				break
			}
		}
		if !dup {
			unique = append(unique, region)
		}
	}
	if len(unique) == 0 {
		r.invalidateLocked(key)
		return
	}
	if len(unique) > maxRoutingEndpoints {
		unique = unique[:maxRoutingEndpoints]
	}
	doc := RoutingDocument{
		Database:       scope.Database,
		Branch:         scope.Branch,
		Placement:      "known",
		RefreshSeconds: minRefreshSeconds,
		Endpoints:      make([]RoutingEndpoint, 0, len(unique)),
	}
	for i, region := range unique {
		doc.Endpoints = append(doc.Endpoints, RoutingEndpoint{
			Region:       string(region),
			URL:          regionalizeHost(r.apiBase, region),
			Priority:     i + 1,
			Capabilities: []RoutingCapability{CapabilityRead, CapabilityWrite, CapabilitySQL, CapabilityReactive},
		})
	}
	r.docs[key] = &routingEntry{doc: doc, expiresAt: r.now().Add(minRefreshSeconds * time.Second)}
	delete(r.demoted, key)
	r.generations[key]++
}

func (r *discoveryRouter) invalidateLocked(key string) {
	delete(r.docs, key)
	delete(r.demoted, key)
	r.generations[key]++
}

// reportRegionFailure records a routing failure against one region: it moves
// behind the alternatives for that scope (ADR 0050 §5.3).
func (r *discoveryRouter) reportRegionFailure(database, region string) {
	if region == "" {
		return
	}
	key := scopeKey(r.scopeFor(database))
	r.mu.Lock()
	defer r.mu.Unlock()
	demoted, ok := r.demoted[key]
	if !ok {
		demoted = make(map[string]struct{})
		r.demoted[key] = demoted
	}
	demoted[region] = struct{}{}
	r.generations[key]++
}

func (r *discoveryRouter) consumeStale(key string) bool {
	r.mu.Lock()
	defer r.mu.Unlock()
	if _, ok := r.stale[key]; !ok {
		return false
	}
	delete(r.stale, key)
	return true
}

func (r *discoveryRouter) noteFailure(key string) {
	r.mu.Lock()
	defer r.mu.Unlock()
	r.failures[key] = r.now()
}

func (r *discoveryRouter) failureBackoffRemaining(key string) time.Duration {
	r.mu.Lock()
	defer r.mu.Unlock()
	failedAt, ok := r.failures[key]
	if !ok {
		return 0
	}
	if remaining := failedAt.Add(discoveryFailureBackoff).Sub(r.now()); remaining > 0 {
		return remaining
	}
	return 0
}

func (r *discoveryRouter) generation(key string) uint64 {
	r.mu.Lock()
	defer r.mu.Unlock()
	return r.generations[key]
}
