package v1_test

// Shape subscription wire contract. Requests and responses come from
// testdata/shape-vectors.json and are served by local httptest servers. Where a
// case needs a state the vectors do not contain, the step edits one vector and
// the test says which edit.

import (
	"context"
	"encoding/json"
	"errors"
	"net/http"
	"net/http/httptest"
	"net/url"
	"os"
	"sync"
	"sync/atomic"
	"testing"
	"time"

	v1 "terrascale.tech/sdk/v1"
)

func TestSubscribeHeaderMismatchKeepsState(t *testing.T) {
	steps := append(subscribeSnapshot(), subscribeStep{id: "closed-commit-upsert-delete", patch: func(v *subscribeVector) {
		v.Response.Headers["TB-Shape-Next-Offset"] = "6"
	}})
	_, col := subscribeSetup(t, v1.Config{Token: testToken}, steps...)
	got := collectSubscribe(t, col, 10)
	wantYields(t, got, 3)
	if got[2].err == nil {
		t.Fatal("inconsistent headers accepted")
	}
	checkSubscribeHead(t, got[2].state, shapeHandleA, "2", false)
	checkSubscribeDoc(t, got[2].state, shapeDocA, shapeVersionA1, upsertDocument(t, loadSubscribeVector(t, "snapshot-first-page").wire(), shapeDocA))
	checkSubscribeDoc(t, got[2].state, shapeDocB, shapeVersionB1, upsertDocument(t, loadSubscribeVector(t, "snapshot-last-page").wire(), shapeDocB))
}

func TestSubscribeNonFreshNoProgressStops(t *testing.T) {
	steps := append(subscribeSnapshot(), subscribeStep{id: "closed-commit-upsert-delete", patch: func(v *subscribeVector) {
		editSubscribeBody(t, v, func(body map[string]any) {
			body["next_offset"], body["covered_offset"] = "2", "2"
			body["changes"] = []any{}
		})
		v.Response.Headers["TB-Shape-Next-Offset"] = "2"
	}})
	server, col := subscribeSetup(t, v1.Config{Token: testToken}, steps...)
	got := collectSubscribe(t, col, 10)
	wantYields(t, got, 3)
	if got[2].err == nil {
		t.Fatal("non-fresh non-advancing body accepted")
	}
	checkSubscribeHead(t, got[2].state, shapeHandleA, "2", false)
	if server.count() != 4 {
		t.Fatal("client retried a non-advancing body")
	}
}

func TestSubscribeSealedChunkCannotClaimFreshness(t *testing.T) {
	steps := append(subscribeSnapshot(), subscribeStep{id: "closed-commit-upsert-delete", patch: func(v *subscribeVector) {
		editSubscribeBody(t, v, func(body map[string]any) { body["up_to_date"] = true })
		v.Response.Headers["TB-Shape-Up-To-Date"] = "true"
	}})
	_, col := subscribeSetup(t, v1.Config{Token: testToken}, steps...)
	got := collectSubscribe(t, col, 10)
	wantYields(t, got, 3)
	if got[2].err == nil {
		t.Fatal("sealed chunk claimed freshness")
	}
	checkSubscribeHead(t, got[2].state, shapeHandleA, "2", false)
}

func TestSubscribeRepeatedBootstrapRefusalsBackOff(t *testing.T) {
	var hits atomic.Int32
	var timesMu sync.Mutex
	var times []time.Time
	server := httptest.NewServer(http.HandlerFunc(func(w http.ResponseWriter, r *http.Request) {
		if r.URL.Query().Get("offset") != "-1" || r.URL.Query().Get("handle") != "" {
			t.Error("reset bootstrap retained a handle")
		}
		hits.Add(1)
		timesMu.Lock()
		times = append(times, time.Now())
		timesMu.Unlock()
		w.Header().Set("TB-Shape-Handle", shapeHandleA)
		w.WriteHeader(http.StatusConflict)
		_, _ = w.Write([]byte(`{"error":{"code":"must_refetch","message":"not yet available"}}`))
	}))
	t.Cleanup(server.Close)
	client, err := v1.New(v1.Config{BaseURL: server.URL, Token: testToken})
	if err != nil {
		t.Fatal(err)
	}
	col := client.Database(shapeProject, "demo").Collection("notes")
	got := collectSubscribe(t, col, 3)
	wantYields(t, got, 3)
	for _, result := range got {
		if result.err != nil {
			t.Fatal(result.err)
		}
		checkSubscribeReset(t, result.state)
	}
	if hits.Load() != 3 {
		t.Fatalf("requests = %d, want 3", hits.Load())
	}
	timesMu.Lock()
	defer timesMu.Unlock()
	if times[2].Sub(times[1]) < 50*time.Millisecond {
		t.Fatal("repeated bootstrap refusals did not back off")
	}
}

