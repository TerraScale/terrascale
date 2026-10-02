package v1test

// The fake implements an in-memory TerraBase v1 server that speaks the frozen
// wire contracts in terrabase docs/{api-v1,query-v1,shapes-v1,control-plane-v1}.md
// over real HTTPS (TLS 1.3, HTTP/2). It is a development and contract-test
// fake, not a database: state lives in memory and is lost on Close. Acceptance
// tests must run against the real tb-server. It deliberately does not reuse
// the client's types so that encoding mistakes are not mirrored.
//
// Scope: route-level behaviour clients depend on (auth roles and grants,
// immediate revocation, preconditions, versions and ETags, idempotency replay
// and expiry, unique constraints, TTL hiding, patches, transactions with
// op-indexed errors, filters, sort, projection, keyset cursors, counts,
// explain, shapes with 307/snapshot/log/live/409/SSE, shape tokens verified by
// an edge proxy, and the control plane). The TTL reaper (shape tombstones for
// expired documents), row filters, rate limiting, home regions and real index
// planning are out of scope; faults such as 429 and 503 commit_uncertain are
// injected explicitly with Server.InjectFault.

import (
	"bytes"
	"context"
	"crypto/ed25519"
	"crypto/rand"
	"crypto/sha256"
	"encoding/base64"
	"encoding/binary"
	"encoding/hex"
	"encoding/json"
	"fmt"
	"io"
	"math/big"
	"net/http"
	"regexp"
	"slices"
	"strconv"
	"strings"
	"sync"
	"time"
)

type fxReply struct {
	status int
	header http.Header
	body   any // marshaled as JSON; nil means no body
}

func fxErr(status int, code string, kv ...any) fxReply {
	e := map[string]any{"code": code}
	for i := 0; i+1 < len(kv); i += 2 {
		e[kv[i].(string)] = kv[i+1]
	}
	return fxReply{status: status, body: map[string]any{"error": e}}
}

func fxOK(status int, body any) fxReply { return fxReply{status: status, body: body} }

type fxKey struct {
	id, token, name, role, tenantID string
	grants                          []fxGrant
	rowFilter                       json.RawMessage
	rate                            map[string]any
	createdAt, expiresAt, revokedAt string
}

type fxGrant struct {
	Database    string   `json:"database"`
	Collections []string `json:"collections"`
	Permissions []string `json:"permissions"`
}

type fxTenant struct {
	id, name, externalRef, status string
	limits                        map[string]any
	advisorExternal               bool
	createdAt                     string
	dbs                           map[string]*fxDB
	dbOrder                       []string
}

type fxDB struct {
	id, name, tenantID, homeRegion, status, durability, createdAt string
	retention                                                     map[string]any
	colls                                                         map[string]*fxColl
	collOrder                                                     []string
	head                                                          uint64
	log                                                           []fxCommit
}

type fxUnique struct {
	Name      string   `json:"name"`
	Paths     []string `json:"paths"`
	Transform string   `json:"transform,omitempty"`
}

type fxColl struct {
	id, name, createdAt, advisor string
	generation                   uint64
	schema                       json.RawMessage
	ttlPath                      *string
	unique                       []fxUnique
	indexes                      []map[string]any
	docs                         map[string]*fxDoc
}

type fxSibling struct {
	sequence uint64
	deleted  bool
	raw      json.RawMessage
}

type fxDoc struct {
	raw      json.RawMessage // nil when deleted or conflicted
	deleted  bool
	version  string
	siblings []fxSibling // set when conflicted
}

func (d *fxDoc) live() bool { return d != nil && !d.deleted }

// ttlInstant reads a ttl_path value: an RFC 3339 string or an integer of Unix
// milliseconds. ok is false for any other value.
func ttlInstant(v any) (time.Time, bool) {
	switch x := v.(type) {
	case string:
		t, err := time.Parse(time.RFC3339Nano, x)
		return t, err == nil
	case json.Number:
		ms, err := strconv.ParseInt(x.String(), 10, 64)
		return time.UnixMilli(ms), err == nil
	}
	return time.Time{}, false
}

// visible reports whether d is live and not TTL-expired: an expired document
// is treated as deleted by reads, queries and transactions immediately.
func (f *fixture) visible(c *fxColl, d *fxDoc) bool {
	if !d.live() {
		return false
	}
	if c == nil || c.ttlPath == nil || d.siblings != nil || d.raw == nil {
		return true
	}
	v, ok := lookup(mustObject(d.raw), *c.ttlPath)
	if !ok {
		return true
	}
	at, ok := ttlInstant(v)
	return !ok || at.After(f.now())
}

type fxChange struct {
	coll, id      string
	before, after *fxDoc
}

type fxCommit struct {
	offset  uint64
	changes []fxChange
}

type fxIdem struct {
	digest [32]byte
	reply  fxReply
	at     time.Time
}

type fxOperation struct {
	id, kind, resource, createdAt string
	started                       time.Time
	polls                         int
	status, finishedAt            string
	errCode, errMessage           string
	effect                        func() // applied when the operation succeeds
}

