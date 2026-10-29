package pitrbench

import (
	"encoding/json"
	"fmt"
	"io"
	"net"
	"net/http"
	"net/http/httptest"
	"path/filepath"
	"slices"
	"strings"
	"sync"
	"testing"
	"time"

	"terrascale.tech/ts-benchmark/internal/benchclient"
)

type fakeDB struct {
	mu         sync.Mutex
	docs       map[string]*fakeDoc
	other      map[string]map[string]*fakeDoc
	byAcct     map[string]map[string]bool
	applied    map[string]int
	seq        int
	tx         int
	gets       int
	dropDelete map[string]bool
	fault      func(n int) *fault
	noBackup   bool

	ignoreAsserts bool
	nudges        []fakeNudge
	indexHides    map[string]bool

	writes       int
	shippedSeq   int
	lastShipped  time.Time
	lastPass     time.Time
	racyMarker   bool
	stopAtMarker bool
	stopOnNudge  string
	stopped      bool

	restoreStatus int
	restoreBody   string
	window        string
}

type fakeDoc struct {
	live    bool
	version string
	body    []byte
	acct    string
	write   int
}

type fakeNudge struct {
	why string
	at  time.Time
}

type fault struct {
	hold         chan struct{}
	evaluated    chan int
	abortAfter   bool
	failAfter    bool
	afterCommit  func()
	reject       int
	partialApply int
}

func (f *fakeDB) setFault(fn func(n int) *fault) {
	f.mu.Lock()
	f.fault = fn
	f.mu.Unlock()
}

func newFakeDB() *fakeDB {
	return &fakeDB{docs: map[string]*fakeDoc{}, other: map[string]map[string]*fakeDoc{}, byAcct: map[string]map[string]bool{},
		applied: map[string]int{}, dropDelete: map[string]bool{}, indexHides: map[string]bool{}}
}

type fakeOp struct {
	Op         string          `json:"op"`
	Collection string          `json:"collection"`
	ID         string          `json:"id"`
	IfMatch    string          `json:"if_match"`
	Absent     bool            `json:"absent"`
	Document   json.RawMessage `json:"document"`
}

func (f *fakeDB) coll(name string) map[string]*fakeDoc {
	if name == "events" {
		return f.docs
	}
	if f.other[name] == nil {
		f.other[name] = map[string]*fakeDoc{}
	}
	return f.other[name]
}

func (f *fakeDB) live(name string) []string {
	f.mu.Lock()
	defer f.mu.Unlock()
	var ids []string
	for id, d := range f.coll(name) {
		if d.live {
			ids = append(ids, id)
		}
	}
	slices.Sort(ids)
	return ids
}