const (
	shapeProject   = "01ARZ3NDEKTSV4RRFFQ69G5FAW"
	shapeDocA      = "01ARZ3NDEKTSV4RRFFQ69G5FB0"
	shapeDocB      = "01ARZ3NDEKTSV4RRFFQ69G5FB1"
	shapeHandleA   = "h1-f3171a2230109328"
	shapeHandleB   = "h1-d90dddd41bd8282d"
	shapeSnapStart = "snap.2.0"
	shapeSnapNext  = "snap.2.MDFBUlozTkRFS1RTVjRSUkZGUTY5RzVGQjA"
	shapeCursorA   = "AS7CHVJ_DeImFLxQzbY4yUUBAAAAAAAAB9AAAQAAMDkAAwABAAAwOQADAAAAAAAAA-gAAAAAAAAAAPT1kCRe1WfKHZY6Wy9LlDDHzfN4_FYu8vGVMm609kpm"
	shapeVersionA1 = "tbv1-b803db62170176ffadaa0ee88b8cb332"
	shapeVersionA2 = "tbv1-def52df94d5598d237cf1184a4f5cb00"
	shapeVersionA3 = "tbv1-b9a54642e86bd83f460ea839d3dc5e51"
	shapeVersionB1 = "tbv1-86f0b3251c928096e66782e532821fed"
	shapeJWTA      = "eyJhbGciOiJFUzI1NiJ9.eyJzdWIiOiJ4In0.c2ln"
	shapeJWTB      = "eyJhbGciOiJFUzI1NiJ9.eyJzdWIiOiJ5In0.c2ln"
)

// subscribeVector is one entry of http_vectors. wire returns the exact
// response bytes: body_utf8 when present, else the raw body.
type subscribeVector struct {
	ID      string `json:"id"`
	Request struct {
		Method  string            `json:"method"`
		Path    string            `json:"path"`
		Query   map[string]string `json:"query"`
		Headers map[string]string `json:"headers"`
	} `json:"request"`
	Response struct {
		Status  int               `json:"status"`
		Headers map[string]string `json:"headers"`
		Body    json.RawMessage   `json:"body"`
	} `json:"response"`
	BodyUTF8 *string `json:"body_utf8"`
}

func (v subscribeVector) wire() []byte {
	if v.BodyUTF8 != nil {
		return []byte(*v.BodyUTF8)
	}
	if string(v.Response.Body) == "null" {
		return nil
	}
	return v.Response.Body
}

type subscribeFile struct {
	HTTP  []subscribeVector `json:"http_vectors"`
	Apply []struct {
		ID   string          `json:"id"`
		Body json.RawMessage `json:"body"`
	} `json:"apply_vectors"`
}

func loadSubscribeFile(t *testing.T) subscribeFile {
	t.Helper()
	raw, err := os.ReadFile("testdata/shape-vectors.json")
	if err != nil {
		t.Fatal(err)
	}
	var f subscribeFile
	if err := json.Unmarshal(raw, &f); err != nil {
		t.Fatal(err)
	}
	return f
}

// loadSubscribeVector returns a fresh copy, so a step may edit it.
func loadSubscribeVector(t *testing.T, id string) subscribeVector {
	t.Helper()
	for _, v := range loadSubscribeFile(t).HTTP {
		if v.ID == id {
			return v
		}
	}
	t.Fatalf("http vector %q not found", id)
	return subscribeVector{}
}

func subscribeApplyBody(t *testing.T, id string) []byte {
	t.Helper()
	for _, v := range loadSubscribeFile(t).Apply {
		if v.ID == id {
			return v.Body
		}
	}
	t.Fatalf("apply vector %q not found", id)
	return nil
}

// subscribeWire decodes an unedited vector body for expected values.
type subscribeWire struct {
	Handle       string `json:"handle"`
	NextOffset   string `json:"next_offset"`
	UpToDate     bool   `json:"up_to_date"`
	CursorResume string `json:"cursor_resume"`
	Changes      []struct {
		Ops []struct {
			Op       string          `json:"op"`
			ID       string          `json:"id"`
			Document json.RawMessage `json:"document"`
		} `json:"ops"`
	} `json:"changes"`
}

func subscribeBodyOf(t *testing.T, id string) subscribeWire {
	t.Helper()
	var w subscribeWire
	if err := json.Unmarshal(loadSubscribeVector(t, id).wire(), &w); err != nil {
		t.Fatal(err)
	}
	return w
}

// upsertDocument returns the exact document bytes of the upsert for id.
func upsertDocument(t *testing.T, body []byte, id string) json.RawMessage {
	t.Helper()
	var w subscribeWire
	if err := json.Unmarshal(body, &w); err != nil {
		t.Fatal(err)
	}
	for _, c := range w.Changes {
		for _, op := range c.Ops {
			if op.Op == "upsert" && op.ID == id {
				return op.Document
			}
		}
	}
	t.Fatalf("no upsert for %s", id)
	return nil
}

// subscribeStep serves one vector. repeat keeps the step for later requests
// (capacity retries). block holds the request until the client disconnects.
type subscribeStep struct {
	id     string
	patch  func(*subscribeVector)
	token  string
	repeat bool
	block  bool
}

type subscribeServer struct {
	t       *testing.T
	mu      sync.Mutex
	steps   []subscribeStep
	next    int
	hits    int
	arrived chan struct{}
}

func (s *subscribeServer) count() int {
	s.mu.Lock()
	defer s.mu.Unlock()
	return s.hits
}

func (s *subscribeServer) ServeHTTP(w http.ResponseWriter, r *http.Request) {
	s.mu.Lock()
	s.hits++
	if s.next >= len(s.steps) {
		s.mu.Unlock()
		s.t.Errorf("unexpected request %s %s", r.Method, r.URL.RequestURI())
		http.Error(w, "unexpected request", http.StatusInternalServerError)
		return
	}
	step := s.steps[s.next]
	if !step.repeat {
		s.next++
	}
	s.mu.Unlock()
	v := loadSubscribeVector(s.t, step.id)
	if step.patch != nil {
		step.patch(&v)
	}
	s.check(r, v, step.token)
	if step.block || step.repeat {
		s.arrived <- struct{}{}
	}
	if step.block {
		<-r.Context().Done()
		return
	}
	for k, val := range v.Response.Headers {
		w.Header().Set(k, val)
	}
	w.WriteHeader(v.Response.Status)
	_, _ = w.Write(v.wire())
}