// fixture is the contract server. All state is guarded by mu.
type fixture struct {
	originURL, edgeURL string
	originClient       *http.Client // used by the edge proxy

	mu         sync.Mutex
	keys       map[string]*fxKey // by token
	tenants    map[string]*fxTenant
	tenantList []string
	idem       map[string]fxIdem
	faults     []*Fault
	ops        map[string]*fxOperation
	opFailures map[string]fxOpFailure // operation kind -> forced failure
	audit      []map[string]any
	shapes     map[string]*fxShape
	seq        uint64
	commitCh   chan struct{}
	requests   map[string]int // "METHOD /path" counts
	shapeLog   []string       // raw queries of origin shape reads
	clockSkew  time.Duration  // added to the wall clock by Server.Advance

	signer   ed25519.PrivateKey
	tokenTTL time.Duration
	liveWait time.Duration
	snapPage int
	maxCount int // assert_count examination limit
	maxChunk uint64
	sseBeat  time.Duration
	opDelay  time.Duration // operations finish after this long (or two polls)

	controlToken, edgeToken string
}

type fxOpFailure struct{ code, message string }

const idemTTL = 24 * time.Hour

func newFake(opts Options) (*fixture, error) {
	_, signer, err := ed25519.GenerateKey(rand.Reader)
	if err != nil {
		return nil, err
	}
	f := &fixture{keys: map[string]*fxKey{}, tenants: map[string]*fxTenant{}, idem: map[string]fxIdem{},
		ops: map[string]*fxOperation{}, opFailures: map[string]fxOpFailure{}, shapes: map[string]*fxShape{},
		commitCh: make(chan struct{}), requests: map[string]int{}, signer: signer,
		tokenTTL: cmpOrDur(opts.ShapeTokenTTL, 15*time.Minute), liveWait: cmpOrDur(opts.LiveWait, 20*time.Second),
		snapPage: cmpOrInt(opts.SnapshotPageSize, 1000), maxCount: cmpOrInt(opts.AssertCountLimit, 10000),
		maxChunk: uint64(cmpOrInt(int(opts.MaxChunk), 4096)), sseBeat: cmpOrDur(opts.SSEHeartbeat, 15*time.Second),
		opDelay: cmpOrDur(opts.OperationDelay, time.Second)}
	f.controlToken = f.addKey(&fxKey{name: "bootstrap", role: "control"})
	f.edgeToken = f.addKey(&fxKey{name: "edge", role: "edge"})
	return f, nil
}

func cmpOrDur(v, def time.Duration) time.Duration {
	if v > 0 {
		return v
	}
	return def
}

func cmpOrInt(v, def int) int {
	if v > 0 {
		return v
	}
	return def
}

// now is the fake's clock: the wall clock plus any Advance.
func (f *fixture) now() time.Time { return time.Now().Add(f.clockSkew) }

func (f *fixture) nowMS() string { return strconv.FormatInt(f.now().UnixMilli(), 10) }

// seed creates the "Acme" tenant with database "main", the collections the
// SDK tests use (users has a unique lower(/email) constraint) and a data key
// with read, write and shape on everything.
func (f *fixture) seed() Seed {
	tenant := f.newTenant("Acme", "proj_acme", nil)
	db := f.newDB(tenant, "main", "gru", nil)
	for _, name := range []string{"users", "orgs", "projects", "sessions", "plans", "invites", "notifications", "items"} {
		var unique []fxUnique
		if name == "users" {
			unique = []fxUnique{{Name: "email", Paths: []string{"/email"}, Transform: "lower"}}
		}
		f.newColl(db, name, nil, nil, unique)
	}
	token := f.addKey(&fxKey{name: "server", role: "data", tenantID: tenant.id,
		grants: []fxGrant{{Database: "*", Collections: []string{"*"}, Permissions: []string{"read", "write", "shape"}}}})
	return Seed{TenantID: tenant.id, Database: "main", DataKey: token}
}

func (f *fixture) newTenant(name, externalRef string, limits map[string]any) *fxTenant {
	if limits == nil {
		limits = map[string]any{"max_databases": 10, "max_storage_bytes": "10737418240", "max_rate_per_second": 1000}
	}
	t := &fxTenant{id: fxUUID(), name: name, externalRef: externalRef, status: "active", limits: limits,
		createdAt: fxNow(), dbs: map[string]*fxDB{}}
	f.tenants[t.id] = t
	f.tenantList = append(f.tenantList, t.id)
	return t
}

// randB64 returns base64url text of n random bytes.
func randB64(n int) string { return base64.RawURLEncoding.EncodeToString(randBytes(n)) }

func (f *fixture) addKey(k *fxKey) string {
	k.id = randB64(16)    // 22 chars
	secret := randB64(32) // 43 chars
	k.token = "tbk1." + k.id + "." + secret
	k.createdAt = fxNow()
	f.keys[k.token] = k
	return k.token
}

func randBytes(n int) []byte {
	b := make([]byte, n)
	rand.Read(b)
	return b
}

func fxNow() string { return strconv.FormatInt(time.Now().UnixMilli(), 10) }

// fxUUID returns a UUIDv7.
func fxUUID() string {
	var u [16]byte
	rand.Read(u[:])
	ms := uint64(time.Now().UnixMilli())
	binary.BigEndian.PutUint16(u[0:2], uint16(ms>>32))
	binary.BigEndian.PutUint32(u[2:6], uint32(ms))
	u[6] = u[6]&0x0f | 0x70
	u[8] = u[8]&0x3f | 0x80
	h := hex.EncodeToString(u[:])
	return h[0:8] + "-" + h[8:12] + "-" + h[12:16] + "-" + h[16:20] + "-" + h[20:]
}

var namePattern = regexp.MustCompile(`^[a-z][a-z0-9_-]{0,62}$`)

