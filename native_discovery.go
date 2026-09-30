package sdk

import (
	"bytes"
	"context"
	"crypto/sha256"
	"encoding/base64"
	"encoding/json"
	"errors"
	"fmt"
	"io"
	"maps"
	"mime"
	"net/http"
	"net/url"
	"regexp"
	"strconv"
	"strings"
	"sync"
	"time"
)

const (
	nativeProxyOrigin         = "https://api.terrascale.tech"
	nativeDefaultRefresh      = 8 * time.Second
	nativeDiscoveryURL        = "https://atlas.terrascale.tech/api/v1/discovery"
	nativeDiscoveryBodyLimit  = 64 << 10
	nativeDiscoveryCacheLimit = 256
)

// NativeRoutingError describes a local native routing failure. It is wrapped
// by RoutingError; Code never invents an Atlas HTTP status or response.
type NativeRoutingError struct {
	Code string
	Err  error
}

func (e *NativeRoutingError) Error() string {
	if e.Err == nil {
		return "sdk: native routing: " + e.Code
	}
	return fmt.Sprintf("sdk: native routing %s: %v", e.Code, e.Err)
}
func (e *NativeRoutingError) Unwrap() error { return e.Err }

func nativeRoutingFailure(code string, err error) error {
	return &NativeRoutingError{Code: code, Err: err}
}

type nativeRequirements uint8

const (
	nativeRead nativeRequirements = 1 << iota
	nativeQuery
	nativeSubscriptions
	nativeWrite
	nativeConditional
	nativeAtomic
)

type nativeDiscoveryCapabilities struct {
	ClientV1      bool `json:"client_v1"`
	Read          bool `json:"read"`
	Query         bool `json:"query"`
	Subscriptions bool `json:"subscriptions"`
	Write         bool `json:"write"`
	Conditional   bool `json:"conditional_mutation"`
	Atomic        bool `json:"atomic_groups"`
	Security      bool `json:"security_state"`
}

func (c nativeDiscoveryCapabilities) supports(required nativeRequirements) bool {
	return c.ClientV1 &&
		(required&nativeRead == 0 || c.Read) &&
		(required&nativeQuery == 0 || c.Query) &&
		(required&nativeSubscriptions == 0 || c.Subscriptions) &&
		(required&nativeWrite == 0 || c.Write) &&
		(required&nativeConditional == 0 || c.Conditional) &&
		(required&nativeAtomic == 0 || c.Atomic)
}

type nativeDiscoveryScope struct {
	Tenant   UUID       `json:"tenant_id"`
	Database UUID       `json:"database_id"`
	Shard    DecimalU64 `json:"shard_id"`
}

func (s nativeDiscoveryScope) documentScope() DocumentScope {
	return DocumentScope{Tenant: s.Tenant, Database: s.Database, Shard: s.Shard}
}

type nativeDiscoveryAuthority struct {
	Deployment string     `json:"deployment_id"`
	Cell       string     `json:"cell"`
	Publisher  string     `json:"publisher_id"`
	Generation DecimalU64 `json:"publisher_generation"`
}
type nativeDiscoveryEndpoint struct {
	Region  string `json:"region"`
	URL     string `json:"url"`
	Catalog struct {
		Digest Hex32 `json:"digest"`
	} `json:"catalog"`
	Capabilities   nativeDiscoveryCapabilities `json:"capabilities"`
	SecurityWriter json.RawMessage             `json:"security_writer"`
	Priority       uint8                       `json:"priority"`
}
type nativeDiscoveryDocument struct {
	Schema         uint8                     `json:"schema_version"`
	Scope          nativeDiscoveryScope      `json:"scope"`
	Authority      nativeDiscoveryAuthority  `json:"authority"`
	Revision       DecimalU64                `json:"revision"`
	PublishedAt    int64                     `json:"published_at"`
	ExpiresAt      int64                     `json:"expires_at"`
	RefreshSeconds uint8                     `json:"refreshSeconds"`
	Residency      *[]string                 `json:"residency"`
	Endpoints      []nativeDiscoveryEndpoint `json:"endpoints"`
}