// check compares the request with the vector. Every GET carries the bearer
// token. Shape resume headers must be present exactly when the vector has them.
func (s *subscribeServer) check(r *http.Request, v subscribeVector, token string) {
	if r.Method != v.Request.Method {
		s.t.Errorf("%s: method %s, want %s", v.ID, r.Method, v.Request.Method)
	}
	if r.URL.Path != v.Request.Path {
		s.t.Errorf("%s: path %s, want %s", v.ID, r.URL.Path, v.Request.Path)
	}
	want := url.Values{}
	for k, val := range v.Request.Query {
		want.Set(k, val)
	}
	if got := r.URL.Query().Encode(); got != want.Encode() {
		s.t.Errorf("%s: query %s, want %s", v.ID, got, want.Encode())
	}
	for _, name := range []string{"TB-Shape-Resume", "If-None-Match"} {
		if got := r.Header.Get(name); got != v.Request.Headers[name] {
			s.t.Errorf("%s: header %s = %q, want %q", v.ID, name, got, v.Request.Headers[name])
		}
	}
	if token == "" {
		token = testToken
	}
	if got := r.Header.Get("Authorization"); got != "Bearer "+token {
		s.t.Errorf("%s: Authorization = %q, want bearer %s", v.ID, got, token)
	}
}

func subscribeSetup(t *testing.T, cfg v1.Config, steps ...subscribeStep) (*subscribeServer, *v1.Collection) {
	t.Helper()
	s := &subscribeServer{t: t, steps: steps, arrived: make(chan struct{}, 16)}
	srv := httptest.NewServer(s)
	t.Cleanup(srv.Close)
	cfg.BaseURL = srv.URL
	if cfg.TokenSource == nil && cfg.Token == "" {
		cfg.Token = testToken
	}
	c, err := v1.New(cfg)
	if err != nil {
		t.Fatal(err)
	}
	return s, c.Database(shapeProject, "demo").Collection("notes")
}

// subscribeSnapshot is the bootstrap redirect and both snapshot pages.
func subscribeSnapshot() []subscribeStep {
	return []subscribeStep{{id: "bootstrap"}, {id: "snapshot-first-page"}, {id: "snapshot-last-page"}}
}

type subscribeYield struct {
	state v1.ShapeState
	err   error
}

// collectSubscribe takes up to n yields, then breaks the caller loop.
func collectSubscribe(t *testing.T, col *v1.Collection, n int) []subscribeYield {
	t.Helper()
	var got []subscribeYield
	for st, err := range col.Subscribe(context.Background(), v1.ShapeOptions{}) {
		got = append(got, subscribeYield{st, err})
		if len(got) == n {
			break
		}
	}
	return got
}

func wantYields(t *testing.T, got []subscribeYield, n int) {
	t.Helper()
	if len(got) != n {
		t.Fatalf("got %d yields, want %d", len(got), n)
	}
	for i, y := range got[:n-1] {
		if y.err != nil {
			t.Fatalf("yield %d: unexpected error %v", i, y.err)
		}
	}
}

func checkSubscribeHead(t *testing.T, st v1.ShapeState, handle, offset string, upToDate bool) {
	t.Helper()
	if st.Handle != handle || st.Offset != offset || st.UpToDate != upToDate {
		t.Fatalf("head = (%q, %q, up=%v), want (%q, %q, up=%v)", st.Handle, st.Offset, st.UpToDate, handle, offset, upToDate)
	}
}

func checkSubscribeDoc(t *testing.T, st v1.ShapeState, id, version string, doc json.RawMessage) {
	t.Helper()
	d, ok := st.Documents[id]
	if !ok {
		t.Fatalf("document %s missing", id)
	}
	if d.Version != version || d.State != v1.StateValue || string(d.Document) != string(doc) {
		t.Fatalf("document %s = (%q, %v, %s), want (%q, value, %s)", id, d.Version, d.State, d.Document, version, doc)
	}
}

func checkSubscribeAbsent(t *testing.T, st v1.ShapeState, id string) {
	t.Helper()
	if _, ok := st.Documents[id]; ok {
		t.Fatalf("document %s present, want absent", id)
	}
}

func checkSubscribeReset(t *testing.T, st v1.ShapeState) {
	t.Helper()
	if st.Handle != "" || st.Offset != "-1" || len(st.Documents) != 0 || st.CursorResume != "" {
		t.Fatalf("state after must_refetch = %+v, want empty bootstrap state", st)
	}
}

// subscribeError rewrites a vector to a synthetic error answer.
func subscribeError(status int, code string) func(*subscribeVector) {
	return func(v *subscribeVector) {
		v.Response.Status = status
		v.Response.Headers = map[string]string{"Cache-Control": "no-store"}
		body := `{"error":{"code":"` + code + `","message":"synthetic ` + code + `"}}`
		v.BodyUTF8 = &body
	}
}