func (f *fixture) newDB(t *fxTenant, name, region string, retention map[string]any) *fxDB {
	if retention == nil {
		retention = map[string]any{"shape_log_bytes": "1073741824", "shape_log_age_seconds": "604800"}
	}
	db := &fxDB{id: fxUUID(), name: name, tenantID: t.id, homeRegion: region, status: "ready", durability: "local_durable",
		createdAt: fxNow(), retention: retention, colls: map[string]*fxColl{}}
	t.dbs[name] = db
	t.dbOrder = append(t.dbOrder, name)
	return db
}

func (f *fixture) newColl(db *fxDB, name string, schema json.RawMessage, ttl *string, unique []fxUnique) *fxColl {
	c := &fxColl{id: fxUUID(), name: name, createdAt: fxNow(), advisor: "auto", generation: 1, schema: schema, ttlPath: ttl,
		unique: unique, docs: map[string]*fxDoc{}}
	db.colls[name] = c
	db.collOrder = append(db.collOrder, name)
	return c
}

func (f *fixture) nextVersion(id string) string {
	f.seq++
	sum := sha256.Sum256([]byte(id + ":" + strconv.FormatUint(f.seq, 10)))
	return "tbv1-" + hex.EncodeToString(sum[:16])
}

func (f *fixture) takeFault(r *http.Request, afterCommit bool) *Fault {
	for _, fault := range f.faults {
		if fault.Remaining > 0 && fault.AfterCommit == afterCommit && fault.Method == r.Method && strings.HasPrefix(r.URL.Path, fault.PathPrefix) {
			fault.Remaining--
			return fault
		}
	}
	return nil
}

func faultReply(fault *Fault) fxReply {
	rep := fxErr(fault.Status, fault.Code)
	if fault.RetryAfter != "" {
		rep.header = http.Header{"Retry-After": {fault.RetryAfter}}
	}
	return rep
}

func writeReply(w http.ResponseWriter, rep fxReply) {
	for k, v := range rep.header {
		w.Header()[k] = v
	}
	if rep.body == nil {
		w.WriteHeader(rep.status)
		return
	}
	data, err := json.Marshal(rep.body)
	if err != nil {
		panic(err)
	}
	w.Header().Set("Content-Type", "application/json")
	if rep.status >= 400 {
		w.Header().Set("Cache-Control", "no-store")
	}
	w.WriteHeader(rep.status)
	w.Write(data)
}

type fxHandler func(r *http.Request, k *fxKey, body []byte) fxReply

type routeKind int

const (
	dataRoute routeKind = iota
	controlRoute
)

var idemPattern = func(s string) bool {
	if len(s) < 1 || len(s) > 64 {
		return false
	}
	for _, c := range s {
		if !(c >= 'a' && c <= 'z' || c >= 'A' && c <= 'Z' || c >= '0' && c <= '9' || c == '_' || c == '.' || c == '-') {
			return false
		}
	}
	return true
}

// authKey resolves the bearer key: nil for unknown, revoked or expired keys.
func (f *fixture) authKey(r *http.Request) *fxKey {
	k := f.keys[strings.TrimPrefix(r.Header.Get("Authorization"), "Bearer ")]
	if k == nil || k.revokedAt != "" || (k.expiresAt != "" && k.expiresAt <= f.nowMS()) {
		return nil
	}
	return k
}

// wrap authenticates, applies faults and idempotency, and runs h under mu.
func (f *fixture) wrap(kind routeKind, h fxHandler) http.HandlerFunc {
	return func(w http.ResponseWriter, r *http.Request) {
		body, err := io.ReadAll(io.LimitReader(r.Body, 4<<20+1))
		if err != nil {
			writeReply(w, fxErr(400, "invalid_request"))
			return
		}
		f.mu.Lock()
		defer f.mu.Unlock()
		defer func() {
			if p := recover(); p != nil {
				writeReply(w, fxErr(500, "internal_error", "message", fmt.Sprint(p)))
			}
		}()
		f.requests[r.Method+" "+r.URL.Path]++
		f.settleOperations()
		if len(body) > 4<<20 {
			writeReply(w, fxErr(413, "payload_too_large"))
			return
		}
		if r.Header.Get("Content-Encoding") != "" {
			writeReply(w, fxErr(415, "unsupported_media_type"))
			return
		}
		k := f.authKey(r)
		if k == nil {
			writeReply(w, fxErr(401, "invalid_key"))
			return
		}
		if (kind == controlRoute) != (k.role == "control") || k.role == "edge" {
			writeReply(w, fxErr(403, "forbidden_scope"))
			return
		}
		if fault := f.takeFault(r, false); fault != nil {
			writeReply(w, faultReply(fault))
			return
		}
		mutation := r.Method != http.MethodGet && !strings.HasSuffix(r.URL.Path, "/query") && !strings.HasSuffix(r.URL.Path, "/shapes")
		idemKey := r.Header.Get("Idempotency-Key")
		var slot string
		if mutation {
			if idemKey == "" && kind == controlRoute {
				writeReply(w, fxErr(400, "idempotency_key_required"))
				return
			}
			if idemKey != "" && !idemPattern(idemKey) {
				writeReply(w, fxErr(400, "invalid_request", "message", "bad Idempotency-Key"))
				return
			}
			if idemKey != "" {
				slot = k.id + " " + r.Method + " " + r.URL.Path + " " + idemKey
				digest := sha256.Sum256(append([]byte(r.Header.Get("Content-Type")+"\n"+r.Header.Get("If-Match")+"\n"+r.Header.Get("If-None-Match")+"\n"), body...))
				if prev, ok := f.idem[slot]; ok {
					switch {
					case f.now().Sub(prev.at) >= idemTTL:
						writeReply(w, fxErr(409, "idempotency_expired"))
					case prev.digest != digest:
						writeReply(w, fxErr(409, "idempotency_conflict"))
					default:
						writeReply(w, prev.reply)
					}
					return
				}
				rep := h(r, k, body)
				if rep.status < 500 {
					f.idem[slot] = fxIdem{digest: digest, reply: rep, at: f.now()}
				}
				f.afterMutation(r, k, idemKey, rep, kind)
				if fault := f.takeFault(r, true); fault != nil {
					rep = faultReply(fault)
				}
				writeReply(w, rep)
				return
			}
		}
		rep := h(r, k, body)
		if mutation {
			f.afterMutation(r, k, idemKey, rep, kind)
		}
		if fault := f.takeFault(r, true); fault != nil {
			rep = faultReply(fault)
		}
		writeReply(w, rep)
	}
}