var (
	nativeDiscoveryIdentity = regexp.MustCompile(`^[A-Za-z0-9][A-Za-z0-9._:-]{0,127}$`)
	nativeDiscoveryToken    = regexp.MustCompile(`^[a-z0-9][a-z0-9-]{0,62}$`)
	nativeDiscoveryRegion   = regexp.MustCompile(`^[A-Z0-9][A-Z0-9-]{0,15}$`)
)

func validNativeDiscoveryRegion(region string) bool {
	if !nativeDiscoveryRegion.MatchString(region) {
		return false
	}
	switch region {
	case "API", "ATLAS", "AUTH", "SYNC", "STATUS", "WWW", "APP", "DASH", "DOCS", "GIT", "CDN", "ADMIN", "MCP", "MAIL":
		return false
	}
	return true
}
func (d nativeDiscoveryDocument) validate(scope DocumentScope) error {
	if d.Schema != 1 || d.Scope.Shard == 0 || d.Scope.documentScope() != scope || d.Authority.Generation == 0 || d.Revision == 0 {
		return fmt.Errorf("invalid discovery schema, scope or revision")
	}
	if !nativeDiscoveryIdentity.MatchString(d.Authority.Deployment) || !nativeDiscoveryToken.MatchString(d.Authority.Cell) || !nativeDiscoveryToken.MatchString(d.Authority.Publisher) {
		return fmt.Errorf("invalid discovery authority")
	}
	if d.PublishedAt < 1 || d.ExpiresAt > 253402300799 || d.ExpiresAt <= d.PublishedAt || d.ExpiresAt-d.PublishedAt > 10 || d.RefreshSeconds < 1 || d.RefreshSeconds > 10 {
		return fmt.Errorf("invalid discovery validity interval")
	}
	residency := make(map[string]bool)
	if d.Residency != nil {
		if len(*d.Residency) == 0 || len(*d.Residency) > 20 {
			return fmt.Errorf("invalid discovery residency")
		}
		for _, region := range *d.Residency {
			if !validNativeDiscoveryRegion(region) || residency[region] {
				return fmt.Errorf("invalid discovery residency region")
			}
			residency[region] = true
		}
	}
	if len(d.Endpoints) < 1 || len(d.Endpoints) > 20 {
		return fmt.Errorf("invalid discovery endpoint count")
	}
	seen := make(map[string]bool, len(d.Endpoints))
	for i, endpoint := range d.Endpoints {
		if !validNativeDiscoveryRegion(endpoint.Region) || seen[endpoint.Region] || endpoint.URL != "https://"+strings.ToLower(endpoint.Region)+".api.terrascale.tech" || int(endpoint.Priority) != i+1 {
			return fmt.Errorf("invalid discovery endpoint or priority")
		}
		seen[endpoint.Region] = true
		if d.Residency != nil && !residency[endpoint.Region] {
			return fmt.Errorf("discovery endpoint violates residency")
		}
		caps := endpoint.Capabilities
		if !caps.ClientV1 || (!caps.Read && !caps.Write) {
			return fmt.Errorf("discovery endpoint is ineligible")
		}
		if caps.Conditional || caps.Atomic || caps.Security || !bytes.Equal(bytes.TrimSpace(endpoint.SecurityWriter), []byte("null")) {
			return fmt.Errorf("unsupported discovery advanced capabilities or writer")
		}
	}
	return nil
}

type nativeDiscoveryFloor struct {
	RefreshSeconds uint8
	Authority      nativeDiscoveryAuthority
	Revision       DecimalU64
}

