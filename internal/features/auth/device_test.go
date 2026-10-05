package auth

import (
	"context"
	"encoding/json/v2"
	"fmt"
	"net/http"
	"net/http/httptest"
	"net/url"
	"strings"
	"sync"
	"sync/atomic"
	"testing"
	"time"

	"terrascale.tech/dashboard/internal/core"
	"terrascale.tech/dashboard/internal/core/memstore"
	"terrascale.tech/dashboard/internal/platform/csrf"
	"terrascale.tech/dashboard/internal/platform/render"
	"terrascale.tech/dashboard/internal/platform/session"
	"terrascale.tech/dashboard/internal/platform/throttle"
)

type deviceFixture struct {
	d                  *DeviceAuthorization
	store              core.Store
	sessions           *session.Manager
	guard              *csrf.Guard
	p                  core.Principal
	cookie, csrfCookie *http.Cookie
	csrfToken          string
	time               atomic.Int64
	public             http.Handler
	browser            http.Handler
}

func newDeviceFixture(t *testing.T) *deviceFixture {
	t.Helper()
	return newDeviceFixtureWithSessionStore(t, nil)
}

func newDeviceFixtureWithSessionStore(t *testing.T, state session.Store) *deviceFixture {
	t.Helper()
	return newDeviceFixtureWithStores(t, state, memstore.New())
}

func newDeviceFixtureWithStores(t *testing.T, state session.Store, accounts core.Store) *deviceFixture {
	t.Helper()
	f := &deviceFixture{store: accounts}
	// A valid fixed hash avoids repeating a slow password hash for every fixture.
	hash := deviceTestHash()
	a, err := f.store.CreateAccount(t.Context(), core.User{Name: "Fixture", Email: "device@example.test", PasswordHash: hash})
	if err != nil {
		t.Fatal(err)
	}
	f.p = a.Principal()
	f.sessions = session.New(f.store, session.Options{Store: state})
	f.guard = csrf.New(f.sessions, csrf.Options{})
	deps := core.Deps{Store: f.store, Sessions: f.sessions, CSRF: f.guard, Render: render.New(f.store, f.guard, nil, false), Throttle: throttle.New(throttle.Options{Client: throttle.Budget{Burst: 100, Interval: time.Second}, Account: throttle.Budget{Burst: 100, Interval: time.Second}}), Demo: true}
	f.d = NewDeviceAuthorization(deps, nil, false, "http://dashboard.test")
	f.time.Store(time.Now().Unix())
	f.d.now = func() time.Time { return time.Unix(f.time.Load(), 0) }
	start := httptest.NewRecorder()
	req := httptest.NewRequest("GET", "http://dashboard.test/", nil)
	if err := f.sessions.Start(start, req, f.p); err != nil {
		t.Fatal(err)
	}
	f.cookie = start.Result().Cookies()[0]
	csrfResponse := httptest.NewRecorder()
	f.csrfToken = f.guard.Token(csrfResponse, req)
	f.csrfCookie = csrfResponse.Result().Cookies()[0]
	f.public = f.d.PublicHandler()
	mux := http.NewServeMux()
	f.d.MountBrowser(mux)
	f.browser = f.guard.Protect(mux)
	return f
}

var deviceTestHash = sync.OnceValue(func() string {
	hash, err := HashPassword("device-fixture-password")
	if err != nil {
		panic(err)
	}
	return hash
})

func (f *deviceFixture) post(handler http.Handler, path string, form url.Values, browser bool) *httptest.ResponseRecorder {
	r := httptest.NewRequest("POST", "http://dashboard.test"+path, strings.NewReader(form.Encode()))
	r.Header.Set("Content-Type", "application/x-www-form-urlencoded")
	if browser {
		r.AddCookie(f.cookie)
		r.AddCookie(f.csrfCookie)
	}
	w := httptest.NewRecorder()
	handler.ServeHTTP(w, r)
	return w
}

func (f *deviceFixture) issue(t *testing.T) (string, string) {
	t.Helper()
	w := f.post(f.public, "/api/v1/dashboard/cli/device-authorization", url.Values{"client_id": {deviceClient}}, false)
	var result struct {
		Device string `json:"device_code"`
		Code   string `json:"user_code"`
	}
	if err := json.Unmarshal(w.Body.Bytes(), &result); err != nil || w.Code != 200 || result.Device == "" || result.Code == "" {
		t.Fatalf("issue: %d %s %v", w.Code, w.Body.String(), err)
	}
	return result.Device, result.Code
}

