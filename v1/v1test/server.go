// Package v1test provides an in-memory TerraBase v1 fake that serves the
// frozen v1 wire contracts (terrabase docs/api-v1.md, query-v1.md,
// shapes-v1.md and control-plane-v1.md) over real HTTPS, in the style of
// net/http/httptest:
//
//	srv := v1test.NewServer(v1test.Options{})
//	defer srv.Close()
//	p, _ := srv.Provision(v1test.ProvisionRequest{Collections: []v1test.CollectionSpec{{Name: "todos"}}})
//	client, _ := v1.New(v1.Config{BaseURL: srv.URL, Token: p.DataKey, HTTPClient: srv.Client()})
//
// It is a development and contract-test fake only. State is held in memory
// and lost on Close; there is no persistence, replication, rate limiting,
// row-filter enforcement or TTL reaper. Acceptance tests must use the real
// tb-server.
package v1test

import (
	"crypto/tls"
	"crypto/x509"
	"encoding/json"
	"errors"
	"fmt"
	"io"
	"log"
	"net"
	"net/http"
	"slices"
	"time"
)

// Options configures a Server. The zero value starts an unseeded fake on
// random loopback ports with a freshly generated CA.
type Options struct {
	// Seed creates the "Acme" tenant with database "main", the collections
	// users (unique lower(/email)), orgs, projects, sessions, plans,
	// invites, notifications and items, and an all-access data key.
	Seed bool

	// Listener and EdgeListener receive the origin and edge-proxy traffic.
	// They default to 127.0.0.1:0.
	Listener, EdgeListener net.Listener

	// Certificates is the TLS material; nil generates a CA and a leaf for
	// DefaultHosts.
	Certificates *Certificates

	// ErrorLog receives server errors; nil discards them.
	ErrorLog *log.Logger

	ShapeTokenTTL    time.Duration // default 15m
	LiveWait         time.Duration // live long-poll wait, default 20s
	SSEHeartbeat     time.Duration // default 15s
	OperationDelay   time.Duration // async operations finish after this or two polls, default 1s
	SnapshotPageSize int           // default 1000
	AssertCountLimit int           // assert_count examination limit, default 10000
	MaxChunk         uint64        // shape log chunk size in offsets, default 4096
}

// Seed describes the data created by Options.Seed.
type Seed struct {
	TenantID string
	Database string
	DataKey  string
}

// Server is a running fake.
type Server struct {
	URL        string // origin base URL, https://127.0.0.1:port
	EdgeURL    string // edge-proxy base URL
	ControlKey string // bootstrap control-role key
	CACertPEM  []byte // CA that signed the serving certificate
	Seed       *Seed  // non-nil when Options.Seed was set

	f      *fixture
	origin *http.Server
	edge   *http.Server
	client *http.Client
}

// NewServer starts a fake and panics on failure, like httptest.NewServer.
func NewServer(opts Options) *Server {
	s, err := Start(opts)
	if err != nil {
		panic("v1test: " + err.Error())
	}
	return s
}

