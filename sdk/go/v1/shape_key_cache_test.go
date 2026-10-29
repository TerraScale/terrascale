package v1

import (
	"context"
	"errors"
	"io"
	"net/http"
	"net/http/httptest"
	"strings"
	"sync"
	"sync/atomic"
	"testing"
	"time"
)

// shapeKeyServer answers key and shape routes from shapeKeyBody. Tests change its
// fields through with, so handler goroutines never race with the test goroutine.
type shapeKeyServer struct {
	mu          sync.Mutex
	hits        int
	refuse      map[string]bool // epochs answered with a key-route must_refetch
	shapeRefuse bool            // every shape request answers must_refetch
	gate        string          // epoch whose requests block until release is closed
	arrived     chan struct{}
	release     chan struct{}
}

func (s *shapeKeyServer) with(f func()) {
	s.mu.Lock()
	defer s.mu.Unlock()
	f()
}

func (s *shapeKeyServer) count() int {
	s.mu.Lock()
	defer s.mu.Unlock()
	return s.hits
}

func (s *shapeKeyServer) ServeHTTP(w http.ResponseWriter, r *http.Request) {
	epoch := r.URL.Query().Get("epoch")
	handle := r.URL.Query().Get("handle")
	s.mu.Lock()
	s.hits++
	gate, refuse, shapeRefuse := s.gate, s.refuse[epoch], s.shapeRefuse
	s.mu.Unlock()
	w.Header().Set("Cache-Control", "no-store")
	if strings.HasSuffix(r.URL.Path, "/shape") && shapeRefuse {
		http.Error(w, `{"error":{"code":"must_refetch","message":"shape lineage changed","handle":"`+handle+`"}}`, http.StatusConflict)
		return
	}
	if gate != "" && epoch == gate {
		s.arrived <- struct{}{}
		<-s.release
	}
	if refuse {
		http.Error(w, `{"error":{"code":"must_refetch","message":"epoch and key pair is not retained","handle":"`+handle+`"}}`, http.StatusConflict)
		return
	}
	io.WriteString(w, shapeKeyBody(handle, epoch, r.URL.Query().Get("key_id"), shapeKeyValue))
}

// newShapeKeyServer returns a server and the notes collection on its client.
// The fake clock starts inside epoch 100 (wall window 100).
func newShapeKeyServer(t *testing.T) (*shapeKeyServer, *Collection) {
	t.Helper()
	s := &shapeKeyServer{arrived: make(chan struct{}, 16), release: make(chan struct{}), refuse: map[string]bool{}}
	srv := httptest.NewServer(s)
	t.Cleanup(srv.Close)
	t.Cleanup(func() {
		select {
		case <-s.release:
		default:
			close(s.release)
		}
	})
	col := shapeKeyCollection(t, srv.URL)
	shapeKeyClock(col, 100*600+1)
	return s, col
}

// shapeKeyClock pins the wall clock used for epoch eviction.
func shapeKeyClock(col *Collection, unix int64) {
	col.db.c.shapeKeys.now = func() time.Time { return time.Unix(unix, 0) }
}

// shapeKeyFetch fetches epoch 100 for the default handle and key ID.
func shapeKeyFetch(ctx context.Context, col *Collection, epoch uint64) (*ShapeKey, error) {
	return col.ShapeKey(ctx, shapeKeyHandle, epoch, shapeKeyID)
}