func (f *fakeDB) ServeHTTP(w http.ResponseWriter, r *http.Request) {

	f.mu.Lock()
	noBackup, window, restoreStatus, restoreBody := f.noBackup, f.window, f.restoreStatus, f.restoreBody
	f.mu.Unlock()
	switch {
	case r.Method == http.MethodPost && strings.HasSuffix(r.URL.Path, "/transactions"):
		f.transaction(w, r)
	case r.Method == http.MethodGet && strings.HasSuffix(r.URL.Path, "/documents/by-key"):
		f.get(w, r)
	case r.Method == http.MethodPost && strings.HasSuffix(r.URL.Path, "/collections"):
		w.WriteHeader(http.StatusCreated)
		fmt.Fprint(w, `{}`)
	case r.Method == http.MethodGet && r.URL.Path == "/healthz":
		fmt.Fprint(w, `{"status":"ok"}`)
	case r.Method == http.MethodPut && strings.Contains(r.URL.Path, "/collections/nudges/documents/by-key"):
		var body struct {
			Why string `json:"why"`
		}
		_ = json.NewDecoder(r.Body).Decode(&body)
		f.mu.Lock()
		f.writes++
		f.coll("nudges")[r.URL.Query().Get("id")] = &fakeDoc{live: true, version: "tbv1-nudge", body: []byte(`{}`), write: f.writes}
		f.nudges = append(f.nudges, fakeNudge{why: body.Why, at: time.Now()})
		if f.stopOnNudge != "" && body.Why == f.stopOnNudge {
			f.stopped = true
		}
		f.mu.Unlock()
		fmt.Fprint(w, `{}`)
	case r.Method == http.MethodGet && strings.HasSuffix(r.URL.Path, "/collections/markers/documents"):
		var items []map[string]string
		for _, id := range f.live("markers") {
			items = append(items, map[string]string{"id": id})
		}
		_ = json.NewEncoder(w).Encode(map[string]any{"items": items, "next_cursor": nil})
	case r.Method == http.MethodPost && strings.HasSuffix(r.URL.Path, "/query"):
		f.query(w, r)
	case r.Method == http.MethodGet && strings.HasSuffix(r.URL.Path, "/point-in-time") && window != "":
		fmt.Fprint(w, window)
	case r.Method == http.MethodPost && strings.HasSuffix(r.URL.Path, "/point-in-time/restore") && restoreStatus != 0:
		w.WriteHeader(restoreStatus)
		fmt.Fprint(w, restoreBody)
	case r.Method == http.MethodGet && r.URL.Path == "/v1/backup" && noBackup:
		fmt.Fprint(w, `{"shipping":false,"files":[]}`)
	case r.Method == http.MethodGet && r.URL.Path == "/v1/backup":
		f.mu.Lock()
		if now := time.Now(); !f.stopped && f.writes > f.shippedSeq && now.Sub(f.lastPass) >= 5*time.Millisecond {
			f.lastShipped, f.lastPass, f.shippedSeq = now, now, f.writes
		}
		shipped := "null"
		if !f.lastShipped.IsZero() {
			shipped = `"` + f.lastShipped.UTC().Format(time.RFC3339Nano) + `"`
		}
		f.mu.Unlock()
		fmt.Fprintf(w, `{"shipping":true,"files":[{"file":"v1/o/p/DBID","lastShippedAt":%s,"lagSeconds":0,"pendingBytes":0,"shipping":true}]}`, shipped)
	default:
		http.Error(w, "not faked", http.StatusNotImplemented)
	}
}

func (f *fakeDB) transaction(w http.ResponseWriter, r *http.Request) {
	body, _ := io.ReadAll(r.Body)
	var req struct {
		Ops []fakeOp `json:"ops"`
	}
	if err := json.Unmarshal(body, &req); err != nil {
		http.Error(w, err.Error(), http.StatusBadRequest)
		return
	}
	f.mu.Lock()
	f.tx++
	n, faultOf := f.tx, f.fault
	f.mu.Unlock()
	var flt *fault
	if faultOf != nil {
		flt = faultOf(n)
	}
	if flt != nil && flt.reject != 0 {
		http.Error(w, `{"error":{"code":"rejected_by_fault"}}`, flt.reject)
		return
	}
	if flt != nil && flt.hold != nil {
		<-flt.hold
	}
	limit := len(req.Ops)
	if flt != nil && flt.partialApply > 0 {
		limit = flt.partialApply
	}
	status, results := f.apply(req.Ops, limit)
	if flt != nil && flt.evaluated != nil {
		flt.evaluated <- status
	}
	if status == http.StatusOK && flt != nil {
		if flt.afterCommit != nil {
			f.mu.Lock()
			flt.afterCommit()
			f.mu.Unlock()
		}
		if flt.abortAfter {
			panic(http.ErrAbortHandler)
		}
		if flt.failAfter {
			http.Error(w, "after commit", http.StatusInternalServerError)
			return
		}
	}
	if status != http.StatusOK {
		http.Error(w, `{"error":{"code":"precondition_failed"}}`, status)
		return
	}
	_ = json.NewEncoder(w).Encode(map[string]any{"offset": fmt.Sprint(n), "durability": "local_durable", "results": results})
}

