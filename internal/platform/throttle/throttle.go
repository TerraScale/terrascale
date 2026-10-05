// Package throttle limits how fast passwords can be guessed. A Limiter keeps
// one budget per client address and one per account, so neither a single
// client nor many clients aimed at one account can guess at the rate the
// server hashes passwords.
package throttle

import (
	"log/slog"
	"net"
	"net/http"
	"net/netip"
	"strings"
	"time"
)

// Budget allows Burst attempts at once, then one more every Interval.
type Budget struct {
	Burst    int
	Interval time.Duration
}

// Options configure a Limiter. A zero budget uses the default.
type Options struct {
	// Store keeps the buckets; nil keeps them in this process's memory.
	Store Store
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
	// maxAccountKey caps the account part of a key; a longer value is not a
	// valid email address anyway.
	maxAccountKey = 320
)

// Limiter is a set of GCRA token buckets, safe for concurrent use. The
// buckets live in its Store, by default this process's memory.
type Limiter struct {
	opts  Options
	now   func() time.Time
	store Store
}

func New(opts Options) *Limiter {
	if opts.Client.Burst < 1 || opts.Client.Interval <= 0 {
		opts.Client = defaultClient
	}
	if opts.Account.Burst < 1 || opts.Account.Interval <= 0 {
		opts.Account = defaultAccount
	}
	store := opts.Store
	if store == nil {
		store = NewMemoryStore()
	}
	return &Limiter{opts: opts, now: time.Now, store: store}
}

// Allow reserves one attempt for the request's client and, when account is
// not empty, for that account. When either budget is spent it reserves
// nothing and reports how long to wait. A store failure refuses the attempt
// for one client interval, so an outage never disables the limit.
func (l *Limiter) Allow(r *http.Request, account string) (time.Duration, bool) {
	buckets := []Bucket{{Key: l.ClientKey(r), Budget: l.opts.Client}}
	if account != "" {
		buckets = append(buckets, Bucket{Key: accountKey(account), Budget: l.opts.Account})
	}
	wait, ok, err := l.store.Reserve(r.Context(), l.now(), buckets)
	if err != nil {
		// Fail closed: an attempt the limiter cannot count is refused.
		slog.Warn("throttle: reserve attempt", "error", err)
		return l.opts.Client.Interval, false
	}
	return wait, ok
}

// Forgive returns the client's reservation and clears the account's budget
// after a correct password, so only failed attempts count against either.
// It is best effort: a store failure leaves the attempt counted.
func (l *Limiter) Forgive(r *http.Request, account string) {
	ctx := r.Context()
	_ = l.store.Refund(ctx, l.now(), Bucket{Key: l.ClientKey(r), Budget: l.opts.Client})
	if account != "" {
		_ = l.store.Reset(ctx, accountKey(account))
	}
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
