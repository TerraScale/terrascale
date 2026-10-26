package tsauth

import (
	"context"
	"errors"
	"net/http"
	"sync/atomic"
	"testing"
	"time"
)

func TestTokenFetchStopsWithLastWaiterAndCannotPoisonNewFlight(t *testing.T) {
	started, stopped, release := make(chan struct{}), make(chan struct{}), make(chan struct{})
	var calls atomic.Int32
	s := newSource(nil, func(ctx context.Context, _ *http.Client) (*tokenResponse, error) {
		if calls.Add(1) == 1 {
			close(started)
			<-ctx.Done()
			close(stopped)
			<-release // Let the abandoned request finish after the new flight.
			return &tokenResponse{AccessToken: "old", ExpiresIn: 900}, nil
		}
		return &tokenResponse{AccessToken: "new", ExpiresIn: 900}, nil
	})
	ctx, cancel := context.WithCancel(t.Context())
	done := make(chan error, 1)
	go func() { _, err := s.Token(ctx); done <- err }()
	<-started
	s.mu.Lock()
	abandoned := s.flight
	s.mu.Unlock()
	cancel()
	if err := <-done; !errors.Is(err, context.Canceled) {
		t.Fatal(err)
	}
	select {
	case <-stopped:
	case <-time.After(time.Second):
		t.Fatal("last waiter left an active fetch")
	}
	if token, err := s.Token(t.Context()); err != nil || token != "new" {
		t.Fatalf("new flight: %q %v", token, err)
	}
	close(release)
	<-abandoned.done
	if token, err := s.Token(t.Context()); err != nil || token != "new" || calls.Load() != 2 {
		t.Fatalf("cached token: %q %v calls=%d", token, err, calls.Load())
	}
}

func TestCanceledTokenCallerDoesNotStartFetch(t *testing.T) {
	s := newSource(nil, func(context.Context, *http.Client) (*tokenResponse, error) {
		t.Error("canceled caller started a request")
		return nil, context.Canceled
	})
	ctx, cancel := context.WithCancel(t.Context())
	cancel()
	if _, err := s.Token(ctx); !errors.Is(err, context.Canceled) {
		t.Fatal(err)
	}
}

func TestExpiredTokenFetchCannotReturnEmptySuccess(t *testing.T) {
	for _, deadline := range []bool{false, true} {
		ctx, cancel := context.WithCancel(t.Context())
		if deadline {
			cancel()
			ctx, cancel = context.WithDeadline(t.Context(), time.Now().Add(-time.Second))
		}
		cancel()
		s := newSource(nil, func(ctx context.Context, _ *http.Client) (*tokenResponse, error) {
			<-ctx.Done()
			return &tokenResponse{AccessToken: "late", ExpiresIn: 900}, nil
		})
		f := &flight{done: make(chan struct{}), cancel: cancel, waiters: 1}
		s.flight = f
		s.run(ctx, f)
		if !errors.Is(f.err, ctx.Err()) || f.token != "" || s.token != "" {
			t.Fatalf("deadline=%v token=%q error=%v", deadline, f.token, f.err)
		}
	}
}