func TestShapeKeyCacheSharesKeyAcrossWrappers(t *testing.T) {
	srv, col := newShapeKeyServer(t)
	first, err := shapeKeyFetch(t.Context(), col, 100)
	if err != nil {
		t.Fatal(err)
	}
	// A fresh Collection wrapper on the same client must hit the same cache.
	again := col.db.c.Database(shapeKeyProject, "demo").Collection("notes")
	second, err := shapeKeyFetch(t.Context(), again, 100)
	if err != nil {
		t.Fatal(err)
	}
	if srv.count() != 1 {
		t.Fatalf("requests = %d, want 1", srv.count())
	}
	if first == second {
		t.Fatal("cache returned the same pointer; callers must get copies")
	}
	// Changing a returned copy must not corrupt the cached metadata.
	first.Handle, first.Epoch, first.KeyID = "h1-0000000000000000", 5, "x"
	third, err := shapeKeyFetch(t.Context(), col, 100)
	if err != nil {
		t.Fatal(err)
	}
	if third.Handle != shapeKeyHandle || third.Epoch != 100 || third.KeyID != shapeKeyID {
		t.Fatalf("metadata = %s %d %s", third.Handle, third.Epoch, third.KeyID)
	}
	if srv.count() != 1 {
		t.Fatalf("requests = %d, want 1 after metadata edit", srv.count())
	}
}

func TestShapeKeyCacheIsScopedByKeyIDAndCollection(t *testing.T) {
	srv, col := newShapeKeyServer(t)
	const otherKeyID = "01ARZ3NDEKTSV4RRFFQ69G5FC1"
	fetches := []struct {
		name string
		col  *Collection
		id   string
	}{
		{"notes key", col, shapeKeyID},
		{"other key ID", col, otherKeyID},
		{"other collection", col.db.Collection("other"), shapeKeyID},
		{"other database", col.db.c.Database(shapeKeyProject, "other").Collection("notes"), shapeKeyID},
	}
	for _, f := range fetches {
		if _, err := f.col.ShapeKey(t.Context(), shapeKeyHandle, 100, f.id); err != nil {
			t.Fatalf("%s: %v", f.name, err)
		}
	}
	if srv.count() != len(fetches) {
		t.Fatalf("requests = %d, want %d (one per scope and key ID)", srv.count(), len(fetches))
	}
	// Each tuple is now cached, so repeating all of them fetches nothing.
	for _, f := range fetches {
		if _, err := f.col.ShapeKey(t.Context(), shapeKeyHandle, 100, f.id); err != nil {
			t.Fatalf("%s repeat: %v", f.name, err)
		}
	}
	if srv.count() != len(fetches) {
		t.Fatalf("requests = %d after repeats, want %d", srv.count(), len(fetches))
	}
}

func TestShapeKeyCacheEvictsOlderEpochOnNewerSuccess(t *testing.T) {
	srv, col := newShapeKeyServer(t)
	if _, err := shapeKeyFetch(t.Context(), col, 100); err != nil {
		t.Fatal(err)
	}
	// Move into wall window 101: a successful epoch 101 must evict epoch 100.
	shapeKeyClock(col, 101*600)
	if _, err := shapeKeyFetch(t.Context(), col, 101); err != nil {
		t.Fatal(err)
	}
	if srv.count() != 2 {
		t.Fatalf("requests = %d, want 2", srv.count())
	}
	if _, err := shapeKeyFetch(t.Context(), col, 100); err != nil {
		t.Fatal(err)
	}
	if srv.count() != 3 {
		t.Fatalf("requests = %d, want 3 (epoch 100 refetched after eviction)", srv.count())
	}
	if _, err := shapeKeyFetch(t.Context(), col, 101); err != nil {
		t.Fatal(err)
	}
	if srv.count() != 3 {
		t.Fatalf("requests = %d, want 3 (epoch 101 still cached)", srv.count())
	}
}

func TestShapeKeyCacheWallRolloverEvictsBelowCurrentEpoch(t *testing.T) {
	srv, col := newShapeKeyServer(t)
	if _, err := shapeKeyFetch(t.Context(), col, 100); err != nil {
		t.Fatal(err)
	}
	// Exactly 600 seconds later the wall epoch is 101, so epoch 100 is below now.
	shapeKeyClock(col, 101*600)
	if _, err := shapeKeyFetch(t.Context(), col, 100); err != nil {
		t.Fatal(err)
	}
	if srv.count() != 2 {
		t.Fatalf("requests = %d, want 2 (historical epoch refetched)", srv.count())
	}
	// Historical keys are fetched but never cached past their epoch.
	if _, err := shapeKeyFetch(t.Context(), col, 100); err != nil {
		t.Fatal(err)
	}
	if srv.count() != 3 {
		t.Fatalf("requests = %d, want 3 (historical epoch not cached)", srv.count())
	}
}