func (f nativeDiscoveryFloor) accepts(d nativeDiscoveryDocument) bool {
	if f.Revision == 0 {
		return true
	}
	return f.Authority.Deployment == d.Authority.Deployment && f.Authority.Cell == d.Authority.Cell &&
		d.Authority.Generation >= f.Authority.Generation && d.Revision >= f.Revision &&
		!(d.Authority.Generation == f.Authority.Generation && d.Revision == f.Revision && d.Authority.Publisher != f.Authority.Publisher)
}

type nativeDiscoveryKey struct {
	Scope      DocumentScope
	Credential [32]byte
}
type nativeDiscoveryLease struct {
	Document    nativeDiscoveryDocument
	Deadline    time.Time
	Used        uint64
	Proxy       bool
	Failed      map[string]bool
	Invalidated bool
}

func (l nativeDiscoveryLease) valid(now time.Time) bool {
	return now.Before(l.Deadline) && (l.Proxy || now.Unix() >= l.Document.PublishedAt && now.Unix() < l.Document.ExpiresAt)
}

type nativeDiscoveryAdmission struct {
	URL   string
	Key   nativeDiscoveryKey
	Lease nativeDiscoveryLease
}

func (r *nativeDiscoveryRouter) assertCurrent(admission nativeDiscoveryAdmission) error {
	r.mu.Lock()
	defer r.mu.Unlock()
	now := r.now()
	if !admission.Lease.valid(now) {
		return nativeRoutingFailure("discovery_expired", nil)
	}
	if !admission.Lease.Proxy && !r.floors[admission.Key.Scope].accepts(admission.Lease.Document) {
		return nativeRoutingFailure("routing_observation_regressed", nil)
	}
	return nil
}

type nativeDiscoveryCall struct {
	Done           chan struct{}
	Err            error
	CallerCanceled bool
}
type nativeDiscoveryRouter struct {
	mu      sync.Mutex
	http    *http.Client
	now     func() time.Time
	floors  map[DocumentScope]nativeDiscoveryFloor
	leases  map[nativeDiscoveryKey]nativeDiscoveryLease
	calls   map[nativeDiscoveryKey]*nativeDiscoveryCall
	serial  uint64
	timeout time.Duration
}

func newNativeDiscoveryRouter(client *http.Client) *nativeDiscoveryRouter {
	return &nativeDiscoveryRouter{http: client, now: time.Now, timeout: 3 * time.Second,
		floors: make(map[DocumentScope]nativeDiscoveryFloor), leases: make(map[nativeDiscoveryKey]nativeDiscoveryLease), calls: make(map[nativeDiscoveryKey]*nativeDiscoveryCall)}
}

func nativeCredentialFingerprint(credential string) ([32]byte, error) {
	if len(credential) != 71 {
		return [32]byte{}, nativeRoutingFailure("invalid_credential", fmt.Errorf("canonical native tbk1 credential is required"))
	}
	parts := strings.Split(credential, ".")
	if len(parts) != 3 || parts[0] != "tbk1" {
		return [32]byte{}, nativeRoutingFailure("invalid_credential", fmt.Errorf("canonical native tbk1 credential is required"))
	}
	for i, size := range []int{16, 32} {
		decoded, err := base64.RawURLEncoding.Strict().DecodeString(parts[i+1])
		if err != nil || len(decoded) != size || base64.RawURLEncoding.EncodeToString(decoded) != parts[i+1] {
			return [32]byte{}, nativeRoutingFailure("invalid_credential", fmt.Errorf("canonical native tbk1 credential is required"))
		}
	}
	return sha256.Sum256([]byte(credential)), nil
}