// Start starts a fake.
func Start(opts Options) (*Server, error) {
	f, err := newFake(opts)
	if err != nil {
		return nil, err
	}
	certs := opts.Certificates
	if certs == nil {
		if certs, err = GenerateCertificates(nil, 0); err != nil {
			return nil, err
		}
	}
	leaf, err := certs.TLSCertificate()
	if err != nil {
		return nil, err
	}
	pool := x509.NewCertPool()
	if !pool.AppendCertsFromPEM(certs.CACertPEM) {
		return nil, errors.New("invalid CA certificate PEM")
	}
	listen := func(l net.Listener) (net.Listener, error) {
		if l != nil {
			return l, nil
		}
		return net.Listen("tcp", "127.0.0.1:0")
	}
	originLn, err := listen(opts.Listener)
	if err != nil {
		return nil, err
	}
	edgeLn, err := listen(opts.EdgeListener)
	if err != nil {
		originLn.Close()
		return nil, err
	}
	errLog := opts.ErrorLog
	if errLog == nil {
		errLog = log.New(io.Discard, "", 0)
	}
	s := &Server{
		URL:        "https://" + originLn.Addr().String(),
		EdgeURL:    "https://" + edgeLn.Addr().String(),
		ControlKey: f.controlToken,
		CACertPEM:  certs.CACertPEM,
		f:          f,
		client:     newClient(pool),
	}
	f.originURL, f.edgeURL = s.URL, s.EdgeURL
	f.originClient = newClient(pool)
	f.originClient.CheckRedirect = func(*http.Request, []*http.Request) error { return http.ErrUseLastResponse }
	if opts.Seed {
		seed := f.seed()
		s.Seed = &seed
	}
	serve := func(ln net.Listener, h http.Handler) *http.Server {
		srv := &http.Server{
			Handler:           h,
			TLSConfig:         &tls.Config{MinVersion: tls.VersionTLS13, Certificates: []tls.Certificate{leaf}, NextProtos: []string{"h2", "http/1.1"}},
			ReadHeaderTimeout: 10 * time.Second,
			ErrorLog:          errLog,
		}
		go srv.ServeTLS(ln, "", "")
		return srv
	}
	s.origin = serve(originLn, f.originHandler())
	s.edge = serve(edgeLn, http.HandlerFunc(f.serveEdge))
	return s, nil
}

func newClient(pool *x509.CertPool) *http.Client {
	return &http.Client{Transport: &http.Transport{
		TLSClientConfig:   &tls.Config{RootCAs: pool, MinVersion: tls.VersionTLS13},
		ForceAttemptHTTP2: true,
		Proxy:             nil,
	}}
}

// Client returns an HTTP client that trusts the fake's CA (origin and edge).
func (s *Server) Client() *http.Client { return s.client }

// Close stops both listeners and drops all connections.
func (s *Server) Close() {
	s.origin.Close()
	s.edge.Close()
	if tr, ok := s.client.Transport.(*http.Transport); ok {
		tr.CloseIdleConnections()
	}
}

// Grant is a data-key grant (control-plane-v1 keys).
type Grant struct {
	Database    string
	Collections []string
	Permissions []string
}

// Unique is a unique constraint; Transform is "" or "lower".
type Unique struct {
	Name      string
	Paths     []string
	Transform string
}

// CollectionSpec describes a collection to create.
type CollectionSpec struct {
	Name    string
	TTLPath string          // "" for none
	Unique  []Unique        // optional
	Schema  json.RawMessage // nil for schemaless
}

// CreateTenant creates an active tenant and returns its ID.
func (s *Server) CreateTenant(name, externalRef string) (string, error) {
	s.f.mu.Lock()
	defer s.f.mu.Unlock()
	if name == "" || externalRef == "" {
		return "", errors.New("name and externalRef are required")
	}
	for _, t := range s.f.tenants {
		if t.externalRef == externalRef {
			return "", fmt.Errorf("external_ref %q is taken", externalRef)
		}
	}
	return s.f.newTenant(name, externalRef, nil).id, nil
}

// CreateDatabase creates a ready database in the tenant.
func (s *Server) CreateDatabase(tenantID, name string) error {
	s.f.mu.Lock()
	defer s.f.mu.Unlock()
	t := s.f.tenants[tenantID]
	switch {
	case t == nil:
		return fmt.Errorf("unknown tenant %q", tenantID)
	case !namePattern.MatchString(name):
		return fmt.Errorf("invalid database name %q", name)
	case t.dbs[name] != nil:
		return fmt.Errorf("database %q exists", name)
	}
	s.f.newDB(t, name, "gru", nil)
	return nil
}