func (f *deviceFixture) poll(device string) *httptest.ResponseRecorder {
	return f.post(f.public, "/api/v1/dashboard/cli/token", url.Values{"client_id": {deviceClient}, "grant_type": {deviceGrantType}, "device_code": {device}}, false)
}

func (f *deviceFixture) decide(code, decision, password string) *httptest.ResponseRecorder {
	return f.post(f.browser, "/cli/approve/submit", url.Values{"_csrf": {f.csrfToken}, "user_code": {code}, "decision": {decision}, "password": {password}}, true)
}

func TestDeviceApprovalDeliversOneUsableToken(t *testing.T) {
	f := newDeviceFixture(t)
	device, code := f.issue(t)
	f.time.Add(5)
	if w := f.poll(device); !strings.Contains(w.Body.String(), "authorization_pending") {
		t.Fatalf("pending: %s", w.Body.String())
	}
	if w := f.decide(code, "approve", "device-fixture-password"); w.Code != 200 || !strings.Contains(w.Body.String(), "CLI authorized") {
		t.Fatalf("approval: %d %s", w.Code, w.Body.String())
	}
	f.time.Add(5)
	var successes atomic.Int64
	var wg sync.WaitGroup
	for range 2 {
		wg.Go(func() {
			w := f.poll(device)
			if w.Code == 200 {
				successes.Add(1)
				var result struct {
					Token string `json:"access_token"`
				}
				if err := json.Unmarshal(w.Body.Bytes(), &result); err != nil {
					t.Error(err)
				}
				p, err := f.store.AccessTokenPrincipal(t.Context(), result.Token)
				if err != nil || p.UserID != f.p.UserID {
					t.Error("redeemed token cannot access dashboard")
				}
			}
		})
	}
	wg.Wait()
	if successes.Load() != 1 {
		t.Fatalf("grant redeemed %d times", successes.Load())
	}
	a, err := f.store.SnapshotAs(t.Context(), f.p)
	if err != nil || len(a.AccessTokens) != 1 {
		t.Fatal("duplicate or missing CLI token")
	}
}

func TestDeviceDenialExpiryAndPollCadence(t *testing.T) {
	for _, action := range []string{"deny", "expire", "fast-poll"} {
		t.Run(action, func(t *testing.T) {
			f := newDeviceFixture(t)
			device, code := f.issue(t)
			var response *httptest.ResponseRecorder
			switch action {
			case "deny":
				f.decide(code, "deny", "")
				f.time.Add(5)
				response = f.poll(device)
			case "expire":
				f.time.Add(int64(deviceLifetime.Seconds()))
				response = f.poll(device)
			case "fast-poll":
				response = f.poll(device)
			}
			want := map[string]string{"deny": "access_denied", "expire": "expired_token", "fast-poll": "slow_down"}[action]
			if response.Code != 400 || !strings.Contains(response.Body.String(), want) {
				t.Fatalf("%s: %d %s", action, response.Code, response.Body.String())
			}
			a, err := f.store.SnapshotAs(t.Context(), f.p)
			if err != nil || len(a.AccessTokens) != 0 {
				t.Fatal("token issued without approval")
			}
		})
	}
}

func TestDeviceApprovalRequiresCSRFPasswordAndLiveSession(t *testing.T) {
	f := newDeviceFixture(t)
	device, code := f.issue(t)
	w := f.post(f.browser, "/cli/approve/submit", url.Values{"user_code": {code}, "decision": {"approve"}, "password": {"device-fixture-password"}}, true)
	if w.Code != 403 {
		t.Fatal("approved without CSRF")
	}
	if w := f.decide(code, "approve", "wrong-password"); !strings.Contains(w.Body.String(), "Current password is incorrect") {
		t.Fatal("approved without current password")
	}
	f.decide(code, "approve", "device-fixture-password")
	r := httptest.NewRequest("POST", "http://dashboard.test/logout", nil)
	r.AddCookie(f.cookie)
	f.sessions.End(httptest.NewRecorder(), r)
	f.time.Add(5)
	if w := f.poll(device); w.Code != 400 || !strings.Contains(w.Body.String(), "access_denied") {
		t.Fatal("redeemed after browser logout")
	}
	account, err := f.store.SnapshotAs(t.Context(), f.p)
	if err != nil || len(account.AccessTokens) != 0 {
		t.Fatal("issued a token for an ended session")
	}
}