func TestShapeKeyCacheKeyRoute409EvictsCachedKey(t *testing.T) {
	srv, col := newShapeKeyServer(t)
	if _, err := shapeKeyFetch(t.Context(), col, 100); err != nil {
		t.Fatal(err)
	}
	// Only a fetch that reaches the server can 409, so refuse a different epoch.
	srv.with(func() { srv.refuse["101"] = true })
	key, err := shapeKeyFetch(t.Context(), col, 101)
	if !errors.Is(err, ErrMustRefetch) || key != nil {
		t.Fatalf("err = %v, key = %v; want must_refetch and no key", err, key)
	}
	if _, err := shapeKeyFetch(t.Context(), col, 100); err != nil {
		t.Fatal(err)
	}
	if srv.count() != 3 {
		t.Fatalf("requests = %d, want 3 (cached epoch 100 evicted by key 409)", srv.count())
	}
}

func TestShapeKeySubscribeShape409EvictsKeys(t *testing.T) {
	srv, col := newShapeKeyServer(t)
	if _, err := shapeKeyFetch(t.Context(), col, 100); err != nil {
		t.Fatal(err)
	}
	srv.with(func() { srv.shapeRefuse = true })
	// The first yield after a shape must_refetch is the reset state; stop there.
	for range col.Subscribe(t.Context(), ShapeOptions{}) {
		break
	}
	srv.with(func() { srv.shapeRefuse = false })
	before := srv.count()
	if _, err := shapeKeyFetch(t.Context(), col, 100); err != nil {
		t.Fatal(err)
	}
	if srv.count() != before+1 {
		t.Fatalf("requests = %d, want %d (key evicted by shape 409)", srv.count(), before+1)
	}
}

func TestShapeKeyCoalescesConcurrentFetchesAndHonorsWaiterCancel(t *testing.T) {
	srv, col := newShapeKeyServer(t)
	srv.with(func() { srv.gate = "100" })
	leader := make(chan error, 1)
	go func() {
		_, err := shapeKeyFetch(t.Context(), col, 100)
		leader <- err
	}()
	<-srv.arrived

	// Done signals that the waiter reached its select on the in-flight key.
	cancelled, cancel := context.WithCancel(t.Context())
	waiting := &shapeKeyWaitContext{Context: cancelled, entered: make(chan struct{})}
	cancelResult := make(chan error, 1)
	go func() {
		_, err := shapeKeyFetch(waiting, col, 100)
		cancelResult <- err
	}()
	<-waiting.entered
	cancel()
	if err := <-cancelResult; !errors.Is(err, context.Canceled) {
		t.Fatalf("cancelled waiter err = %v, want context.Canceled", err)
	}

	waiters := make(chan error, 3)
	for range 3 {
		waiting := &shapeKeyWaitContext{Context: t.Context(), entered: make(chan struct{})}
		go func() {
			_, err := shapeKeyFetch(waiting, col, 100)
			waiters <- err
		}()
		<-waiting.entered
	}
	close(srv.release)
	if err := <-leader; err != nil {
		t.Fatal(err)
	}
	for range 3 {
		if err := <-waiters; err != nil {
			t.Fatal(err)
		}
	}
	if srv.count() != 1 {
		t.Fatalf("requests = %d, want 1 (concurrent calls coalesce)", srv.count())
	}
}

type shapeKeyWaitContext struct {
	context.Context
	entered chan struct{}
	once    sync.Once
}

func (ctx *shapeKeyWaitContext) Done() <-chan struct{} {
	ctx.once.Do(func() { close(ctx.entered) })
	return ctx.Context.Done()
}