// CreateCollection creates a collection in an existing database.
func (s *Server) CreateCollection(tenantID, database string, spec CollectionSpec) error {
	s.f.mu.Lock()
	defer s.f.mu.Unlock()
	t := s.f.tenants[tenantID]
	if t == nil || t.dbs[database] == nil {
		return fmt.Errorf("unknown database %q", database)
	}
	db := t.dbs[database]
	unique := make([]fxUnique, 0, len(spec.Unique))
	for _, u := range spec.Unique {
		unique = append(unique, fxUnique{Name: u.Name, Paths: u.Paths, Transform: u.Transform})
	}
	var ttl *string
	if spec.TTLPath != "" {
		ttl = &spec.TTLPath
	}
	switch {
	case !namePattern.MatchString(spec.Name):
		return fmt.Errorf("invalid collection name %q", spec.Name)
	case db.colls[spec.Name] != nil:
		return fmt.Errorf("collection %q exists", spec.Name)
	case !validUnique(unique):
		return errors.New("invalid unique constraints")
	case ttl != nil && len(splitPointer(*ttl)) == 0:
		return fmt.Errorf("invalid ttl_path %q", *ttl)
	}
	c := s.f.newColl(db, spec.Name, spec.Schema, ttl, unique)
	for _, u := range unique {
		keys := make([]fxIndexKey, 0, len(u.Paths))
		for _, p := range u.Paths {
			keys = append(keys, fxIndexKey{Path: p, Order: "asc", Transform: u.Transform})
		}
		c.indexes = append(c.indexes, newIndex("unique_"+u.Name, keys, "constraint", true, "active"))
	}
	return nil
}

// CreateDataKey creates a data-role key with the grants and returns its token.
func (s *Server) CreateDataKey(tenantID string, grants ...Grant) (string, error) {
	s.f.mu.Lock()
	defer s.f.mu.Unlock()
	if s.f.tenants[tenantID] == nil {
		return "", fmt.Errorf("unknown tenant %q", tenantID)
	}
	gs := make([]fxGrant, 0, len(grants))
	for _, g := range grants {
		gs = append(gs, fxGrant{Database: g.Database, Collections: g.Collections, Permissions: g.Permissions})
	}
	if len(gs) == 0 || !validGrants(gs) {
		return "", errors.New("invalid grants")
	}
	return s.f.addKey(&fxKey{name: "v1test", role: "data", tenantID: tenantID, grants: gs,
		rate: map[string]any{"per_second": 100, "burst": 200, "max_in_flight": 16}}), nil
}

// RevokeKey revokes any key by token, effective for the next request. It
// reports whether the key existed.
func (s *Server) RevokeKey(token string) bool {
	s.f.mu.Lock()
	defer s.f.mu.Unlock()
	k := s.f.keys[token]
	if k == nil {
		return false
	}
	if k.revokedAt == "" {
		k.revokedAt = fxNow()
	}
	return true
}

// ProvisionRequest describes a tenant, database, collections and data key to
// create in one step. Empty names get defaults.
type ProvisionRequest struct {
	TenantName  string // default "Tenant <n>"
	ExternalRef string // default "proj_<random>"
	Database    string // default "main"
	Collections []CollectionSpec
	Permissions []string // default read, write and shape on every collection
}

// Provisioned is the result of Provision.
type Provisioned struct {
	TenantID string
	Database string
	DataKey  string
}

// Provision creates a tenant, a database, its collections and a data key.
func (s *Server) Provision(req ProvisionRequest) (Provisioned, error) {
	if req.ExternalRef == "" {
		req.ExternalRef = "proj_" + randB64(9)
	}
	if req.TenantName == "" {
		req.TenantName = "Tenant " + req.ExternalRef
	}
	if req.Database == "" {
		req.Database = "main"
	}
	if req.Permissions == nil {
		req.Permissions = []string{"read", "write", "shape"}
	}
	tenantID, err := s.CreateTenant(req.TenantName, req.ExternalRef)
	if err != nil {
		return Provisioned{}, err
	}
	if err := s.CreateDatabase(tenantID, req.Database); err != nil {
		return Provisioned{}, err
	}
	for _, c := range req.Collections {
		if err := s.CreateCollection(tenantID, req.Database, c); err != nil {
			return Provisioned{}, err
		}
	}
	key, err := s.CreateDataKey(tenantID, Grant{Database: req.Database, Collections: []string{"*"}, Permissions: req.Permissions})
	if err != nil {
		return Provisioned{}, err
	}
	return Provisioned{TenantID: tenantID, Database: req.Database, DataKey: key}, nil
}