func TestDeviceDenialDoesNotResetPasswordGuessingBudget(t *testing.T) {
	f := newDeviceFixture(t)
	limiter := throttle.New(throttle.Options{Client: throttle.Budget{Burst: 100, Interval: time.Hour}, Account: throttle.Budget{Burst: 2, Interval: time.Hour}})
	f.d.deps.Throttle = limiter
	_, code := f.issue(t)
	if w := f.decide(code, "approve", "wrong-password"); !strings.Contains(w.Body.String(), "Current password is incorrect") {
		t.Fatal("first password attempt unexpectedly rejected")
	}
	if w := f.decide(code, "deny", ""); w.Code != 200 {
		t.Fatalf("denial failed: %d", w.Code)
	}
	_, nextCode := f.issue(t)
	if w := f.decide(nextCode, "approve", "wrong-password"); w.Code != http.StatusTooManyRequests {
		t.Fatal("denial reset the account's password guessing budget")
	}
}

func TestDeviceApprovalInvalidatesOnCredentialChange(t *testing.T) {
	f := newDeviceFixture(t)
	device, code := f.issue(t)
	f.decide(code, "approve", "device-fixture-password")
	if err := f.store.UpdateAs(t.Context(), f.p, func(a *core.Account) error { a.User.PasswordHash = "changed-password-hash"; return nil }); err != nil {
		t.Fatal(err)
	}
	f.time.Add(5)
	if w := f.poll(device); w.Code != 400 || !strings.Contains(w.Body.String(), "access_denied") {
		t.Fatal("redeemed after credential change")
	}
}

func TestDevicePublicEndpointsRejectCrossSiteAndUnknownClients(t *testing.T) {
	f := newDeviceFixture(t)
	w := f.post(f.public, "/api/v1/dashboard/cli/device-authorization", url.Values{"client_id": {"other-client"}}, false)
	if w.Code != 400 {
		t.Fatal("accepted unknown client")
	}
	r := httptest.NewRequest("POST", "http://dashboard.test/api/v1/dashboard/cli/device-authorization", strings.NewReader("client_id=terrascale-cli"))
	r.Header.Set("Content-Type", "application/x-www-form-urlencoded")
	r.Header.Set("Origin", "http://other.test")
	r.Header.Set("Sec-Fetch-Site", "cross-site")
	w = httptest.NewRecorder()
	f.public.ServeHTTP(w, r)
	if w.Code != 403 {
		t.Fatal("accepted cross-site device request")
	}
}

func TestDeviceGrantQuotaIsPerClient(t *testing.T) {
	f := newDeviceFixture(t)
	for range maxDeviceGrantsPerClient {
		f.issue(t)
	}
	if w := f.post(f.public, "/api/v1/dashboard/cli/device-authorization", url.Values{"client_id": {deviceClient}}, false); w.Code != http.StatusTooManyRequests {
		t.Fatalf("unbounded client grants: %d", w.Code)
	}
	r := httptest.NewRequest("POST", "http://dashboard.test/api/v1/dashboard/cli/device-authorization", strings.NewReader("client_id=terrascale-cli"))
	r.RemoteAddr = "192.0.2.2:1234"
	r.Header.Set("Content-Type", "application/x-www-form-urlencoded")
	w := httptest.NewRecorder()
	f.public.ServeHTTP(w, r)
	if w.Code != 200 {
		t.Fatalf("one client's quota blocked another: %d", w.Code)
	}
}
func TestDeviceApprovalUsesConfiguredOrigin(t *testing.T) {
	f := newDeviceFixture(t)
	r := httptest.NewRequest("POST", "http://evil.test/api/v1/dashboard/cli/device-authorization", strings.NewReader("client_id=terrascale-cli"))
	r.Header.Set("Content-Type", "application/x-www-form-urlencoded")
	w := httptest.NewRecorder()
	f.public.ServeHTTP(w, r)
	if w.Code != 200 || strings.Contains(w.Body.String(), "evil.test") || !strings.Contains(w.Body.String(), "http://dashboard.test/cli/approve") {
		t.Fatalf("untrusted verification origin: %s", w.Body.String())
	}
}
func TestDeviceTokenQuotaFailureCanBeRetried(t *testing.T) {
	f := newDeviceFixture(t)
	var revokeID string
	for range core.MaxAccessTokens {
		token, _, err := f.store.CreateAccessToken(t.Context(), f.p, "existing", time.Hour)
		if err != nil {
			t.Fatal(err)
		}
		revokeID = token.ID
	}
	device, code := f.issue(t)
	f.decide(code, "approve", "device-fixture-password")
	f.time.Add(5)
	if w := f.poll(device); w.Code != http.StatusServiceUnavailable || !strings.Contains(w.Body.String(), "temporarily_unavailable") {
		t.Fatalf("quota failure mislabeled: %d %s", w.Code, w.Body.String())
	}
	if err := f.store.RevokeAccessToken(t.Context(), f.p, revokeID); err != nil {
		t.Fatal(err)
	}
	f.time.Add(5)
	if w := f.poll(device); w.Code != 200 {
		t.Fatalf("approved grant lost on quota failure: %d %s", w.Code, w.Body.String())
	}
}

