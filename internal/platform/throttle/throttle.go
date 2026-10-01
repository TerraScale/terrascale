// Package throttle limits how fast passwords can be guessed. A Limiter keeps
// one budget per client address and one per account, so neither a single
// client nor many clients aimed at one account can guess at the rate the
// server hashes passwords.
package throttle

import (
	"net"
	"net/http"
	"net/netip"
	"strings"
	"sync"
	"time"
)

// Budget allows Burst attempts at once, then one more every Interval.
type Budget struct {
	Burst    int
	Interval time.Duration
}

// Options configure a Limiter. A zero budget uses the default.
type Options struct {
	// ClientHeader names the header in which a trusted reverse proxy reports
	// the client address, such as Fly-Client-IP. When it is empty or absent,
	// the connection's remote address identifies the client.
	ClientHeader string
	Client       Budget
	Account      Budget
}

var (
	defaultClient  = Budget{Burst: 10, Interval: 12 * time.Second}
	defaultAccount = Budget{Burst: 10, Interval: 30 * time.Second}
)

const (
	// maxKeys bounds the memory the limiter holds. Once it is reached,
	// buckets that have refilled are dropped, and a new client or account is
	// refused while every tracked bucket is still in use.
	maxKeys = 100_000
	// pruneEvery spaces out the scans for refilled buckets while the limiter
	// is full, so a flood of new keys cannot make every request scan.
	pruneEvery = time.Second
	// maxAccountKey caps the account part of a key; a longer value is not a
	// valid email address anyway.
	maxAccountKey = 320
)

// Limiter is a set of GCRA token buckets, safe for concurrent use. Like the
// demo's sessions, the buckets live in this process's memory.
type Limiter struct {
	opts Options
	now  func() time.Time

	mu sync.Mutex
	// due holds each bucket's theoretical arrival time: the bucket is full
	// once it has passed, and one Interval later per attempt taken.
	due      map[string]time.Time
	prunedAt time.Time
}

func New(opts Options) *Limiter {
	if opts.Client.Burst < 1 || opts.Client.Interval <= 0 {
		opts.Client = defaultClient
	}
	if opts.Account.Burst < 1 || opts.Account.Interval <= 0 {
		opts.Account = defaultAccount
	}
	return &Limiter{opts: opts, now: time.Now, due: make(map[string]time.Time)}
}

// Allow reserves one attempt for the request's client and, when account is
// not empty, for that account. When either budget is spent it reserves
// nothing and reports how long to wait.
func (l *Limiter) Allow(r *http.Request, account string) (time.Duration, bool) {
	client := l.ClientKey(r)
	now := l.now()
	l.mu.Lock()
	defer l.mu.Unlock()
	wait := l.waitLocked(client, l.opts.Client, now)
	if account != "" {
		wait = max(wait, l.waitLocked(accountKey(account), l.opts.Account, now))
	}
	if wait > 0 {
		return wait, false
	}
	l.takeLocked(client, l.opts.Client, now)
	if account != "" {
		l.takeLocked(accountKey(account), l.opts.Account, now)
	}
	return 0, true
}

// Forgive returns the client's reservation and clears the account's budget
// after a correct password, so only failed attempts count against either.
func (l *Limiter) Forgive(r *http.Request, account string) {
	client := l.ClientKey(r)
	now := l.now()
	l.mu.Lock()
	defer l.mu.Unlock()
	if due, ok := l.due[client]; ok {
		if due = due.Add(-l.opts.Client.Interval); due.After(now) {
			l.due[client] = due
		} else {
			delete(l.due, client)
		}
	}
	if account != "" {
		delete(l.due, accountKey(account))
	}
}

// waitLocked reports how long key must wait for its next attempt.
func (l *Limiter) waitLocked(key string, budget Budget, now time.Time) time.Duration {
	due, tracked := l.due[key]
	if !tracked {
		if len(l.due) >= maxKeys && !l.pruneLocked(now) {
			return budget.Interval
		}
		return 0
	}
	tolerance := time.Duration(budget.Burst-1) * budget.Interval
	return max(0, due.Sub(now)-tolerance)
}

func (l *Limiter) takeLocked(key string, budget Budget, now time.Time) {
	due := l.due[key]
	if due.Before(now) {
		due = now
	}
	l.due[key] = due.Add(budget.Interval)
}

// pruneLocked drops refilled buckets, at most once per pruneEvery, and
// reports whether there is room for another key.
func (l *Limiter) pruneLocked(now time.Time) bool {
	if now.Sub(l.prunedAt) >= pruneEvery {
		l.prunedAt = now
		for key, due := range l.due {
			if !due.After(now) {
				delete(l.due, key)
			}
		}
	}
	return len(l.due) < maxKeys
}

// ClientKey identifies the request's client by the address the trusted
// proxy header reports, or else by the connection's remote address. IPv6
// clients are grouped by /64, the block one subscriber usually holds.
func (l *Limiter) ClientKey(r *http.Request) string {
	addr, ok := netip.Addr{}, false
	if l.opts.ClientHeader != "" {
		addr, ok = parseAddr(r.Header.Get(l.opts.ClientHeader))
	}
	if !ok {
		host, _, err := net.SplitHostPort(r.RemoteAddr)
		if err != nil {
			host = r.RemoteAddr
		}
		if addr, ok = parseAddr(host); !ok {
			return "client " + host
		}
	}
	if addr.Is6() {
		prefix, _ := addr.Prefix(64)
		return "client " + prefix.String()
	}
	return "client " + addr.String()
}

func parseAddr(raw string) (netip.Addr, bool) {
	addr, err := netip.ParseAddr(strings.TrimSpace(raw))
	if err != nil {
		return netip.Addr{}, false
	}
	return addr.Unmap().WithZone(""), true
}

func accountKey(account string) string {
	if len(account) > maxAccountKey {
		account = account[:maxAccountKey]
	}
	return "account " + account
}