func (r *nativeDiscoveryRouter) resolve(ctx context.Context, scope DocumentScope, credential string, required nativeRequirements) (nativeDiscoveryAdmission, error) {
	fingerprint, err := nativeCredentialFingerprint(credential)
	if err != nil {
		return nativeDiscoveryAdmission{}, err
	}
	key := nativeDiscoveryKey{Scope: scope, Credential: fingerprint}
	for {
		if err := ctx.Err(); err != nil {
			return nativeDiscoveryAdmission{}, nativeRoutingFailure("discovery_unavailable", err)
		}
		r.mu.Lock()
		floor := r.floors[scope]
		previous := r.leases[key]
		if lease, ok := r.leases[key]; ok && !lease.Proxy && !lease.Invalidated && lease.valid(r.now()) && floor.accepts(lease.Document) {
			r.serial++
			lease.Used = r.serial
			r.leases[key] = lease
			r.mu.Unlock()
			for _, endpoint := range lease.Document.Endpoints {
				if !lease.Failed[endpoint.URL] && endpoint.Capabilities.supports(required) {
					return nativeDiscoveryAdmission{URL: endpoint.URL, Key: key, Lease: lease}, nil
				}
			}
			for _, endpoint := range lease.Document.Endpoints {
				if endpoint.Capabilities.supports(required) {
					return nativeDiscoveryAdmission{}, nativeRoutingFailure("regions_unavailable", nil)
				}
			}
			return nativeDiscoveryAdmission{}, nativeRoutingFailure("capability_unavailable", nil)
		}
		delete(r.leases, key)
		if call, ok := r.calls[key]; ok {
			r.mu.Unlock()
			select {
			case <-ctx.Done():
				return nativeDiscoveryAdmission{}, nativeRoutingFailure("discovery_unavailable", ctx.Err())
			case <-call.Done:
				if err := ctx.Err(); err != nil {
					return nativeDiscoveryAdmission{}, nativeRoutingFailure("discovery_unavailable", err)
				}
				if call.Err != nil && !call.CallerCanceled {
					return nativeDiscoveryAdmission{}, call.Err
				}
				// A canceled initiating caller ends only its own fetch. Live
				// joiners compete for the next single-flight attempt using
				// their original contexts and the retained scope floor.
				continue
			}
		}
		if floor.Revision == 0 && len(r.floors) >= nativeDiscoveryCacheLimit || len(r.calls) >= nativeDiscoveryCacheLimit {
			r.mu.Unlock()
			return nativeDiscoveryAdmission{}, nativeRoutingFailure("routing_capacity", nil)
		}
		call := &nativeDiscoveryCall{Done: make(chan struct{})}
		r.calls[key] = call
		r.mu.Unlock()
		discoveryCtx, cancel := context.WithTimeout(ctx, r.timeout)
		lease, err := r.fetch(discoveryCtx, scope, credential, floor)
		cancel()
		r.mu.Lock()
		if err == nil {
			current := r.floors[scope]
			if !current.accepts(lease.Document) {
				err = nativeRoutingFailure("routing_observation_regressed", nil)
			} else if current.Revision == 0 && len(r.floors) >= nativeDiscoveryCacheLimit {
				err = nativeRoutingFailure("routing_capacity", nil)
			} else if !lease.valid(r.now()) {
				err = nativeRoutingFailure("discovery_expired", nil)
			} else {
				r.floors[scope] = nativeDiscoveryFloor{Authority: lease.Document.Authority, Revision: lease.Document.Revision, RefreshSeconds: lease.Document.RefreshSeconds}
				if len(r.leases) >= nativeDiscoveryCacheLimit {
					var oldestKey nativeDiscoveryKey
					oldest := ^uint64(0)
					for candidate, cached := range r.leases {
						if cached.Used < oldest {
							oldest, oldestKey = cached.Used, candidate
						}
					}
					delete(r.leases, oldestKey)
				}
				r.serial++
				lease.Used = r.serial
				if previous.Invalidated {
					lease.Failed = maps.Clone(previous.Failed)
				}
				r.leases[key] = lease
			}
		}
		call.Err = err
		if callerErr := ctx.Err(); callerErr != nil {
			call.CallerCanceled = errors.Is(err, callerErr)
		}
		delete(r.calls, key)
		close(call.Done)
		r.mu.Unlock()
		if err != nil {
			return nativeDiscoveryAdmission{}, err
		}
	}
}

