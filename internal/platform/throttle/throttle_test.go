package throttle

import (
	"fmt"
	"net/http"
	"net/http/httptest"
	"testing"
	"time"
)

var start = time.Date(2026, 9, 26, 12, 0, 0, 0, time.UTC)

// newLimiter returns a limiter on a clock the test advances.
func newLimiter(opts Options) (*Limiter, *time.Time) {
	l := New(opts)
	now := start
	l.now = func() time.Time { return now }
	return l, &now
}

func from(remoteAddr string) *http.Request {
	r := httptest.NewRequest(http.MethodPost, "/api/v1/auth/password/login", nil)
	r.RemoteAddr = remoteAddr
	return r
}

func TestClientBudgetRefillsOneAttemptPerInterval(t *testing.T) {
	l, now := newLimiter(Options{Client: Budget{Burst: 3, Interval: 10 * time.Second}})
	r := from("203.0.113.7:4000")
	for i := range 3 {
		if _, ok := l.Allow(r, ""); !ok {
			t.Fatalf("attempt %d refused within the burst", i+1)
		}
	}
	if wait, ok := l.Allow(r, ""); ok || wait != 10*time.Second {
		t.Fatalf("attempt after the burst = %v, %t; want a 10s wait", wait, ok)
	}
	*now = now.Add(4 * time.Second)
	if wait, ok := l.Allow(r, ""); ok || wait != 6*time.Second {
		t.Fatalf("attempt 4s later = %v, %t; want a 6s wait", wait, ok)
	}
	*now = now.Add(6 * time.Second)
	if _, ok := l.Allow(r, ""); !ok {
		t.Fatal("attempt after one interval refused")
	}
	if _, ok := l.Allow(r, ""); ok {
		t.Fatal("second attempt after one interval allowed")
	}
	if _, ok := l.Allow(from("203.0.113.8:4000"), ""); !ok {
		t.Fatal("another client shares the spent budget")
	}
}

func TestAccountBudgetSpansClients(t *testing.T) {
	l, _ := newLimiter(Options{Account: Budget{Burst: 2, Interval: time.Minute}})
	for i := range 2 {
		if _, ok := l.Allow(from(fmt.Sprintf("198.51.100.%d:1", i)), "ana@example.test"); !ok {
			t.Fatalf("attempt %d refused within the account burst", i+1)
		}
	}
	if wait, ok := l.Allow(from("198.51.100.9:1"), "ana@example.test"); ok || wait != time.Minute {
		t.Fatalf("third client for the account = %v, %t; want a 1m wait", wait, ok)
	}
	if _, ok := l.Allow(from("198.51.100.9:1"), "ravi@example.test"); !ok {
		t.Fatal("refused attempt reserved the client budget, or accounts share a budget")
	}
}

func TestRefusedAttemptReservesNothing(t *testing.T) {
	l, _ := newLimiter(Options{
		Client:  Budget{Burst: 2, Interval: time.Minute},
		Account: Budget{Burst: 1, Interval: time.Minute},
	})
	r := from("192.0.2.1:1")
	if _, ok := l.Allow(r, "ana@example.test"); !ok {
		t.Fatal("first attempt refused")
	}
	for range 3 {
		if _, ok := l.Allow(r, "ana@example.test"); ok {
			t.Fatal("attempt past the account budget allowed")
		}
	}
	if _, ok := l.Allow(r, "ravi@example.test"); !ok {
		t.Fatal("attempts refused by the account budget spent the client budget")
	}
}

func TestForgiveCountsOnlyFailures(t *testing.T) {
	l, _ := newLimiter(Options{
		Client:  Budget{Burst: 2, Interval: time.Minute},
		Account: Budget{Burst: 2, Interval: time.Minute},
	})
	r := from("192.0.2.1:1")
	for range 10 {
		if _, ok := l.Allow(r, "ana@example.test"); !ok {
			t.Fatal("forgiven attempts spent the budget")
		}
		l.Forgive(r, "ana@example.test")
	}
	l.Allow(r, "ana@example.test")
	l.Allow(r, "ana@example.test")
	if _, ok := l.Allow(r, "ana@example.test"); ok {
		t.Fatal("failed attempts did not spend the budget")
	}
	if len(l.due) != 2 {
		t.Fatalf("tracking %d buckets, want the client and the account", len(l.due))
	}
}

func TestClientKey(t *testing.T) {
	behindProxy := New(Options{ClientHeader: "Fly-Client-IP"})
	direct := New(Options{})
	for _, tc := range []struct {
		name    string
		limiter *Limiter
		remote  string
		header  string
		want    string
	}{
		{"remote IPv4", direct, "203.0.113.7:4000", "", "client 203.0.113.7"},
		{"header ignored without a trusted proxy", direct, "203.0.113.7:4000", "198.51.100.1", "client 203.0.113.7"},
		{"trusted proxy header", behindProxy, "172.16.0.2:4000", "198.51.100.1", "client 198.51.100.1"},
		{"missing proxy header", behindProxy, "172.16.0.2:4000", "", "client 172.16.0.2"},
		{"malformed proxy header", behindProxy, "172.16.0.2:4000", "198.51.100.1, 10.0.0.1", "client 172.16.0.2"},
		{"IPv4-mapped IPv6", direct, "[::ffff:203.0.113.7]:4000", "", "client 203.0.113.7"},
		{"IPv6 by /64", direct, "[2001:db8:1:2:aaaa::1]:4000", "", "client 2001:db8:1:2::/64"},
		{"IPv6 header by /64", behindProxy, "172.16.0.2:4000", "2001:db8:1:2:bbbb::9", "client 2001:db8:1:2::/64"},
	} {
		t.Run(tc.name, func(t *testing.T) {
			r := from(tc.remote)
			if tc.header != "" {
				r.Header.Set("Fly-Client-IP", tc.header)
			}
			if got := tc.limiter.clientKey(r); got != tc.want {
				t.Fatalf("clientKey = %q, want %q", got, tc.want)
			}
		})
	}
}

func TestMemoryStaysBounded(t *testing.T) {
	l, now := newLimiter(Options{Client: Budget{Burst: 1, Interval: time.Minute}})
	for i := range maxKeys {
		l.due[fmt.Sprintf("client %d", i)] = now.Add(time.Minute)
	}
	if wait, ok := l.Allow(from("192.0.2.1:1"), ""); ok || wait != time.Minute {
		t.Fatalf("new client while every bucket is in use = %v, %t; want refused", wait, ok)
	}
	*now = now.Add(time.Minute)
	if _, ok := l.Allow(from("192.0.2.1:1"), ""); !ok {
		t.Fatal("new client refused after the buckets refilled")
	}
	if len(l.due) != 1 {
		t.Fatalf("tracking %d buckets after pruning, want 1", len(l.due))
	}
}