// subscribeRedirect rewrites a vector to a 307 with the given Location.
func subscribeRedirect(location string) func(*subscribeVector) {
	return func(v *subscribeVector) {
		v.Response.Status = http.StatusTemporaryRedirect
		v.Response.Headers = map[string]string{"Location": location, "TB-Shape-Handle": shapeHandleA}
	}
}

// editSubscribeBody re-marshals the body. Object key order changes, so tests
// only compare document bytes on unedited bodies.
func editSubscribeBody(t *testing.T, v *subscribeVector, edit func(map[string]any)) {
	var m map[string]any
	if err := json.Unmarshal(v.wire(), &m); err != nil {
		t.Errorf("%s: decode body to edit: %v", v.ID, err)
		return
	}
	edit(m)
	b, err := json.Marshal(m)
	if err != nil {
		t.Errorf("%s: encode edited body: %v", v.ID, err)
		return
	}
	body := string(b)
	v.BodyUTF8 = &body
}

// A non-live read at the head is fresh and empty; it has no live bucket.
func subscribeAtHead() subscribeStep {
	return subscribeStep{id: "live-timeout", patch: func(v *subscribeVector) {
		delete(v.Request.Query, "live")
		delete(v.Request.Query, "cursor")
		delete(v.Response.Headers, "TB-Shape-Cursor")
	}}
}

func TestSubscribeSnapshotPagesStagedBeforeCursorResume(t *testing.T) {
	s, col := subscribeSetup(t, v1.Config{Token: testToken}, subscribeSnapshot()...)
	got := collectSubscribe(t, col, 2)
	wantYields(t, got, 2)
	first, last := got[0].state, got[1].state

	checkSubscribeHead(t, first, shapeHandleA, shapeSnapNext, false)
	if first.CursorResume != "" {
		t.Errorf("cursor resume %q usable mid-snapshot, want empty", first.CursorResume)
	}
	checkSubscribeDoc(t, first, shapeDocA, shapeVersionA1, upsertDocument(t, loadSubscribeVector(t, "snapshot-first-page").wire(), shapeDocA))
	checkSubscribeHead(t, last, shapeHandleA, "2", false)
	if want := subscribeBodyOf(t, "snapshot-last-page").CursorResume; last.CursorResume != want {
		t.Errorf("cursor resume = %q, want %q", last.CursorResume, want)
	}
	checkSubscribeDoc(t, last, shapeDocA, shapeVersionA1, upsertDocument(t, loadSubscribeVector(t, "snapshot-first-page").wire(), shapeDocA))
	checkSubscribeDoc(t, last, shapeDocB, shapeVersionB1, upsertDocument(t, loadSubscribeVector(t, "snapshot-last-page").wire(), shapeDocB))
	if s.count() != 3 {
		t.Errorf("requests = %d, want 3 (redirect and two pages)", s.count())
	}
}

func TestSubscribeClosedCommitUpsertsAndDeletesAtomically(t *testing.T) {
	steps := append(subscribeSnapshot(), subscribeStep{id: "closed-commit-upsert-delete"})
	_, col := subscribeSetup(t, v1.Config{Token: testToken}, steps...)
	got := collectSubscribe(t, col, 3)
	wantYields(t, got, 3)
	st := got[2].state
	checkSubscribeHead(t, st, shapeHandleA, "4", false)
	checkSubscribeDoc(t, st, shapeDocA, shapeVersionA2, upsertDocument(t, loadSubscribeVector(t, "closed-commit-upsert-delete").wire(), shapeDocA))
	checkSubscribeAbsent(t, st, shapeDocB)
	if want := subscribeBodyOf(t, "closed-commit-upsert-delete").CursorResume; st.CursorResume != want {
		t.Errorf("cursor resume = %q, want %q", st.CursorResume, want)
	}
}

func TestSubscribeLivePollCarriesCursorAndKeepsDocuments(t *testing.T) {
	steps := append(subscribeSnapshot(),
		subscribeStep{id: "closed-commit-upsert-delete"},
		subscribeAtHead(),
		subscribeStep{id: "live-timeout", patch: func(v *subscribeVector) { delete(v.Request.Query, "cursor") }},
		subscribeStep{id: "live-timeout", patch: func(v *subscribeVector) {
			v.Request.Query["cursor"] = "51"
			v.Response.Headers["TB-Shape-Cursor"] = "52"
		}},
	)
	_, col := subscribeSetup(t, v1.Config{Token: testToken}, steps...)
	got := collectSubscribe(t, col, 6)
	wantYields(t, got, 6)
	caught, idle := got[3].state, got[5].state
	checkSubscribeHead(t, caught, shapeHandleA, "4", true)
	checkSubscribeHead(t, idle, shapeHandleA, "4", true)
	// Fresh reads and empty live polls retain the exact sealed document bytes.
	if idle.Documents[shapeDocA].Version != shapeVersionA2 || string(idle.Documents[shapeDocA].Document) != string(caught.Documents[shapeDocA].Document) {
		t.Errorf("live poll changed document %s", shapeDocA)
	}
	if idle.CursorResume != caught.CursorResume {
		t.Errorf("empty live poll changed cursor resume")
	}
}