func (f *fixture) afterMutation(r *http.Request, k *fxKey, idemKey string, rep fxReply, kind routeKind) {
	if kind != controlRoute {
		return
	}
	outcome := "succeeded"
	if rep.status >= 400 {
		outcome = "failed"
	}
	tenant := r.PathValue("tenant")
	if body, ok := rep.body.(map[string]any); ok && tenant == "" && r.URL.Path == "/v1/dashboard/tenants" {
		tenant, _ = body["id"].(string)
	}
	f.audit = append(f.audit, map[string]any{"id": fxUUID(), "at": fxNow(), "actor_key_id": k.id, "action": r.Method + " " + r.Pattern,
		"resource": r.URL.Path, "idempotency_key": idemKey, "outcome": outcome, "tenant": tenant})
}

// routes collects method patterns so every known path can answer other
// methods with 405 method_not_allowed in the contract's error envelope.
type routes struct {
	mux   *http.ServeMux
	paths []string
}

func (rt *routes) handle(pattern string, h http.HandlerFunc) {
	rt.mux.HandleFunc(pattern, h)
	_, path, _ := strings.Cut(pattern, " ")
	if !slices.Contains(rt.paths, path) {
		rt.paths = append(rt.paths, path)
	}
}

func (f *fixture) originHandler() http.Handler {
	rt := &routes{mux: http.NewServeMux()}
	docs := "/v1/databases/{db}/collections/{coll}/documents"
	rt.handle("GET "+docs+"/{id}", f.wrap(dataRoute, f.getDoc))
	rt.handle("PUT "+docs+"/{id}", f.wrap(dataRoute, f.putDoc))
	rt.handle("POST "+docs, f.wrap(dataRoute, f.createDoc))
	rt.handle("PATCH "+docs+"/{id}", f.wrap(dataRoute, f.patchDoc))
	rt.handle("DELETE "+docs+"/{id}", f.wrap(dataRoute, f.deleteDoc))
	rt.handle("POST /v1/databases/{db}/transactions", f.wrap(dataRoute, f.transaction))
	rt.handle("POST /v1/databases/{db}/collections/{coll}/query", f.wrap(dataRoute, f.query))
	rt.handle("POST /v1/databases/{db}/shapes", f.wrap(dataRoute, f.gatekeeper))
	rt.handle("GET /v1/shapes/{shape}", f.readShape)
	rt.handle("GET /v1/shapes/{shape}/sse", f.streamShape)
	rt.handle("GET /v1/.well-known/terrabase-edge-jwks.json", f.jwks)
	// The fake serves the admin probes on its single listener.
	probe := func(w http.ResponseWriter, _ *http.Request) { writeReply(w, fxOK(200, map[string]any{"status": "ok"})) }
	rt.handle("GET /v1/readyz", probe)
	rt.handle("GET /v1/healthz", probe)
	f.dashboardRoutes(rt)
	for _, path := range rt.paths {
		rt.mux.HandleFunc(path, func(w http.ResponseWriter, _ *http.Request) { writeReply(w, fxErr(405, "method_not_allowed")) })
	}
	rt.mux.HandleFunc("GET /{$}", func(w http.ResponseWriter, r *http.Request) {
		http.Redirect(w, r, "/v1/docs", http.StatusPermanentRedirect)
	})
	rt.mux.HandleFunc("/", func(w http.ResponseWriter, r *http.Request) { writeReply(w, fxErr(404, "unknown_route")) })
	return rt.mux
}

// jwks publishes the shape-token public key (shapes-v1).
func (f *fixture) jwks(w http.ResponseWriter, _ *http.Request) {
	pub := f.signer.Public().(ed25519.PublicKey)
	writeReply(w, fxOK(200, map[string]any{"keys": []any{map[string]any{"kty": "OKP", "crv": "Ed25519", "alg": "EdDSA", "use": "sig",
		"kid": "k1", "x": base64.RawURLEncoding.EncodeToString(pub)}}}))
}

func hasString(list []string, v string) bool {
	return slices.Contains(list, v) || slices.Contains(list, "*")
}