// resolveHosted retains native admission errors, but uses the Atlas proxy when
// its native producer is unavailable. The proxy never grants native capabilities.
func (r *nativeDiscoveryRouter) resolveHosted(ctx context.Context, scope DocumentScope, credential string, required nativeRequirements) (nativeDiscoveryAdmission, error) {
	fingerprint, err := nativeCredentialFingerprint(credential)
	if err != nil {
		return nativeDiscoveryAdmission{}, err
	}
	if err := ctx.Err(); err != nil {
		return nativeDiscoveryAdmission{}, err
	}
	key := nativeDiscoveryKey{Scope: scope, Credential: fingerprint}
	r.mu.Lock()
	previous := r.leases[key]
	if previous.Proxy && previous.valid(r.now()) {
		r.serial++
		previous.Used = r.serial
		r.leases[key] = previous
		r.mu.Unlock()
		return nativeDiscoveryAdmission{URL: nativeProxyOrigin, Key: key, Lease: previous}, nil
	}
	r.mu.Unlock()
	admission, err := r.resolve(ctx, scope, credential, required)
	if err == nil {
		return admission, nil
	}
	if ctx.Err() != nil {
		return nativeDiscoveryAdmission{}, err
	}
	var apiError *APIError
	var routingError *NativeRoutingError
	unavailable := errors.As(err, &apiError) && (apiError.Status == 404 || apiError.Status >= 500)
	if errors.As(err, &routingError) {
		unavailable = routingError.Code == "discovery_unavailable" || routingError.Code == "invalid_discovery" || routingError.Code == "discovery_expired" || routingError.Code == "regions_unavailable"
	}
	if !unavailable {
		return nativeDiscoveryAdmission{}, err
	}
	r.mu.Lock()
	defer r.mu.Unlock()
	// A concurrent successful refresh takes precedence over a fallback decision.
	if current, ok := r.leases[key]; ok && !current.Proxy && !current.Invalidated && current.valid(r.now()) && r.floors[scope].accepts(current.Document) && len(current.Failed) == 0 {
		for _, endpoint := range current.Document.Endpoints {
			if endpoint.Capabilities.supports(required) {
				return nativeDiscoveryAdmission{URL: endpoint.URL, Key: key, Lease: current}, nil
			}
		}
	}
	refresh := nativeDefaultRefresh
	if cadence := r.floors[scope].RefreshSeconds; cadence > 0 {
		refresh = time.Duration(cadence) * time.Second
	}
	lease := nativeDiscoveryLease{Proxy: true, Document: previous.Document, Deadline: r.now().Add(refresh)}
	if len(r.leases) >= nativeDiscoveryCacheLimit {
		return nativeDiscoveryAdmission{}, nativeRoutingFailure("routing_capacity", nil)
	}
	r.serial++
	lease.Used = r.serial
	r.leases[key] = lease
	return nativeDiscoveryAdmission{URL: nativeProxyOrigin, Key: key, Lease: lease}, nil
}

// fail changes subsequent routing even when the failed operation cannot be replayed.
func (r *nativeDiscoveryRouter) fail(admission nativeDiscoveryAdmission) {
	if admission.Lease.Proxy {
		return
	}
	r.mu.Lock()
	defer r.mu.Unlock()
	lease, ok := r.leases[admission.Key]
	if !ok || lease.Proxy || lease.Document.Revision != admission.Lease.Document.Revision || lease.Document.Authority != admission.Lease.Document.Authority {
		return
	}
	lease.Failed = maps.Clone(lease.Failed)
	if lease.Failed == nil {
		lease.Failed = make(map[string]bool)
	}
	lease.Failed[admission.URL] = true
	lease.Invalidated = true
	r.leases[admission.Key] = lease
}