// Fault is an injected failure for requests whose method matches and whose
// path starts with PathPrefix. AfterCommit faults replace the response after
// the request took effect (for example 503 commit_uncertain). Remaining is the
// number of requests to fail (default 1).
type Fault struct {
	Method, PathPrefix string
	Status             int
	Code               string
	RetryAfter         string
	AfterCommit        bool
	Remaining          int
}

// InjectFault queues a fault.
func (s *Server) InjectFault(fault Fault) {
	s.f.mu.Lock()
	defer s.f.mu.Unlock()
	if fault.Remaining == 0 {
		fault.Remaining = 1
	}
	s.f.faults = append(s.f.faults, &fault)
}

// FaultsLeft reports how many injected fault requests remain.
func (s *Server) FaultsLeft() int {
	s.f.mu.Lock()
	defer s.f.mu.Unlock()
	n := 0
	for _, fault := range s.f.faults {
		n += fault.Remaining
	}
	return n
}

// RequestCount reports how many requests reached "METHOD path". Edge
// requests count as ("EDGE", method) and edge rejections as ("EDGE", status).
func (s *Server) RequestCount(method, path string) int {
	s.f.mu.Lock()
	defer s.f.mu.Unlock()
	return s.f.requests[method+" "+path]
}

// ShapeReadLog returns the raw query strings of origin shape reads.
func (s *Server) ShapeReadLog() []string {
	s.f.mu.Lock()
	defer s.f.mu.Unlock()
	return slices.Clone(s.f.shapeLog)
}

// SetAssertCountLimit changes the assert_count examination limit.
func (s *Server) SetAssertCountLimit(n int) {
	s.f.mu.Lock()
	defer s.f.mu.Unlock()
	s.f.maxCount = n
}

// SetSnapshotPageSize changes the shape snapshot page size.
func (s *Server) SetSnapshotPageSize(n int) {
	s.f.mu.Lock()
	defer s.f.mu.Unlock()
	s.f.snapPage = n
}

// SetShapeTokenTTL changes the lifetime of newly issued shape tokens.
func (s *Server) SetShapeTokenTTL(d time.Duration) {
	s.f.mu.Lock()
	defer s.f.mu.Unlock()
	s.f.tokenTTL = d
}

// FailOperations makes every later-finishing operation of kind (for example
// "delete_collection") fail with the error code and message.
func (s *Server) FailOperations(kind, code, message string) {
	s.f.mu.Lock()
	defer s.f.mu.Unlock()
	s.f.opFailures[kind] = fxOpFailure{code: code, message: message}
}

// Advance moves the fake's clock forward. It affects TTL expiry, key expiry
// and the 24-hour idempotency window, not shape tokens or timestamps.
func (s *Server) Advance(d time.Duration) {
	s.f.mu.Lock()
	defer s.f.mu.Unlock()
	s.f.clockSkew += d
}

// SeedConflict replaces a document with a conflicted state of two live
// siblings a and b (JSON objects) and returns its version.
func (s *Server) SeedConflict(tenantID, database, collection, id, a, b string) (string, error) {
	s.f.mu.Lock()
	defer s.f.mu.Unlock()
	t := s.f.tenants[tenantID]
	if t == nil || t.dbs[database] == nil || t.dbs[database].colls[collection] == nil {
		return "", fmt.Errorf("unknown collection %s/%s", database, collection)
	}
	st := newStaged(t.dbs[database])
	doc := &fxDoc{version: s.f.nextVersion(id), siblings: []fxSibling{{sequence: 12, raw: json.RawMessage(a)}, {sequence: 14, raw: json.RawMessage(b)}}}
	st.put(collection, id, doc)
	s.f.commit(st)
	return doc.version, nil
}

// RotateShapeHandle gives a shape a new handle, simulating a new lineage
// (generation change or retention), so readers get 409 must_refetch.
func (s *Server) RotateShapeHandle(shapeID string) {
	s.f.mu.Lock()
	defer s.f.mu.Unlock()
	if sh := s.f.shapes[shapeID]; sh != nil {
		sh.handle = "h1-" + randB64(6)
		close(s.f.commitCh)
		s.f.commitCh = make(chan struct{})
	}
}