type contextDeviceGrants struct{ DeviceGrants }

func (g contextDeviceGrants) Update(ctx context.Context, key [32]byte, change func(*DeviceGrant) bool) error {
	if err := ctx.Err(); err != nil {
		return err
	}
	return g.DeviceGrants.Update(ctx, key, change)
}

func (g contextDeviceGrants) Delete(ctx context.Context, key [32]byte) error {
	if err := ctx.Err(); err != nil {
		return err
	}
	return g.DeviceGrants.Delete(ctx, key)
}

type cancelDeviceTokenStore struct {
	core.Store
	cancel context.CancelFunc
	mint   bool
}

func (s cancelDeviceTokenStore) CreateAccessToken(ctx context.Context, p core.Principal, name string, lifetime time.Duration) (core.AccessToken, string, error) {
	if !s.mint {
		s.cancel()
		return core.AccessToken{}, "", ctx.Err()
	}
	token, secret, err := s.Store.CreateAccessToken(ctx, p, name, lifetime)
	s.cancel()
	return token, secret, err
}

func TestCanceledDeviceRedemptionStillCleansUpTheGrant(t *testing.T) {
	for _, mint := range []bool{false, true} {
		name, wantStatus, wantRetry := "failed token creation", http.StatusServiceUnavailable, http.StatusOK
		if mint {
			name, wantStatus, wantRetry = "completed token creation", http.StatusOK, http.StatusBadRequest
		}
		t.Run(name, func(t *testing.T) {
			f := newDeviceFixture(t)
			f.d.grants = contextDeviceGrants{f.d.grants}
			device, code := f.issue(t)
			if w := f.decide(code, "approve", "device-fixture-password"); w.Code != http.StatusOK {
				t.Fatalf("approval: %d %s", w.Code, w.Body.String())
			}
			f.time.Add(5)
			ctx, cancel := context.WithCancel(t.Context())
			defer cancel()
			f.d.deps.Store = cancelDeviceTokenStore{Store: f.store, cancel: cancel, mint: mint}
			w := f.post(http.HandlerFunc(func(w http.ResponseWriter, r *http.Request) {
				f.public.ServeHTTP(w, r.WithContext(ctx))
			}), "/api/v1/dashboard/cli/token", url.Values{"client_id": {deviceClient}, "grant_type": {deviceGrantType}, "device_code": {device}}, false)
			if w.Code != wantStatus {
				t.Fatalf("canceled redemption: %d %s", w.Code, w.Body.String())
			}
			f.d.deps.Store = f.store
			f.time.Add(5)
			if w := f.poll(device); w.Code != wantRetry {
				t.Fatalf("retry after cancellation: %d %s, want %d", w.Code, w.Body.String(), wantRetry)
			} else if mint && !strings.Contains(w.Body.String(), "invalid_grant") {
				t.Fatalf("redeemed grant was not deleted: %s", w.Body.String())
			}
			account, err := f.store.SnapshotAs(t.Context(), f.p)
			if err != nil || len(account.AccessTokens) != 1 {
				t.Fatalf("tokens after retry: %d, error %v", len(account.AccessTokens), err)
			}
		})
	}
}

type faultSessionAccountStore struct {
	core.Store
	err error
}

func (s *faultSessionAccountStore) SessionPrincipal(ctx context.Context, accountID, userID string, version uint64) (core.Principal, error) {
	if s.err != nil {
		return core.Principal{}, s.err
	}
	return s.Store.SessionPrincipal(ctx, accountID, userID, version)
}