func TestSubscribeLiveCommitAdvancesAndApplies(t *testing.T) {
	steps := append(subscribeSnapshot(),
		subscribeStep{id: "closed-commit-upsert-delete"},
		subscribeAtHead(),
		subscribeStep{id: "closed-commit-upsert-delete", patch: func(v *subscribeVector) {
			v.Request.Query["offset"], v.Request.Query["live"] = "4", "true"
			v.Response.Headers["TB-Shape-Next-Offset"] = "6"
			v.Response.Headers["TB-Shape-Up-To-Date"], v.Response.Headers["TB-Shape-Cursor"] = "true", "51"
			editSubscribeBody(t, v, func(body map[string]any) {
				body["offset"], body["next_offset"], body["covered_offset"], body["values_at"] = "4", "6", "6", "6"
				body["up_to_date"] = true
				change := body["changes"].([]any)[0].(map[string]any)
				change["offset"] = "6"
				upsert := change["ops"].([]any)[0].(map[string]any)
				upsert["version"], upsert["document"] = shapeVersionA3, map[string]any{"title": "Live", "read": true}
			})
		}},
	)
	_, col := subscribeSetup(t, v1.Config{Token: testToken}, steps...)
	got := collectSubscribe(t, col, 5)
	wantYields(t, got, 5)
	state := got[4].state
	checkSubscribeHead(t, state, shapeHandleA, "6", true)
	checkSubscribeAbsent(t, state, shapeDocB)
	if state.Documents[shapeDocA].Version != shapeVersionA3 {
		t.Fatal("live upsert version was not applied")
	}
	doc := state.Documents[shapeDocA]
	var body struct {
		Title string
		Read  bool
	}
	if err := doc.Decode(&body); err != nil || body.Title != "Live" || !body.Read {
		t.Fatalf("live document = %+v, err %v", body, err)
	}
}

func TestSubscribeBreakStopsWithoutExtraRequest(t *testing.T) {
	s, col := subscribeSetup(t, v1.Config{Token: testToken}, subscribeStep{id: "bootstrap"}, subscribeStep{id: "snapshot-first-page"})
	got := collectSubscribe(t, col, 1)
	wantYields(t, got, 1)
	if n := s.count(); n != 2 {
		t.Fatalf("requests = %d after break, want 2", n)
	}
}

func TestSubscribeCancelInLongPollReturnsContextError(t *testing.T) {
	steps := append(subscribeSnapshot(),
		subscribeStep{id: "closed-commit-upsert-delete"},
		subscribeAtHead(),
		subscribeStep{id: "live-timeout", block: true, patch: func(v *subscribeVector) { delete(v.Request.Query, "cursor") }},
	)
	s, col := subscribeSetup(t, v1.Config{Token: testToken}, steps...)
	ctx, cancel := context.WithCancel(context.Background())
	defer cancel()
	go func() {
		<-s.arrived
		cancel()
	}()
	var got []subscribeYield
	for st, err := range col.Subscribe(ctx, v1.ShapeOptions{}) {
		got = append(got, subscribeYield{st, err})
	}
	if len(got) != 5 {
		t.Fatalf("got %d yields, want 5 (four states and the cancel)", len(got))
	}
	wantYields(t, got, 5)
	last := got[4]
	if !errors.Is(last.err, context.Canceled) {
		t.Fatalf("final error = %v, want context.Canceled", last.err)
	}
	checkSubscribeHead(t, last.state, shapeHandleA, "4", true)
}

func TestSubscribeCancelDuringCapacityBackoffKeepsCheckpoint(t *testing.T) {
	steps := append(subscribeSnapshot(), subscribeStep{id: "oversize-materialized-commit", repeat: true})
	s, col := subscribeSetup(t, v1.Config{Token: testToken}, steps...)
	ctx, cancel := context.WithCancel(context.Background())
	defer cancel()
	go func() {
		<-s.arrived
		<-s.arrived
		cancel()
	}()
	var got []subscribeYield
	for st, err := range col.Subscribe(ctx, v1.ShapeOptions{}) {
		got = append(got, subscribeYield{st, err})
	}
	if len(got) != 3 {
		t.Fatalf("got %d yields, want 3 (two states and the cancel)", len(got))
	}
	if !errors.Is(got[2].err, context.Canceled) {
		t.Fatalf("final error = %v, want context.Canceled", got[2].err)
	}
	checkSubscribeHead(t, got[2].state, shapeHandleA, "2", false)
}

func TestSubscribeRetriesCapacityFromSameCheckpoint(t *testing.T) {
	for _, tc := range []struct {
		name  string
		retry subscribeStep
	}{
		{"shape_too_large", subscribeStep{id: "oversize-materialized-commit"}},
		{"shape_busy", subscribeStep{id: "closed-commit-upsert-delete", patch: subscribeError(429, "shape_busy")}},
		{"unavailable", subscribeStep{id: "closed-commit-upsert-delete", patch: subscribeError(503, "unavailable")}},
	} {
		t.Run(tc.name, func(t *testing.T) {
			steps := append(subscribeSnapshot(), tc.retry, subscribeStep{id: "closed-commit-upsert-delete"})
			_, col := subscribeSetup(t, v1.Config{Token: testToken}, steps...)
			got := collectSubscribe(t, col, 3)
			wantYields(t, got, 3)
			checkSubscribeHead(t, got[2].state, shapeHandleA, "4", false)
			checkSubscribeDoc(t, got[2].state, shapeDocA, shapeVersionA2, upsertDocument(t, loadSubscribeVector(t, "closed-commit-upsert-delete").wire(), shapeDocA))
		})
	}
}