func TestShapeKeyLiveWaiterRetriesWhenFetchOwnerCancels(t *testing.T) {
	srv, col := newShapeKeyServer(t)
	srv.with(func() { srv.gate = "100" })
	leaderCtx, cancel := context.WithCancel(t.Context())
	defer cancel()
	leader := make(chan error, 1)
	go func() {
		_, err := shapeKeyFetch(leaderCtx, col, 100)
		leader <- err
	}()
	<-srv.arrived
	waiting := &shapeKeyWaitContext{Context: t.Context(), entered: make(chan struct{})}
	joiner := make(chan error, 1)
	go func() {
		_, err := shapeKeyFetch(waiting, col, 100)
		joiner <- err
	}()
	<-waiting.entered
	cancel()
	if err := <-leader; !errors.Is(err, context.Canceled) {
		t.Fatalf("fetch owner error = %v, want context.Canceled", err)
	}
	select {
	case err := <-joiner:
		t.Fatalf("live waiter returned before its retry: %v", err)
	case <-srv.arrived:
	case <-time.After(2 * time.Second):
		t.Fatal("live waiter did not retry the cancelled fetch")
	}
	close(srv.release)
	if err := <-joiner; err != nil {
		t.Fatalf("live waiter error = %v", err)
	}
	if srv.count() != 2 {
		t.Fatalf("requests = %d, want cancelled fetch plus one retry", srv.count())
	}
	if _, err := shapeKeyFetch(t.Context(), col, 100); err != nil {
		t.Fatal(err)
	}
	if srv.count() != 2 {
		t.Fatal("successful retry was not cached")
	}
}

func TestShapeKeyCacheReturnsHTTPTimeoutWithoutRestartingFetch(t *testing.T) {
	var calls atomic.Int64
	server := httptest.NewServer(http.HandlerFunc(func(w http.ResponseWriter, r *http.Request) {
		calls.Add(1)
		<-r.Context().Done()
	}))
	defer server.Close()
	c, err := New(Config{BaseURL: server.URL, Token: shapeKeyToken, MaxRetries: -1,
		HTTPClient: &http.Client{Timeout: 20 * time.Millisecond}})
	if err != nil {
		t.Fatal(err)
	}
	ctx, cancel := context.WithTimeout(t.Context(), 500*time.Millisecond)
	defer cancel()
	key, err := shapeKeyFetch(ctx, c.Database(shapeKeyProject, "demo").Collection("notes"), 100)
	if !errors.Is(err, context.DeadlineExceeded) || key != nil || ctx.Err() != nil {
		t.Fatalf("key = %v, error = %v, caller context = %v; want HTTP timeout with live caller", key, err, ctx.Err())
	}
	if calls.Load() != 1 {
		t.Fatalf("requests = %d, want one timed-out fetch", calls.Load())
	}
}

func TestShapeKeyInvalidatedInFlightFetchDoesNotRepopulate(t *testing.T) {
	srv, col := newShapeKeyServer(t)
	srv.with(func() { srv.gate = "101" })
	pending := make(chan error, 1)
	go func() {
		_, err := shapeKeyFetch(t.Context(), col, 101)
		pending <- err
	}()
	<-srv.arrived

	// A key-route 409 for epoch 100 evicts every collection key, including the pending 101 fetch.
	srv.with(func() { srv.refuse["100"] = true })
	if _, err := shapeKeyFetch(t.Context(), col, 100); !errors.Is(err, ErrMustRefetch) {
		t.Fatalf("409 err = %v, want must_refetch", err)
	}
	close(srv.release)
	if err := <-pending; !errors.Is(err, ErrMustRefetch) {
		t.Fatalf("pending err = %v, want must_refetch", err)
	}

	// The pending response must not have been cached: the next call fetches again.
	srv.with(func() { srv.gate = "" })
	before := srv.count()
	if _, err := shapeKeyFetch(t.Context(), col, 101); err != nil {
		t.Fatal(err)
	}
	if srv.count() != before+1 {
		t.Fatalf("requests = %d, want %d (invalidated response repopulated cache)", srv.count(), before+1)
	}
}