// scope resolves db/collection for a data key, enforcing grants.
func (f *fixture) scope(k *fxKey, dbName, collName, perm string) (*fxDB, *fxColl, *fxReply) {
	var grant *fxGrant
	for i, g := range k.grants {
		if (g.Database == dbName || g.Database == "*") && (collName == "" || hasString(g.Collections, collName)) && slices.Contains(g.Permissions, perm) {
			grant = &k.grants[i]
			break
		}
	}
	forbidden := fxErr(403, "forbidden_scope")
	if grant == nil {
		return nil, nil, &forbidden
	}
	t := f.tenants[k.tenantID]
	if t == nil || t.status != "active" {
		return nil, nil, &forbidden
	}
	db := t.dbs[dbName]
	if db == nil {
		if grant.Database != "*" {
			return nil, nil, &forbidden
		}
		rep := fxErr(404, "unknown_database")
		return nil, nil, &rep
	}
	if collName == "" {
		return db, nil, nil
	}
	c := db.colls[collName]
	if c == nil {
		if !slices.Contains(grant.Collections, "*") {
			return nil, nil, &forbidden
		}
		rep := fxErr(404, "unknown_collection")
		return nil, nil, &rep
	}
	return db, c, nil
}

func parseIfMatch(h string) string { return strings.Trim(strings.TrimSpace(h), `"`) }

func decodeObject(raw []byte) (map[string]any, error) {
	dec := json.NewDecoder(bytes.NewReader(raw))
	dec.UseNumber()
	var v any
	if err := dec.Decode(&v); err != nil {
		return nil, err
	}
	if dec.More() {
		return nil, fmt.Errorf("trailing data")
	}
	m, ok := v.(map[string]any)
	if !ok {
		return nil, fmt.Errorf("not an object")
	}
	return m, nil
}

func mustRaw(v any) json.RawMessage {
	data, err := json.Marshal(v)
	if err != nil {
		panic(err)
	}
	return data
}

func docJSON(id string, d *fxDoc) map[string]any {
	out := map[string]any{"id": id, "version": d.version}
	if d.siblings != nil {
		out["state"] = "conflict"
		sibs := make([]any, 0, len(d.siblings))
		for _, s := range d.siblings {
			e := map[string]any{"event": map[string]any{"replica": "0192f0c4-7a1e-7c3d-9b2a-5e8f1d2c3b4a", "incarnation": "8f14e45fceea167a5a36dedd4bea2543", "sequence": strconv.FormatUint(s.sequence, 10)}, "deleted": s.deleted}
			if !s.deleted {
				e["document"] = s.raw
			}
			sibs = append(sibs, e)
		}
		out["siblings"] = sibs
		return out
	}
	out["state"] = "value"
	out["document"] = d.raw
	return out
}

func etag(v string) http.Header { return http.Header{"ETag": {`"` + v + `"`}} }

// staged is a transaction's view: writes are buffered until commit.
type staged struct {
	db      *fxDB
	writes  map[string]*fxDoc // coll + "/" + id
	order   []string
	targets map[string]bool
}

func newStaged(db *fxDB) *staged {
	return &staged{db: db, writes: map[string]*fxDoc{}, targets: map[string]bool{}}
}

func (s *staged) get(coll, id string) *fxDoc {
	if d, ok := s.writes[coll+"/"+id]; ok {
		return d
	}
	return s.db.colls[coll].docs[id]
}

func (s *staged) put(coll, id string, d *fxDoc) {
	key := coll + "/" + id
	if _, ok := s.writes[key]; !ok {
		s.order = append(s.order, key)
	}
	s.writes[key] = d
}

// commit applies staged writes as one commit and returns its offset.
func (f *fixture) commit(s *staged) uint64 {
	s.db.head++
	c := fxCommit{offset: s.db.head}
	for _, key := range s.order {
		coll, id, _ := strings.Cut(key, "/")
		before := s.db.colls[coll].docs[id]
		after := s.writes[key]
		s.db.colls[coll].docs[id] = after
		c.changes = append(c.changes, fxChange{coll: coll, id: id, before: before, after: after})
	}
	s.db.log = append(s.db.log, c)
	close(f.commitCh)
	f.commitCh = make(chan struct{})
	return c.offset
}

func writeResult(id string, d *fxDoc, offset uint64) map[string]any {
	return map[string]any{"id": id, "version": d.version, "offset": strconv.FormatUint(offset, 10), "durability": "local_durable"}
}

// checkUnique enforces the collection's unique constraints against the
// staged view, excluding the document itself.
func (f *fixture) checkUnique(s *staged, coll *fxColl, id string, obj map[string]any) string {
	for _, u := range coll.unique {
		key, ok := uniqueKey(u, obj)
		if !ok {
			continue
		}
		seen := map[string]bool{id: true}
		check := func(otherID string, d *fxDoc) bool {
			if seen[otherID] || !f.visible(coll, d) || d.raw == nil {
				return false
			}
			seen[otherID] = true
			other, _ := decodeObject(d.raw)
			k2, ok := uniqueKey(u, other)
			return ok && k2 == key
		}
		for _, wkey := range s.order {
			c, oid, _ := strings.Cut(wkey, "/")
			if c == coll.name && check(oid, s.writes[wkey]) {
				return u.Name
			}
		}
		for oid, d := range coll.docs {
			if _, overridden := s.writes[coll.name+"/"+oid]; !overridden && check(oid, d) {
				return u.Name
			}
		}
	}
	return ""
}

func uniqueKey(u fxUnique, obj map[string]any) (string, bool) {
	parts := make([]string, 0, len(u.Paths))
	for _, p := range u.Paths {
		v, ok := lookup(obj, p)
		if !ok || v == nil {
			return "", false
		}
		if s, isStr := v.(string); isStr && u.Transform == "lower" {
			v = strings.ToLower(s)
		}
		parts = append(parts, string(mustRaw(v)))
	}
	return strings.Join(parts, "\x00"), true
}