func TestDeviceSessionResolutionFailureCanBeRetried(t *testing.T) {
	for _, name := range []string{"store outage", "account outage", "canceled poll", "cleanup outage"} {
		t.Run(name, func(t *testing.T) {
			state := &faultSessionStore{Store: session.NewMemoryStore()}
			accounts := &faultSessionAccountStore{Store: memstore.New()}
			f := newDeviceFixtureWithStores(t, state, accounts)
			f.d.grants = contextDeviceGrants{f.d.grants}
			device, code := f.issue(t)
			if w := f.decide(code, "approve", "device-fixture-password"); w.Code != http.StatusOK {
				t.Fatalf("approval = %d %s", w.Code, w.Body.String())
			}
			f.time.Add(5)
			ctx, cancel := context.WithCancel(t.Context())
			defer cancel()
			grants := &failUpdateDeviceGrants{DeviceGrants: f.d.grants}
			if name == "cleanup outage" {
				grants.failAt = 2 // The old flow tried to release its claim here.
				f.d.grants = grants
			}
			if name == "store outage" || name == "cleanup outage" {
				state.getErr = fmt.Errorf("read session: %w", core.ErrUnavailable)
			} else if name == "account outage" {
				accounts.err = fmt.Errorf("read account: %w", core.ErrUnavailable)
			} else {
				state.cancel = cancel
			}
			w := f.post(http.HandlerFunc(func(w http.ResponseWriter, r *http.Request) {
				f.public.ServeHTTP(w, r.WithContext(ctx))
			}), "/api/v1/dashboard/cli/token", url.Values{"client_id": {deviceClient}, "grant_type": {deviceGrantType}, "device_code": {device}}, false)
			if w.Code != http.StatusServiceUnavailable || !strings.Contains(w.Body.String(), "temporarily_unavailable") || w.Header().Get("Retry-After") == "" {
				t.Fatalf("transient resolution failure = %d %s, headers %v", w.Code, w.Body.String(), w.Header())
			}
			grant, _, err := f.d.lookup(t.Context(), code)
			if err != nil || grant.Session == "" || grant.Redeeming {
				t.Fatalf("approval was lost or locked: %+v, %v", grant, err)
			}
			account, err := f.store.SnapshotAs(t.Context(), f.p)
			if err != nil || len(account.AccessTokens) != 0 {
				t.Fatal("transient resolution failure issued a token")
			}
			state.getErr, state.cancel, accounts.err = nil, nil, nil
			grants.failAt = 0
			f.time.Add(5)
			if w := f.poll(device); w.Code != http.StatusOK {
				t.Fatalf("retry = %d %s", w.Code, w.Body.String())
			}
			account, err = f.store.SnapshotAs(t.Context(), f.p)
			if err != nil || len(account.AccessTokens) != 1 {
				t.Fatal("retry did not issue exactly one token")
			}
		})
	}
}

func TestDeviceCorrectPasswordAgainstExpiredCodeIsForgiven(t *testing.T) {
	f := newDeviceFixture(t)
	f.d.deps.Throttle = throttle.New(throttle.Options{Client: throttle.Budget{Burst: 100, Interval: time.Hour}, Account: throttle.Budget{Burst: 1, Interval: time.Hour}})
	_, code := f.issue(t)
	f.time.Add(int64(deviceLifetime.Seconds()))
	for range 2 {
		if w := f.decide(code, "approve", "device-fixture-password"); w.Code != 200 {
			t.Fatalf("correct password spent account budget: %d", w.Code)
		}
	}
}

type failUpdateDeviceGrants struct {
	DeviceGrants
	calls, failAt int
}

func (g *failUpdateDeviceGrants) Update(ctx context.Context, key [32]byte, change func(*DeviceGrant) bool) error {
	g.calls++
	if g.calls == g.failAt {
		return core.ErrUnavailable
	}
	return g.DeviceGrants.Update(ctx, key, change)
}

type resolutionFailureSessions struct {
	core.Sessions
	core.CLIGrantSessions
	err error
}

func (s *resolutionFailureSessions) CLIGrantPrincipal(ctx context.Context, binding string) (core.Principal, bool, error) {
	if s.err != nil {
		return core.Principal{}, false, s.err
	}
	return s.CLIGrantSessions.CLIGrantPrincipal(ctx, binding)
}

func TestDeviceApprovalCanRetryFreshResolutionFailure(t *testing.T) {
	f := newDeviceFixture(t)
	_, code := f.issue(t)
	sessions := &resolutionFailureSessions{Sessions: f.sessions, CLIGrantSessions: f.sessions, err: core.ErrUnavailable}
	f.d.deps.Sessions = sessions
	if w := f.decide(code, "approve", "device-fixture-password"); w.Code != http.StatusServiceUnavailable {
		t.Fatalf("failed resolution = %d %s", w.Code, w.Body.String())
	}
	grant, pending, err := f.d.lookup(t.Context(), code)
	if err != nil || !pending || grant.Session != "" || grant.Denied || grant.Redeeming {
		t.Fatalf("failed resolution changed grant: %+v, pending %t, error %v", grant, pending, err)
	}
	sessions.err = nil
	if w := f.decide(code, "approve", "device-fixture-password"); w.Code != http.StatusOK {
		t.Fatalf("approval retry = %d %s", w.Code, w.Body.String())
	}
	grant, pending, err = f.d.lookup(t.Context(), code)
	if err != nil || pending || grant.Session == "" || grant.Denied {
		t.Fatalf("retry did not approve: %+v, pending %t, error %v", grant, pending, err)
	}
}