func (f *fakeDB) apply(ops []fakeOp, limit int) (int, []map[string]any) {
	f.mu.Lock()
	defer f.mu.Unlock()
	for _, op := range ops {
		d := f.coll(op.Collection)[op.ID]
		switch op.Op {
		case "assert":
			if !op.Absent {
				return http.StatusBadRequest, nil
			}
			if d != nil && d.live && !f.ignoreAsserts {
				return http.StatusPreconditionFailed, nil
			}
		case "create":
			if d != nil && d.live {
				return http.StatusPreconditionFailed, nil
			}
		case "put":
			if op.IfMatch != "" && (d == nil || d.version != op.IfMatch) {
				return http.StatusPreconditionFailed, nil
			}
		case "delete":
			if d == nil || !d.live {
				return http.StatusNotFound, nil
			}
			if op.IfMatch != "" && d.version != op.IfMatch {
				return http.StatusPreconditionFailed, nil
			}
		}
	}
	for _, op := range ops {
		if op.Collection == "markers" && f.racyMarker {

			f.lastShipped, f.lastPass, f.shippedSeq = time.Now(), time.Now(), f.writes
			f.stopped = f.stopAtMarker
		}
	}
	f.writes++
	results := make([]map[string]any, 0, len(ops))
	for k, op := range ops {
		if op.Op == "assert" {
			results = append(results, map[string]any{"asserted": true})
			continue
		}
		f.seq++
		version := fmt.Sprintf("tbv1-%032x", f.seq)
		switch {
		case op.Collection != "events":
			f.coll(op.Collection)[op.ID] = &fakeDoc{live: op.Op != "delete", version: version, body: op.Document, write: f.writes}
		case k >= limit:

		case op.Op == "delete" && f.dropDelete[op.ID]:

		case op.Op == "delete":
			f.set(op.ID, &fakeDoc{version: version})
			f.applied[op.ID]++
		default:
			canonical, _ := benchclient.Canonical(op.Document)
			f.set(op.ID, &fakeDoc{live: true, version: version, body: canonical})
			f.applied[op.ID]++
		}
		results = append(results, map[string]any{"id": op.ID, "version": version})
	}
	return http.StatusOK, results
}

func (f *fakeDB) set(id string, d *fakeDoc) {
	d.write = f.writes
	if old := f.docs[id]; old != nil && old.live {
		delete(f.byAcct[old.acct], id)
	}
	if d.live {
		var doc struct {
			Acct string `json:"acct"`
		}
		_ = json.Unmarshal(d.body, &doc)
		d.acct = doc.Acct
		if f.byAcct[d.acct] == nil {
			f.byAcct[d.acct] = map[string]bool{}
		}
		f.byAcct[d.acct][id] = true
	}
	f.docs[id] = d
}

func (f *fakeDB) get(w http.ResponseWriter, r *http.Request) {
	f.mu.Lock()
	defer f.mu.Unlock()
	f.gets++
	coll, _, _ := strings.Cut(strings.TrimPrefix(r.URL.Path[strings.Index(r.URL.Path, "/collections/"):], "/collections/"), "/")
	d := f.coll(coll)[r.URL.Query().Get("id")]
	switch {
	case d == nil:
		http.Error(w, "not found", http.StatusNotFound)
	case !d.live:
		w.Header().Set("ETag", `"`+d.version+`"`)
		http.Error(w, "not found", http.StatusNotFound)
	default:
		w.Header().Set("ETag", `"`+d.version+`"`)
		fmt.Fprintf(w, `{"id":%q,"version":%q,"state":"value","document":%s}`, r.URL.Query().Get("id"), d.version, d.body)
	}
}