func TestSubscribeTerminalErrorsKeepLastValidState(t *testing.T) {
	for _, tc := range []struct {
		name   string
		status int
		code   string
	}{
		{"not found", 404, "not_found"},
		{"unauthorized with static token", 401, "unauthorized"},
	} {
		t.Run(tc.name, func(t *testing.T) {
			steps := append(subscribeSnapshot(), subscribeStep{id: "closed-commit-upsert-delete", patch: subscribeError(tc.status, tc.code)})
			s, col := subscribeSetup(t, v1.Config{Token: testToken}, steps...)
			got := collectSubscribe(t, col, 10)
			wantYields(t, got, 3)
			var apiErr *v1.APIError
			if !errors.As(got[2].err, &apiErr) || apiErr.Status != tc.status {
				t.Fatalf("error = %v, want APIError status %d", got[2].err, tc.status)
			}
			checkSubscribeHead(t, got[2].state, shapeHandleA, "2", false)
			if n := s.count(); n != 4 {
				t.Errorf("requests = %d, want 4 (no retry)", n)
			}
		})
	}
}

func TestSubscribeResumeRejected400IsTerminal(t *testing.T) {
	steps := append(subscribeSnapshot(),
		subscribeStep{id: "changed-machine-continuation-tries-resume-once"},
		subscribeStep{id: "resume-malformed-token", patch: func(v *subscribeVector) { v.Request.Query["cursor"] = shapeCursorA }},
	)
	s, col := subscribeSetup(t, v1.Config{Token: testToken}, steps...)
	got := collectSubscribe(t, col, 10)
	wantYields(t, got, 3)
	var apiErr *v1.APIError
	if !errors.As(got[2].err, &apiErr) || apiErr.Status != http.StatusBadRequest {
		t.Fatalf("error = %v, want APIError status 400", got[2].err)
	}
	checkSubscribeHead(t, got[2].state, shapeHandleA, "2", false)
	if n := s.count(); n != 5 {
		t.Errorf("requests = %d, want 5", n)
	}
}

func TestSubscribeTokenRefreshRetriesOnceWithNewToken(t *testing.T) {
	src := &subscribeTokens{tokens: []string{shapeJWTA, shapeJWTB}}
	steps := subscribeSnapshot()
	for i := range steps {
		steps[i].token = shapeJWTA
	}
	steps = append(steps,
		subscribeStep{id: "closed-commit-upsert-delete", token: shapeJWTA, patch: subscribeError(401, "unauthorized")},
		subscribeStep{id: "closed-commit-upsert-delete", token: shapeJWTB},
	)
	_, col := subscribeSetup(t, v1.Config{TokenSource: src}, steps...)
	got := collectSubscribe(t, col, 3)
	wantYields(t, got, 3)
	checkSubscribeHead(t, got[2].state, shapeHandleA, "4", false)
	if src.idx != 1 {
		t.Errorf("token invalidations = %d, want 1", src.idx)
	}
}

// subscribeTokens hands out tokens in order; Invalidate moves to the next.
type subscribeTokens struct {
	tokens []string
	idx    int
}

func (s *subscribeTokens) Token(context.Context) (string, error) {
	return s.tokens[s.idx], nil
}

func (s *subscribeTokens) Invalidate(string) {
	if s.idx < len(s.tokens)-1 {
		s.idx++
	}
}

func TestSubscribeRedirectStaysOnCollectionAndUses307Only(t *testing.T) {
	var foreignHits atomic.Int32
	foreign := httptest.NewServer(http.HandlerFunc(func(http.ResponseWriter, *http.Request) { foreignHits.Add(1) }))
	t.Cleanup(foreign.Close)
	base := "/v1/projects/" + shapeProject + "/databases/demo/collections/notes/shape"
	sameCollection := base + "?handle=" + shapeHandleA + "&offset=" + shapeSnapStart

	for _, tc := range []struct {
		name     string
		location string
	}{
		{"foreign origin", foreign.URL + sameCollection},
		{"other collection", "/v1/projects/" + shapeProject + "/databases/demo/collections/other/shape?handle=" + shapeHandleA + "&offset=" + shapeSnapStart},
		{"extra query key", sameCollection + "&cursor=" + shapeCursorA},
		{"bootstrap skips first page", base + "?handle=" + shapeHandleA + "&offset=" + shapeSnapNext},
	} {
		t.Run(tc.name, func(t *testing.T) {
			steps := []subscribeStep{{id: "bootstrap", patch: subscribeRedirect(tc.location)}}
			s, col := subscribeSetup(t, v1.Config{Token: testToken}, steps...)
			got := collectSubscribe(t, col, 10)
			wantYields(t, got, 1)
			if got[0].err == nil {
				t.Fatal("redirect accepted, want error")
			}
			if n := s.count(); n != 1 {
				t.Errorf("requests = %d, want 1", n)
			}
			if n := foreignHits.Load(); n != 0 {
				t.Errorf("foreign origin received %d requests, want 0", n)
			}
		})
	}

	t.Run("second 307 is refused", func(t *testing.T) {
		steps := []subscribeStep{
			{id: "bootstrap"},
			{id: "snapshot-first-page", patch: subscribeRedirect(sameCollection)},
		}
		s, col := subscribeSetup(t, v1.Config{Token: testToken}, steps...)
		got := collectSubscribe(t, col, 10)
		wantYields(t, got, 1)
		if got[0].err == nil {
			t.Fatal("second redirect accepted, want error")
		}
		if n := s.count(); n != 2 {
			t.Errorf("requests = %d, want 2", n)
		}
	})
}