// writeDoc applies one write op to the staged view. kind is put, patch or
// delete; ifMatch/ifNoneMatch are unquoted preconditions.
func (f *fixture) writeDoc(s *staged, coll *fxColl, kind, id, ifMatch, ifNoneMatch string, raw json.RawMessage, patch func(map[string]any) *fxReply) (*fxDoc, bool, *fxReply) {
	cur := s.get(coll.name, id)
	fail := func(status int, code string, kv ...any) (*fxDoc, bool, *fxReply) {
		rep := fxErr(status, code, kv...)
		return nil, false, &rep
	}
	if ifMatch != "" && (cur == nil || cur.version != ifMatch) {
		return fail(412, "precondition_failed")
	}
	live := f.visible(coll, cur)
	if ifNoneMatch == "*" && live {
		return fail(412, "precondition_failed")
	}
	created := !live
	switch kind {
	case "delete":
		if !live {
			return fail(404, "not_found")
		}
		return &fxDoc{deleted: true, version: f.nextVersion(id)}, false, nil
	case "patch":
		if !live {
			return fail(404, "not_found")
		}
		if cur.siblings != nil {
			return fail(409, "conflict_unresolved")
		}
		obj, _ := decodeObject(cur.raw)
		if rep := patch(obj); rep != nil {
			return nil, false, rep
		}
		raw = mustRaw(obj)
		created = false
	}
	obj, err := decodeObject(raw)
	if err != nil {
		return fail(400, "invalid_request", "message", "document must be a JSON object")
	}
	if coll.ttlPath != nil {
		if v, present := lookup(obj, *coll.ttlPath); present {
			if _, ok := ttlInstant(v); !ok {
				return fail(400, "ttl_invalid", "message", "ttl_path value must be an RFC 3339 string or integer Unix milliseconds")
			}
		}
	}
	if name := f.checkUnique(s, coll, id, obj); name != "" {
		return fail(409, "unique_violation", "constraint", name)
	}
	return &fxDoc{raw: mustRaw(obj), version: f.nextVersion(id)}, created, nil
}

func (f *fixture) docScope(r *http.Request, k *fxKey, perm string) (*fxDB, *fxColl, string, *fxReply) {
	db, coll, rep := f.scope(k, r.PathValue("db"), r.PathValue("coll"), perm)
	if rep != nil {
		return nil, nil, "", rep
	}
	id := r.PathValue("id")
	if id != "" && !isUUID(id) {
		e := fxErr(400, "invalid_request", "message", "bad id")
		return nil, nil, "", &e
	}
	return db, coll, id, nil
}

func isUUID(s string) bool {
	if len(s) != 36 {
		return false
	}
	for i, c := range s {
		if i == 8 || i == 13 || i == 18 || i == 23 {
			if c != '-' {
				return false
			}
		} else if !(c >= '0' && c <= '9' || c >= 'a' && c <= 'f') {
			return false
		}
	}
	return true
}

func (f *fixture) getDoc(r *http.Request, k *fxKey, _ []byte) fxReply {
	_, coll, id, rep := f.docScope(r, k, "read")
	if rep != nil {
		return *rep
	}
	d := coll.docs[id]
	if !f.visible(coll, d) {
		rep := fxErr(404, "not_found")
		if d != nil {
			rep.header = etag(d.version)
		}
		return rep
	}
	if parseIfMatch(r.Header.Get("If-None-Match")) == d.version {
		return fxReply{status: 304, header: etag(d.version)}
	}
	return fxReply{status: 200, header: etag(d.version), body: docJSON(id, d)}
}

func (f *fixture) putDoc(r *http.Request, k *fxKey, body []byte) fxReply {
	db, coll, id, rep := f.docScope(r, k, "write")
	if rep != nil {
		return *rep
	}
	s := newStaged(db)
	d, created, rep := f.writeDoc(s, coll, "put", id, parseIfMatch(r.Header.Get("If-Match")), r.Header.Get("If-None-Match"), body, nil)
	if rep != nil {
		return *rep
	}
	s.put(coll.name, id, d)
	offset := f.commit(s)
	status := 200
	if created {
		status = 201
	}
	return fxReply{status: status, header: etag(d.version), body: writeResult(id, d, offset)}
}

func (f *fixture) createDoc(r *http.Request, k *fxKey, body []byte) fxReply {
	db, coll, _, rep := f.docScope(r, k, "write")
	if rep != nil {
		return *rep
	}
	id := fxUUID()
	s := newStaged(db)
	d, _, rep := f.writeDoc(s, coll, "put", id, "", "*", body, nil)
	if rep != nil {
		return *rep
	}
	s.put(coll.name, id, d)
	offset := f.commit(s)
	h := etag(d.version)
	h.Set("Location", "/v1/databases/"+db.name+"/collections/"+coll.name+"/documents/"+id)
	return fxReply{status: 201, header: h, body: writeResult(id, d, offset)}
}