func (r *nativeDiscoveryRouter) fetch(ctx context.Context, scope DocumentScope, credential string, floor nativeDiscoveryFloor) (nativeDiscoveryLease, error) {
	query := url.Values{"tenant_id": {string(scope.Tenant)}, "database_id": {string(scope.Database)}, "shard_id": {strconv.FormatUint(uint64(scope.Shard), 10)}}
	if floor.Revision != 0 {
		query.Set("observed_deployment_id", floor.Authority.Deployment)
		query.Set("observed_cell", floor.Authority.Cell)
		query.Set("observed_publisher_generation", strconv.FormatUint(uint64(floor.Authority.Generation), 10))
		query.Set("rev", strconv.FormatUint(uint64(floor.Revision), 10))
	}
	req, err := http.NewRequestWithContext(ctx, http.MethodGet, nativeDiscoveryURL+"?"+query.Encode(), nil)
	if err != nil {
		return nativeDiscoveryLease{}, nativeRoutingFailure("discovery_unavailable", err)
	}
	req.Header.Set("Accept", "application/json")
	req.Header.Set("Authorization", "Bearer "+credential)
	started := r.now()
	response, err := r.http.Do(req)
	if err != nil {
		return nativeDiscoveryLease{}, nativeRoutingFailure("discovery_unavailable", err)
	}
	defer response.Body.Close()
	if response.StatusCode != http.StatusOK {
		body, _ := io.ReadAll(io.LimitReader(response.Body, nativeDiscoveryBodyLimit))
		return nativeDiscoveryLease{}, errorFromResponse("native.discovery", http.MethodGet, "/api/v1/discovery", response.StatusCode, response.Header, body)
	}
	if response.ContentLength > nativeDiscoveryBodyLimit {
		return nativeDiscoveryLease{}, nativeRoutingFailure("invalid_discovery", fmt.Errorf("discovery response byte limit"))
	}
	body, err := io.ReadAll(io.LimitReader(response.Body, nativeDiscoveryBodyLimit+1))
	if err != nil {
		return nativeDiscoveryLease{}, nativeRoutingFailure("discovery_unavailable", err)
	}
	if len(body) > nativeDiscoveryBodyLimit {
		return nativeDiscoveryLease{}, nativeRoutingFailure("invalid_discovery", fmt.Errorf("discovery response byte limit"))
	}
	if response.StatusCode != http.StatusOK {
		return nativeDiscoveryLease{}, errorFromResponse("native.discovery", http.MethodGet, "/api/v1/discovery", response.StatusCode, response.Header, body)
	}
	mediaType, _, err := mime.ParseMediaType(response.Header.Get("Content-Type"))
	if err != nil || mediaType != "application/json" {
		return nativeDiscoveryLease{}, nativeRoutingFailure("invalid_discovery", fmt.Errorf("discovery success requires application/json"))
	}
	var document nativeDiscoveryDocument
	if err := decodeNativeJSON(body, &document); err != nil {
		return nativeDiscoveryLease{}, nativeRoutingFailure("invalid_discovery", err)
	}
	if err := document.validate(scope); err != nil {
		return nativeDiscoveryLease{}, nativeRoutingFailure("invalid_discovery", err)
	}
	decided := r.now()
	if document.PublishedAt > decided.Unix() {
		return nativeDiscoveryLease{}, nativeRoutingFailure("invalid_discovery", fmt.Errorf("discovery publication is in the future"))
	}
	// Add the absolute-time remainder to the request-start time to retain its
	// monotonic clock. Network/decoding latency never renews the advertised TTL.
	lifetime := min(time.Duration(document.RefreshSeconds)*time.Second, time.Unix(document.ExpiresAt, 0).Sub(started))
	deadline := started.Add(lifetime)
	lease := nativeDiscoveryLease{Document: document, Deadline: deadline}
	if lifetime <= 0 || !lease.valid(decided) {
		return nativeDiscoveryLease{}, nativeRoutingFailure("discovery_expired", nil)
	}
	return lease, nil
}