func (f *fakeDB) query(w http.ResponseWriter, r *http.Request) {
	var req struct {
		Filter  json.RawMessage `json:"filter"`
		Limit   int             `json:"limit"`
		Cursor  string          `json:"cursor"`
		Project []string        `json:"project"`
	}
	if err := json.NewDecoder(r.Body).Decode(&req); err != nil {
		http.Error(w, err.Error(), http.StatusBadRequest)
		return
	}
	var conds [][3]string
	var parse func(raw json.RawMessage) error
	parse = func(raw json.RawMessage) error {
		var node map[string]json.RawMessage
		if err := json.Unmarshal(raw, &node); err != nil {
			return err
		}
		for op, arg := range node {
			if op == "and" {
				var children []json.RawMessage
				if err := json.Unmarshal(arg, &children); err != nil {
					return err
				}
				for _, c := range children {
					if err := parse(c); err != nil {
						return err
					}
				}
				continue
			}
			var pv []string
			if err := json.Unmarshal(arg, &pv); err != nil || len(pv) != 2 {
				return fmt.Errorf("filter %s: %s", op, arg)
			}
			conds = append(conds, [3]string{op, pv[0], pv[1]})
		}
		return nil
	}
	if len(req.Filter) > 0 {
		if err := parse(req.Filter); err != nil {
			http.Error(w, err.Error(), http.StatusBadRequest)
			return
		}
	}
	f.mu.Lock()
	defer f.mu.Unlock()
	var ids []string
	acct := ""
	for _, c := range conds {
		if c[0] == "eq" && c[1] == "/acct" {
			acct = c[2]
		}
	}
	if acct != "" {
		for id := range f.byAcct[acct] {
			if !f.indexHides[id] {
				ids = append(ids, id)
			}
		}
	} else {
		for id, d := range f.docs {
			if d.live {
				ids = append(ids, id)
			}
		}
	}
	ids = slices.DeleteFunc(ids, func(id string) bool {
		for _, c := range conds {
			switch {
			case c[1] == "$id" && c[0] == "gte" && id < c[2], c[1] == "$id" && c[0] == "lt" && id >= c[2]:
				return true
			}
		}
		return false
	})
	slices.Sort(ids)
	out := map[string]any{"next_cursor": nil}
	if req.Cursor != "" {
		k, _ := slices.BinarySearch(ids, req.Cursor+"\x00")
		ids = ids[k:]
	}
	if len(ids) > req.Limit {
		ids = ids[:req.Limit]
		out["next_cursor"] = ids[len(ids)-1]
	}
	items := make([]json.RawMessage, 0, len(ids))
	for _, id := range ids {
		d := f.docs[id]
		doc := string(d.body)
		if slices.Equal(req.Project, []string{"/acct"}) {
			doc = fmt.Sprintf(`{"acct":%q}`, d.acct)
		}
		items = append(items, json.RawMessage(fmt.Sprintf(`{"id":%q,"version":%q,"document":%s}`, id, d.version, doc)))
	}
	out["items"] = items
	_ = json.NewEncoder(w).Encode(out)
}

func newTestRun(t *testing.T, docs, split int64) (*Run, *fakeDB) {
	t.Helper()
	db := newFakeDB()
	srv := httptest.NewUnstartedServer(db)
	var p http.Protocols
	p.SetHTTP1(true)
	p.SetUnencryptedHTTP2(true)
	srv.Config.Protocols = &p
	srv.Start()
	t.Cleanup(srv.Close)
	_, port, _ := net.SplitHostPort(srv.Listener.Addr().String())
	var portN int
	fmt.Sscan(port, &portN)
	r := &Run{Dir: t.TempDir(), ID: "test", Seed: 42, Docs: docs, LoadSplit: split, Partitions: 4, Project: "P",
		Database: "pitr", DatabaseID: "DBID", Owner: "OWNER", Collection: "events"}
	if err := r.Save(); err != nil {
		t.Fatal(err)
	}
	if err := writeJSON(filepath.Join(r.Dir, "node.json"), NodeState{InternalPort: portN, PeerSecret: strings.Repeat("a", 64)}); err != nil {
		t.Fatal(err)
	}
	return r, db
}