// Resume after a changed handle: one cursor attempt, then the redirect target.
func TestSubscribeResumeOnceWithCursorQuery(t *testing.T) {
	for _, redirect := range []string{"proven-cursor-resume-query", "resume-uncovered-row-above-sealed-head"} {
		t.Run(redirect, func(t *testing.T) {
			cross := subscribeApplyBody(t, "cross-lineage-repeat-has-newer-materialized-value")
			steps := append(subscribeSnapshot(),
				subscribeStep{id: "changed-machine-continuation-tries-resume-once"},
				subscribeStep{id: redirect},
				subscribeStep{id: "resume-header-preserved-on-followup", patch: func(v *subscribeVector) {
					// Query resume: the follow-up carries no resume header.
					delete(v.Request.Headers, "TB-Shape-Resume")
					body := string(cross)
					v.BodyUTF8 = &body
				}},
			)
			_, col := subscribeSetup(t, v1.Config{Token: testToken}, steps...)
			got := collectSubscribe(t, col, 3)
			wantYields(t, got, 3)
			st := got[2].state
			checkSubscribeHead(t, st, shapeHandleB, "4", false)
			// Repeat of commit A with a newer value is applied, not skipped.
			checkSubscribeDoc(t, st, shapeDocA, shapeVersionA3, upsertDocument(t, cross, shapeDocA))
			checkSubscribeDoc(t, st, shapeDocB, shapeVersionB1, upsertDocument(t, loadSubscribeVector(t, "snapshot-last-page").wire(), shapeDocB))
		})
	}
}

// Long resume tokens stay intact on the authorized same-origin follow-up.
func TestSubscribeResumeLongTokenHeaderPreservedOnFollowup(t *testing.T) {
	long := loadSubscribeVector(t, "large-resume-token-uses-header").Request.Headers["TB-Shape-Resume"]
	steps := []subscribeStep{
		{id: "bootstrap"},
		{id: "snapshot-first-page"},
		{id: "snapshot-last-page", patch: func(v *subscribeVector) {
			editSubscribeBody(t, v, func(m map[string]any) { m["cursor_resume"] = long })
		}},
		{id: "changed-machine-continuation-tries-resume-once"},
		{id: "large-resume-token-uses-header"},
		// These independent vectors describe different resume targets. Adapt this
		// follow-up to the long-token vector's target and check refusal there.
		{id: "resume-header-preserved-on-followup", patch: func(v *subscribeVector) {
			v.Request.Query["offset"] = "4"
			v.Request.Headers["TB-Shape-Resume"] = long
			v.Response.Status = http.StatusConflict
			v.Response.Headers = map[string]string{"Cache-Control": "no-store", "TB-Shape-Handle": shapeHandleB}
			body := `{"error":{"code":"must_refetch","message":"stop after header check","handle":"` + shapeHandleB + `"}}`
			v.BodyUTF8 = &body
		}},
	}
	_, col := subscribeSetup(t, v1.Config{Token: testToken}, steps...)
	got := collectSubscribe(t, col, 3)
	wantYields(t, got, 3)
	checkSubscribeReset(t, got[2].state)
}

// A 409 before the snapshot is complete has no cursor, so it never resumes.
func TestSubscribeInterruptedSnapshotRestartsWithoutResume(t *testing.T) {
	steps := []subscribeStep{
		{id: "bootstrap"},
		{id: "snapshot-first-page"},
		{id: "snapshot-last-page", patch: func(v *subscribeVector) {
			v.Response.Status = http.StatusConflict
			v.Response.Headers["TB-Shape-Handle"] = shapeHandleB
			body := `{"error":{"code":"must_refetch","message":"handle belongs to another serving lineage","handle":"` + shapeHandleB + `"}}`
			v.BodyUTF8 = &body
		}},
		{id: "bootstrap"},
		{id: "snapshot-first-page"},
	}
	_, col := subscribeSetup(t, v1.Config{Token: testToken}, steps...)
	got := collectSubscribe(t, col, 3)
	wantYields(t, got, 3)
	checkSubscribeReset(t, got[1].state)
	checkSubscribeHead(t, got[2].state, shapeHandleA, shapeSnapNext, false)
	checkSubscribeDoc(t, got[2].state, shapeDocA, shapeVersionA1, upsertDocument(t, loadSubscribeVector(t, "snapshot-first-page").wire(), shapeDocA))
}

// The resume follow-up answers 409 again: reset, bootstrap, and no second resume.
func TestSubscribeResumeFollowupRefetchResetsWithoutSecondAttempt(t *testing.T) {
	steps := append(subscribeSnapshot(),
		subscribeStep{id: "changed-machine-continuation-tries-resume-once"},
		subscribeStep{id: "proven-cursor-resume-query"},
		subscribeStep{id: "resume-followup-second-machine-refetch-does-not-loop", patch: func(v *subscribeVector) {
			delete(v.Request.Headers, "TB-Shape-Resume")
		}},
		subscribeStep{id: "bootstrap"},
		subscribeStep{id: "snapshot-first-page"},
	)
	s, col := subscribeSetup(t, v1.Config{Token: testToken}, steps...)
	got := collectSubscribe(t, col, 4)
	wantYields(t, got, 4)
	checkSubscribeReset(t, got[2].state)
	checkSubscribeHead(t, got[3].state, shapeHandleA, shapeSnapNext, false)
	if n := s.count(); n != 8 {
		t.Errorf("requests = %d, want 8", n)
	}
}

