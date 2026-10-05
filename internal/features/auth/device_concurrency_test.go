package auth

import (
	"context"
	"encoding/json/v2"
	"net/http"
	"net/http/httptest"
	"strings"
	"sync"
	"testing"
	"time"

	"terrascale.tech/dashboard/internal/core"
)

type blockingGrantSessions struct {
	core.Sessions
	core.CLIGrantSessions
	entered chan struct{}
	release <-chan struct{}
}

func (s *blockingGrantSessions) CLIGrantPrincipal(ctx context.Context, binding string) (core.Principal, bool, error) {
	s.entered <- struct{}{}
	select {
	case <-s.release:
		return s.CLIGrantSessions.CLIGrantPrincipal(ctx, binding)
	case <-ctx.Done():
		return core.Principal{}, false, ctx.Err()
	}
}

func blockDeviceResolution(t *testing.T, f *deviceFixture) (<-chan struct{}, func()) {
	t.Helper()
	entered, release := make(chan struct{}, 2), make(chan struct{})
	f.d.deps.Sessions = &blockingGrantSessions{Sessions: f.sessions, CLIGrantSessions: f.sessions, entered: entered, release: release}
	unblock := sync.OnceFunc(func() { close(release) })
	t.Cleanup(unblock)
	return entered, unblock
}

func awaitDeviceResolution(t *testing.T, entered <-chan struct{}) {
	t.Helper()
	select {
	case <-entered:
	case <-time.After(5 * time.Second):
		t.Fatal("poll did not reach session resolution")
	}
}

func awaitDevicePoll(t *testing.T, results <-chan *httptest.ResponseRecorder) *httptest.ResponseRecorder {
	t.Helper()
	select {
	case result := <-results:
		return result
	case <-time.After(5 * time.Second):
		t.Fatal("poll did not finish after session resolution was released")
		return nil
	}
}

func TestDeviceOverlappingResolutionsIssueOneToken(t *testing.T) {
	f := newDeviceFixture(t)
	device, code := f.issue(t)
	if w := f.decide(code, "approve", "device-fixture-password"); w.Code != http.StatusOK {
		t.Fatalf("approval=%d %s", w.Code, w.Body.String())
	}
	entered, unblock := blockDeviceResolution(t, f)
	results := make(chan *httptest.ResponseRecorder, 2)
	f.time.Add(5)
	go func() { results <- f.poll(device) }()
	awaitDeviceResolution(t, entered)
	// Advance beyond pacing while the first poll is still resolving its session.
	f.time.Add(5)
	go func() { results <- f.poll(device) }()
	awaitDeviceResolution(t, entered)
	unblock()
	successes := 0
	for range 2 {
		w := awaitDevicePoll(t, results)
		if w.Code != http.StatusOK {
			if w.Code != http.StatusBadRequest || (!strings.Contains(w.Body.String(), "authorization_pending") && !strings.Contains(w.Body.String(), "invalid_grant")) {
				t.Fatalf("losing poll=%d %s", w.Code, w.Body.String())
			}
			continue
		}
		successes++
		var result struct {
			Token string `json:"access_token"`
		}
		if err := json.Unmarshal(w.Body.Bytes(), &result); err != nil {
			t.Fatal(err)
		}
		if p, err := f.store.AccessTokenPrincipal(t.Context(), result.Token); err != nil || p.UserID != f.p.UserID {
			t.Fatalf("returned token is unusable: principal=%+v error=%v", p, err)
		}
	}
	if successes != 1 {
		t.Fatalf("overlapping resolutions redeemed %d tokens", successes)
	}
	if a, err := f.store.SnapshotAs(t.Context(), f.p); err != nil || len(a.AccessTokens) != 1 {
		t.Fatalf("stored tokens=%d error=%v; want exactly one", len(a.AccessTokens), err)
	}
}

func TestDeviceExpiryDuringResolutionIssuesNoToken(t *testing.T) {
	f := newDeviceFixture(t)
	device, code := f.issue(t)
	if w := f.decide(code, "approve", "device-fixture-password"); w.Code != http.StatusOK {
		t.Fatalf("approval=%d %s", w.Code, w.Body.String())
	}
	entered, unblock := blockDeviceResolution(t, f)
	results := make(chan *httptest.ResponseRecorder, 1)
	f.time.Add(5)
	go func() { results <- f.poll(device) }()
	awaitDeviceResolution(t, entered)
	f.time.Add(int64(deviceLifetime.Seconds()))
	unblock()
	w := awaitDevicePoll(t, results)
	if w.Code != http.StatusBadRequest || !strings.Contains(w.Body.String(), "expired_token") {
		t.Fatalf("expired poll=%d %s", w.Code, w.Body.String())
	}
	if a, err := f.store.SnapshotAs(t.Context(), f.p); err != nil || len(a.AccessTokens) != 0 {
		t.Fatalf("stored tokens=%d error=%v; want no token", len(a.AccessTokens), err)
	}
}