func (f *fixture) patchDoc(r *http.Request, k *fxKey, body []byte) fxReply {
	db, coll, id, rep := f.docScope(r, k, "write")
	if rep != nil {
		return *rep
	}
	var apply func(map[string]any) *fxReply
	switch r.Header.Get("Content-Type") {
	case "application/merge-patch+json":
		merge, err := decodeObject(body)
		if err != nil {
			return fxErr(400, "invalid_patch")
		}
		apply = func(obj map[string]any) *fxReply { mergePatch(obj, merge); return nil }
	case "application/vnd.terrabase.ops+json":
		var p struct {
			Ops []json.RawMessage `json:"ops"`
		}
		if err := strictDecode(body, &p); err != nil || len(p.Ops) == 0 || len(p.Ops) > 256 {
			return fxErr(400, "invalid_patch")
		}
		apply = func(obj map[string]any) *fxReply { return applyOps(obj, p.Ops) }
	default:
		return fxErr(415, "unsupported_media_type")
	}
	s := newStaged(db)
	d, _, rep := f.writeDoc(s, coll, "patch", id, parseIfMatch(r.Header.Get("If-Match")), "", nil, apply)
	if rep != nil {
		return *rep
	}
	s.put(coll.name, id, d)
	offset := f.commit(s)
	return fxReply{status: 200, header: etag(d.version), body: writeResult(id, d, offset)}
}

func (f *fixture) deleteDoc(r *http.Request, k *fxKey, _ []byte) fxReply {
	db, coll, id, rep := f.docScope(r, k, "write")
	if rep != nil {
		return *rep
	}
	s := newStaged(db)
	d, _, rep := f.writeDoc(s, coll, "delete", id, parseIfMatch(r.Header.Get("If-Match")), "", nil, nil)
	if rep != nil {
		return *rep
	}
	s.put(coll.name, id, d)
	offset := f.commit(s)
	return fxReply{status: 200, body: writeResult(id, d, offset)}
}

func strictDecode(data []byte, v any) error {
	dec := json.NewDecoder(bytes.NewReader(data))
	dec.UseNumber()
	dec.DisallowUnknownFields()
	if err := dec.Decode(v); err != nil {
		return err
	}
	if dec.More() {
		return fmt.Errorf("trailing data")
	}
	return nil
}

func mergePatch(target, patch map[string]any) {
	for k, v := range patch {
		switch pv := v.(type) {
		case nil:
			delete(target, k)
		case map[string]any:
			sub, ok := target[k].(map[string]any)
			if !ok {
				sub = map[string]any{}
			}
			mergePatch(sub, pv)
			target[k] = sub
		default:
			target[k] = v
		}
	}
}

func splitPointer(p string) []string {
	if !strings.HasPrefix(p, "/") {
		return nil
	}
	segs := strings.Split(p[1:], "/")
	for i, s := range segs {
		segs[i] = strings.ReplaceAll(strings.ReplaceAll(s, "~1", "/"), "~0", "~")
	}
	return segs
}

func lookup(obj map[string]any, pointer string) (any, bool) {
	var cur any = obj
	for _, seg := range splitPointer(pointer) {
		m, ok := cur.(map[string]any)
		if !ok {
			return nil, false
		}
		if cur, ok = m[seg]; !ok {
			return nil, false
		}
	}
	return cur, true
}

// parent returns the object holding the pointer's last member, creating
// missing parents when create is set.
func parent(obj map[string]any, pointer string, create bool) (map[string]any, string, bool) {
	segs := splitPointer(pointer)
	if len(segs) == 0 {
		return nil, "", false
	}
	cur := obj
	for _, seg := range segs[:len(segs)-1] {
		next, ok := cur[seg].(map[string]any)
		if !ok {
			if _, exists := cur[seg]; exists || !create {
				return nil, "", false
			}
			next = map[string]any{}
			cur[seg] = next
		}
		cur = next
	}
	return cur, segs[len(segs)-1], true
}

func applyOps(obj map[string]any, ops []json.RawMessage) *fxReply {
	conflict := fxErr(409, "patch_conflict")
	invalid := fxErr(400, "invalid_patch")
	for _, raw := range ops {
		var op struct {
			Op    string          `json:"op"`
			Path  string          `json:"path"`
			Value json.RawMessage `json:"value"`
			By    json.Number     `json:"by"`
		}
		if strictDecode(raw, &op) != nil {
			return &invalid
		}
		var value any
		if op.Value != nil {
			dec := json.NewDecoder(bytes.NewReader(op.Value))
			dec.UseNumber()
			if dec.Decode(&value) != nil {
				return &invalid
			}
		}
		p, last, ok := parent(obj, op.Path, op.Op != "unset")
		if !ok {
			if op.Op == "unset" {
				continue
			}
			return &conflict
		}
		switch op.Op {
		case "set":
			if op.Value == nil {
				return &invalid
			}
			p[last] = value
		case "unset":
			delete(p, last)
		case "inc":
			by, ok := new(big.Rat).SetString(op.By.String())
			if !ok {
				return &invalid
			}
			cur := new(big.Rat)
			if existing, present := p[last]; present {
				n, isNum := existing.(json.Number)
				if !isNum {
					return &conflict
				}
				cur.SetString(n.String())
			}
			p[last] = json.Number(ratString(cur.Add(cur, by)))
		case "append":
			existing, present := p[last]
			arr, isArr := existing.([]any)
			if present && !isArr {
				return &conflict
			}
			p[last] = append(arr, value)
		default:
			return &invalid
		}
	}
	return nil
}

func ratString(r *big.Rat) string {
	if r.IsInt() {
		return r.Num().String()
	}
	for scale := 1; scale <= 128; scale++ {
		probe := new(big.Rat).Mul(r, new(big.Rat).SetInt(new(big.Int).Exp(big.NewInt(10), big.NewInt(int64(scale)), nil)))
		if probe.IsInt() {
			return r.FloatString(scale)
		}
	}
	return r.FloatString(128)
}