// Resume refusals reset and bootstrap. The request token is the snapshot's own
// cursor, because the vector token belongs to another lineage.
func TestSubscribeRefusedResumeResetsAndBootstraps(t *testing.T) {
	for _, id := range []string{
		"resume-unproven-prefix",
		"resume-expired",
		"resume-target-below-retention-floor",
		"resume-other-collection",
		"resume-other-filter",
		"resume-other-generation",
	} {
		t.Run(id, func(t *testing.T) {
			steps := append(subscribeSnapshot(),
				subscribeStep{id: "changed-machine-continuation-tries-resume-once"},
				subscribeStep{id: id, patch: func(v *subscribeVector) { v.Request.Query["cursor"] = shapeCursorA }},
				subscribeStep{id: "bootstrap"},
				subscribeStep{id: "snapshot-first-page"},
			)
			_, col := subscribeSetup(t, v1.Config{Token: testToken}, steps...)
			got := collectSubscribe(t, col, 4)
			wantYields(t, got, 4)
			checkSubscribeReset(t, got[2].state)
			checkSubscribeHead(t, got[3].state, shapeHandleA, shapeSnapNext, false)
		})
	}
}

// Stale or out-of-range 409s with the same handle reset to a bootstrap.
func TestSubscribeSameHandleRefetchResetsState(t *testing.T) {
	for _, id := range []string{"stale-handle", "expired-offset", "future-offset", "snapshot-page-missing"} {
		t.Run(id, func(t *testing.T) {
			steps := append(subscribeSnapshot(),
				subscribeStep{id: id, patch: func(v *subscribeVector) {
					v.Request.Query = map[string]string{"handle": shapeHandleA, "offset": "2"}
				}},
				subscribeStep{id: "bootstrap"},
				subscribeStep{id: "snapshot-first-page"},
			)
			_, col := subscribeSetup(t, v1.Config{Token: testToken}, steps...)
			got := collectSubscribe(t, col, 4)
			wantYields(t, got, 4)
			checkSubscribeReset(t, got[2].state)
			checkSubscribeHead(t, got[3].state, shapeHandleA, shapeSnapNext, false)
		})
	}
}

// Malformed success yields no partial state, and earlier yields are untouched.
func TestSubscribeRejectsMalformedCommitWithoutPartialState(t *testing.T) {
	for _, tc := range []struct {
		name  string
		patch func(*subscribeVector)
	}{
		{"handle header disagrees", func(v *subscribeVector) { v.Response.Headers["TB-Shape-Handle"] = shapeHandleB }},
		{"next offset header disagrees", func(v *subscribeVector) { v.Response.Headers["TB-Shape-Next-Offset"] = "5" }},
		{"unknown op after valid upsert", func(t *testing.T) func(*subscribeVector) {
			return func(v *subscribeVector) {
				editSubscribeBody(t, v, func(m map[string]any) {
					ch := m["changes"].([]any)[0].(map[string]any)
					ch["ops"] = append(ch["ops"].([]any), map[string]any{"op": "frob", "id": shapeDocB})
				})
			}
		}(t)},
		{"truncated body", func(v *subscribeVector) {
			body := `{"shape_id":`
			v.BodyUTF8 = &body
		}},
	} {
		t.Run(tc.name, func(t *testing.T) {
			steps := append(subscribeSnapshot(), subscribeStep{id: "closed-commit-upsert-delete", patch: tc.patch})
			_, col := subscribeSetup(t, v1.Config{Token: testToken}, steps...)
			got := collectSubscribe(t, col, 10)
			wantYields(t, got, 3)
			if got[2].err == nil {
				t.Fatal("malformed commit accepted, want error")
			}
			checkSubscribeHead(t, got[2].state, shapeHandleA, "2", false)
			checkSubscribeDoc(t, got[2].state, shapeDocA, shapeVersionA1, upsertDocument(t, loadSubscribeVector(t, "snapshot-first-page").wire(), shapeDocA))
			checkSubscribeDoc(t, got[2].state, shapeDocB, shapeVersionB1, upsertDocument(t, loadSubscribeVector(t, "snapshot-last-page").wire(), shapeDocB))
			checkSubscribeDoc(t, got[1].state, shapeDocA, shapeVersionA1, upsertDocument(t, loadSubscribeVector(t, "snapshot-first-page").wire(), shapeDocA))
		})
	}
}

// Yielded state belongs to the caller: mutating it cannot change what follows.
func TestSubscribeYieldedStateIsCallerOwned(t *testing.T) {
	_, col := subscribeSetup(t, v1.Config{Token: testToken}, subscribeSnapshot()...)
	n := 0
	for st, err := range col.Subscribe(context.Background(), v1.ShapeOptions{}) {
		if err != nil {
			t.Fatal(err)
		}
		n++
		if n == 1 {
			delete(st.Documents, shapeDocA)
			st.Documents["junk"] = v1.Document{ID: "junk"}
			continue
		}
		checkSubscribeDoc(t, st, shapeDocA, shapeVersionA1, upsertDocument(t, loadSubscribeVector(t, "snapshot-first-page").wire(), shapeDocA))
		if _, ok := st.Documents["junk"]; ok {
			t.Fatal("caller mutation leaked into the next state")
		}
		break
	}
	if n != 2 {
		t.Fatalf("got %d yields, want 2", n)
	}
}