func (f *fixture) transaction(r *http.Request, k *fxKey, body []byte) fxReply {
	var req struct {
		Ops []json.RawMessage `json:"ops"`
	}
	if strictDecode(body, &req) != nil || len(req.Ops) == 0 || len(req.Ops) > 1024 {
		return fxErr(400, "invalid_request")
	}
	type op struct {
		Op          string            `json:"op"`
		Collection  string            `json:"collection"`
		ID          string            `json:"id"`
		Document    json.RawMessage   `json:"document"`
		IfMatch     string            `json:"if_match"`
		IfNoneMatch string            `json:"if_none_match"`
		Merge       json.RawMessage   `json:"merge"`
		Ops         []json.RawMessage `json:"ops"`
		Filter      json.RawMessage   `json:"filter"`
		Absent      bool              `json:"absent"`
		Gte         *uint64           `json:"gte"`
		Lte         *uint64           `json:"lte"`
	}
	countOps := 0
	dbName := r.PathValue("db")
	var s *staged
	results := make([]any, 0, len(req.Ops))
	for i, raw := range req.Ops {
		var o op
		if strictDecode(raw, &o) != nil {
			return fxErr(400, "invalid_request", "op", i)
		}
		perm := "write"
		if o.Op == "assert" || o.Op == "assert_count" {
			perm = "read"
		}
		db, coll, rep := f.scope(k, dbName, o.Collection, perm)
		if rep != nil {
			rep.body.(map[string]any)["error"].(map[string]any)["op"] = i
			return *rep
		}
		if s == nil {
			s = newStaged(db)
		}
		if o.Op == "assert_count" {
			countOps++
			filter, err := parseFilter(o.Filter)
			if err != nil || o.ID != "" || (o.Gte == nil && o.Lte == nil) || countOps > 16 {
				return fxErr(400, "invalid_request", "op", i)
			}
			// Evaluated before this transaction's writes.
			live, n := 0, uint64(0)
			for id, d := range coll.docs {
				if !f.visible(coll, d) {
					continue
				}
				if live++; live > f.maxCount {
					return fxErr(400, "assert_requires_index", "op", i)
				}
				if d.siblings == nil && filter.match(id, mustObject(d.raw)) {
					n++
				}
			}
			if (o.Gte != nil && n < *o.Gte) || (o.Lte != nil && n > *o.Lte) {
				return fxErr(412, "precondition_failed", "op", i)
			}
			results = append(results, map[string]any{"asserted": true, "count": strconv.FormatUint(n, 10)})
			continue
		}
		if o.Op == "create" && (o.IfMatch != "" || o.IfNoneMatch != "") {
			return fxErr(400, "invalid_request", "op", i, "message", "create takes no preconditions")
		}
		if o.ID == "" && o.Op == "create" {
			o.ID = fxUUID()
		}
		if !isUUID(o.ID) || s.targets[o.Collection+"/"+o.ID] {
			return fxErr(400, "invalid_request", "op", i)
		}
		s.targets[o.Collection+"/"+o.ID] = true
		var (
			d *fxDoc
		)
		switch o.Op {
		case "create":
			d, _, rep = f.writeDoc(s, coll, "put", o.ID, "", "*", o.Document, nil)
		case "put":
			d, _, rep = f.writeDoc(s, coll, "put", o.ID, o.IfMatch, o.IfNoneMatch, o.Document, nil)
		case "delete":
			d, _, rep = f.writeDoc(s, coll, "delete", o.ID, o.IfMatch, "", nil, nil)
		case "patch":
			if (o.Merge == nil) == (o.Ops == nil) {
				return fxErr(400, "invalid_patch", "op", i)
			}
			apply := func(obj map[string]any) *fxReply { return applyOps(obj, o.Ops) }
			if o.Merge != nil {
				merge, err := decodeObject(o.Merge)
				if err != nil {
					return fxErr(400, "invalid_patch", "op", i)
				}
				apply = func(obj map[string]any) *fxReply { mergePatch(obj, merge); return nil }
			}
			d, _, rep = f.writeDoc(s, coll, "patch", o.ID, o.IfMatch, "", nil, apply)
		case "assert":
			cur := s.get(coll.name, o.ID)
			ok := !f.visible(coll, cur)
			if !o.Absent {
				filter, err := parseFilter(o.Filter)
				if err != nil {
					return fxErr(400, "invalid_query", "op", i)
				}
				ok = f.visible(coll, cur) && cur.siblings == nil && filter.match(o.ID, mustObject(cur.raw))
			}
			if !ok {
				return fxErr(412, "precondition_failed", "op", i)
			}
			results = append(results, map[string]any{"asserted": true})
			continue
		default:
			return fxErr(400, "invalid_request", "op", i)
		}
		if rep != nil {
			rep.body.(map[string]any)["error"].(map[string]any)["op"] = i
			return *rep
		}
		s.put(coll.name, o.ID, d)
		results = append(results, map[string]any{"id": o.ID, "version": d.version})
	}
	offset := f.commit(s)
	return fxOK(200, map[string]any{"offset": strconv.FormatUint(offset, 10), "durability": "local_durable", "results": results})
}

func mustObject(raw json.RawMessage) map[string]any {
	m, _ := decodeObject(raw)
	return m
}

// wait blocks until a commit happens or the context/timeout ends. Callers
// must not hold mu.
func (f *fixture) waitCommit(ctx context.Context, ch <-chan struct{}, d time.Duration) {
	t := time.NewTimer(d)
	defer t.Stop()
	select {
	case <-ch:
	case <-t.C:
	case <-ctx.Done():
	}
}
