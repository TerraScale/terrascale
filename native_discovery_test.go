package sdk

import (
	"bytes"
	"context"
	"crypto/sha256"
	"encoding/base64"
	"encoding/hex"
	"encoding/json"
	"errors"
	"io"
	"math"
	"net/http"
	"net/http/cookiejar"
	"net/url"
	"os"
	"strings"
	"sync"
	"sync/atomic"
	"testing"
	"time"
)

func nativeDiscoveryFixture(t *testing.T) ([]byte, nativeDiscoveryDocument) {
	t.Helper()
	body, err := os.ReadFile("contracts/native/atlas/native-discovery-success.json")
	if err != nil {
		t.Fatal(err)
	}
	var document nativeDiscoveryDocument
	if err := decodeNativeJSON(body, &document); err != nil {
		t.Fatal(err)
	}
	return body, document
}
func nativeDiscoveryTestKey(value byte) string {
	return "tbk1." + base64.RawURLEncoding.EncodeToString(bytes.Repeat([]byte{value}, 16)) + "." + base64.RawURLEncoding.EncodeToString(bytes.Repeat([]byte{value + 1}, 32))
}
func nativeDiscoveryResponse(status int, body []byte) *http.Response {
	return &http.Response{StatusCode: status, Header: http.Header{"Content-Type": {"application/json"}}, Body: io.NopCloser(bytes.NewReader(body))}
}
func nativeDiscoveryClient(t *testing.T, transport http.RoundTripper) *Client {
	t.Helper()
	client, err := NewClient(Config{DataAccessToken: nativeDiscoveryTestKey(5), HTTPClient: &http.Client{Transport: transport}})
	if err != nil {
		t.Fatal(err)
	}
	client.nativeRouter.now = func() time.Time { return time.Unix(1790769600, 0) }
	return client
}
func assertNativeRoutingCode(t *testing.T, err error, code string) {
	t.Helper()
	var cause *NativeRoutingError
	if !errors.As(err, &cause) || cause.Code != code {
		t.Fatalf("routing code = %v, want %s", err, code)
	}
}

type nativeDiscoveryJoinedContext struct {
	context.Context
	onWait func()
}

func (c nativeDiscoveryJoinedContext) Done() <-chan struct{} {
	c.onWait()
	return c.Context.Done()
}

// resolve consults a joiner's Done channel only after finding the in-flight
// call. This signal lets cancellation tests prove enrollment without sleeps.
func nativeDiscoveryJoinContext(ctx context.Context) (context.Context, <-chan struct{}) {
	joined := make(chan struct{})
	return nativeDiscoveryJoinedContext{Context: ctx, onWait: sync.OnceFunc(func() { close(joined) })}, joined
}

func TestNativeDiscoveryPinnedReceiverArtifacts(t *testing.T) {
	pins := map[string]string{
		"native-discovery-success.json":           "82fbc6518a5e8e825879243c45a86e6e66f44323a6dcd3ad8280c8109aa671ec",
		"native-discovery-success-receipt.json":   "d56ccaaef70693c5df168a7ae1c8c854f0c504c331d551d4273000fdac6c246d",
		"native-discovery-forbidden.json":         "3208d206e45cde22446e0e227c4882bdacb97f2a4926c0341a13580c23e48b9c",
		"native-discovery-forbidden-receipt.json": "2b63d132df8b304286ebb0e9b4ab967a317544e8128f9105c7d71a138721528f",
		"native-wire-identity.json":               "b789d6fb7782e34ece3b516139856f2b6d2dea0543436820eefa50ff31355538",
	}
	for name, pin := range pins {
		body, err := os.ReadFile("contracts/native/atlas/" + name)
		if err != nil {
			t.Fatal(err)
		}
		sum := sha256.Sum256(body)
		if hex.EncodeToString(sum[:]) != pin {
			t.Fatalf("receiver artifact changed: %s", name)
		}
	}
	_, fixture := nativeDiscoveryFixture(t)
	if fixture.Scope.Shard != DecimalU64(math.MaxUint64) || fixture.Authority.Generation != DecimalU64(math.MaxUint64) {
		t.Fatal("actual fixture lost full u64")
	}
	if err := fixture.validate(fixture.Scope.documentScope()); err != nil {
		t.Fatal(err)
	}
}

func TestNativeDiscoveryCapturesCanonicalKeyWithoutAmbientHeaders(t *testing.T) {
	body, fixture := nativeDiscoveryFixture(t)
	scope := fixture.Scope.documentScope()
	providerCalls, managementCalls, atlasCalls, dataCalls := 0, 0, 0, 0
	jar, err := cookiejar.New(nil)
	if err != nil {
		t.Fatal(err)
	}
	for _, host := range []string{"atlas", "gru"} {
		origin, _ := url.Parse("https://" + host + ".api.terrascale.tech")
		jar.SetCookies(origin, []*http.Cookie{{Name: "ambient", Value: "cookie"}})
	}
	transport := pinningRoundTripperFunc(func(r *http.Request) (*http.Response, error) {
		if r.Header.Get("Authorization") != "Bearer "+nativeDiscoveryTestKey(5) {
			t.Error("captured native key changed")
		}
		for _, name := range []string{"Cookie", "X-TerraScale-Tenant-ID", "X-TerraScale-Project-ID", "X-TerraScale-Session", "X-TerraScale-Preferred-Region", "Idempotency-Key"} {
			if r.Header.Get(name) != "" {
				t.Errorf("ambient native header: %s", name)
			}
		}
		if r.URL.Host == "atlas.terrascale.tech" {
			atlasCalls++
			if r.Method != http.MethodGet || r.URL.Path != "/api/v1/discovery" || len(r.URL.Query()) != 3 || r.URL.Query().Get("tenant_id") != string(scope.Tenant) || r.URL.Query().Get("database_id") != string(scope.Database) || r.URL.Query().Get("shard_id") != "18446744073709551615" {
				t.Errorf("wrong canonical discovery request: %s", r.URL)
			}
			return nativeDiscoveryResponse(200, body), nil
		}
		dataCalls++
		if r.URL.Host != "gru.api.terrascale.tech" || r.URL.Path != scope.path()+"/collections/"+string(nativeTestCollection)+"/documents/"+string(nativeTestDocument) {
			t.Errorf("wrong direct native request: %s", r.URL)
		}
		return nativeDiscoveryResponse(200, []byte(nativeAbsentPoint)), nil
	})
	client, err := NewClient(Config{
		HTTPClient: &http.Client{Transport: transport, Jar: jar}, DataAccessToken: nativeDiscoveryTestKey(7),
		DataTokenProvider: func() string { providerCalls++; return nativeDiscoveryTestKey(byte(4 + providerCalls)) },
		DiscoveryBaseURL:  "https://metadata.example.test", APIBaseURL: "https://product.example.test", TenantID: "ambient-tenant", ProjectID: "ambient-project", PreferredRegion: RegionFRA, SessionToken: "ambient-session", AccessToken: "management-static",
		TokenProvider: func() string { managementCalls++; return "management-provider" },
	})
	if err != nil {
		t.Fatal(err)
	}
	client.nativeRouter.now = func() time.Time { return time.Unix(fixture.PublishedAt, 0) }
	if _, err := client.Documents.Get(t.Context(), scope, nativeTestCollection, nativeTestDocument); err != nil {
		t.Fatal(err)
	}
	if providerCalls != 1 || managementCalls != 0 || atlasCalls != 1 || dataCalls != 1 {
		t.Fatalf("wrong request counts %d %d %d %d", providerCalls, managementCalls, atlasCalls, dataCalls)
	}
	if client.http.Jar != jar || client.nativeHTTP.Jar != nil {
		t.Fatal("native cookie isolation changed product client")
	}
}

func TestNativeDiscoveryRejectsMalformedCredentialsBeforeHTTP(t *testing.T) {
	_, fixture := nativeDiscoveryFixture(t)
	valid := nativeDiscoveryTestKey(5)
	for _, credential := range []string{"", "management", "Bearer " + valid, " " + valid, valid + " ", valid + "=", strings.Replace(valid, "tbk1", "TBK1", 1), "tbk1.AA.AA", valid + ".extra", valid + "\n"} {
		t.Run("credential", func(t *testing.T) {
			calls := 0
			client := nativeDiscoveryClient(t, pinningRoundTripperFunc(func(*http.Request) (*http.Response, error) { calls++; return nil, errors.New("unexpected") }))
			client.cfg.DataAccessToken = credential
			_, err := client.Documents.Get(t.Context(), fixture.Scope.documentScope(), nativeTestCollection, nativeTestDocument)
			assertNativeRoutingCode(t, err, "invalid_credential")
			if calls != 0 {
				t.Fatal("malformed credential reached HTTP")
			}
			if credential != "" && strings.Contains(err.Error(), credential) {
				t.Fatal("routing error reflects raw credential")
			}
		})
	}
}

func TestNativeDiscoveryClosedResponseValidation(t *testing.T) {
	body, fixture := nativeDiscoveryFixture(t)
	tests := map[string]func(map[string]any){
		"schema":              func(d map[string]any) { d["schema_version"] = 2 },
		"scope":               func(d map[string]any) { d["scope"].(map[string]any)["database_id"] = string(nativeTestDocument) },
		"numeric shard":       func(d map[string]any) { d["scope"].(map[string]any)["shard_id"] = 1 },
		"zero response shard": func(d map[string]any) { d["scope"].(map[string]any)["shard_id"] = "0" },
		"authority":           func(d map[string]any) { d["authority"].(map[string]any)["cell"] = "foreign.cell" },
		"generation overflow": func(d map[string]any) {
			d["authority"].(map[string]any)["publisher_generation"] = "18446744073709551616"
		},
		"revision leading zero": func(d map[string]any) { d["revision"] = "041" },
		"zero revision":         func(d map[string]any) { d["revision"] = "0" },
		"ttl zero":              func(d map[string]any) { d["refreshSeconds"] = 0 },
		"ttl eleven":            func(d map[string]any) { d["refreshSeconds"] = 11 },
		"fractional ttl":        func(d map[string]any) { d["refreshSeconds"] = 1.5 },
		"time overflow":         func(d map[string]any) { d["expires_at"] = 253402300800.0 },
		"time ordering":         func(d map[string]any) { d["expires_at"] = d["published_at"] },
		"empty endpoints":       func(d map[string]any) { d["endpoints"] = []any{} },
		"residency empty":       func(d map[string]any) { d["residency"] = []any{} },
		"residency duplicates":  func(d map[string]any) { d["residency"] = []any{"GRU", "GRU"} },
		"residency mismatch":    func(d map[string]any) { d["residency"] = []any{"SFO"} },
		"url alias": func(d map[string]any) {
			d["endpoints"].([]any)[0].(map[string]any)["url"] = "https://gru.terrascale.tech"
		},
		"url query": func(d map[string]any) {
			d["endpoints"].([]any)[0].(map[string]any)["url"] = "https://gru.api.terrascale.tech?"
		},
		"url unicode": func(d map[string]any) {
			d["endpoints"].([]any)[0].(map[string]any)["url"] = "https://gru。terrascale。tech"
		},
		"reserved region": func(d map[string]any) {
			e := d["endpoints"].([]any)[0].(map[string]any)
			e["region"] = "AUTH"
			e["url"] = "https://auth.terrascale.tech"
		},
		"priority": func(d map[string]any) { d["endpoints"].([]any)[0].(map[string]any)["priority"] = 2 },
		"writer": func(d map[string]any) {
			d["endpoints"].([]any)[0].(map[string]any)["security_writer"] = map[string]any{}
		},
		"catalog": func(d map[string]any) {
			d["endpoints"].([]any)[0].(map[string]any)["catalog"].(map[string]any)["digest"] = strings.Repeat("C", 64)
		},
		"duplicate endpoints": func(d map[string]any) { e := d["endpoints"].([]any); d["endpoints"] = append(e, e[0]) },
		"unknown root":        func(d map[string]any) { d["legacy_database"] = "name" },
	}
	for _, capability := range []string{"conditional_mutation", "atomic_groups", "security_state"} {
		tests[capability] = func(d map[string]any) {
			d["endpoints"].([]any)[0].(map[string]any)["capabilities"].(map[string]any)[capability] = true
		}
	}
	for _, name := range []string{"schema_version", "scope", "authority", "revision", "published_at", "expires_at", "refreshSeconds", "residency", "endpoints"} {
		tests["missing "+name] = func(d map[string]any) { delete(d, name) }
	}
	for _, name := range []string{"client_v1", "read", "query", "subscriptions", "write", "conditional_mutation", "atomic_groups", "security_state"} {
		tests["missing cap "+name] = func(d map[string]any) {
			delete(d["endpoints"].([]any)[0].(map[string]any)["capabilities"].(map[string]any), name)
		}
	}
	for name, mutate := range tests {
		t.Run(name, func(t *testing.T) {
			var d map[string]any
			if err := json.Unmarshal(body, &d); err != nil {
				t.Fatal(err)
			}
			mutate(d)
			changed, err := json.Marshal(d)
			if err != nil {
				t.Fatal(err)
			}
			client := nativeDiscoveryClient(t, pinningRoundTripperFunc(func(r *http.Request) (*http.Response, error) {
				if r.URL.Host != "atlas.terrascale.tech" {
					t.Fatal("invalid response dispatched data")
				}
				return nativeDiscoveryResponse(200, changed), nil
			}))
			_, err = client.Documents.Get(t.Context(), fixture.Scope.documentScope(), nativeTestCollection, nativeTestDocument)
			assertNativeRoutingCode(t, err, "invalid_discovery")
		})
	}
	malformed := [][]byte{
		bytes.Replace(body, []byte(`"schema_version":1`), []byte(`"schema_version":1,"schema_version":1`), 1),
		append(bytes.Clone(body), []byte(` {}`)...),
		bytes.Replace(body, []byte(`"publisher-a"`), []byte(`"\ud800"`), 1),
		bytes.Replace(body, []byte(`"publisher-a"`), []byte{'"', 0xff, '"'}, 1),
	}
	for _, changed := range malformed {
		client := nativeDiscoveryClient(t, pinningRoundTripperFunc(func(*http.Request) (*http.Response, error) { return nativeDiscoveryResponse(200, changed), nil }))
		_, err := client.Documents.Get(t.Context(), fixture.Scope.documentScope(), nativeTestCollection, nativeTestDocument)
		assertNativeRoutingCode(t, err, "invalid_discovery")
	}
}

func TestNativeDiscoveryTTLIsClampedAtRequestStart(t *testing.T) {
	_, fixture := nativeDiscoveryFixture(t)
	for _, test := range []struct {
		name      string
		ttl       uint8
		expiry    int64
		latency   time.Duration
		remaining time.Duration
		expired   bool
	}{
		{"one", 1, 9, 0, time.Second, false}, {"eight minus latency", 8, 9, 2 * time.Second, 6 * time.Second, false}, {"ten", 10, 10, 0, 10 * time.Second, false}, {"absolute clamp", 8, 3, time.Second, 2 * time.Second, false}, {"already expired", 1, 9, 2 * time.Second, 0, true},
	} {
		t.Run(test.name, func(t *testing.T) {
			document := fixture
			document.RefreshSeconds = test.ttl
			document.ExpiresAt = document.PublishedAt + test.expiry
			body, _ := json.Marshal(document)
			now := time.Unix(document.PublishedAt, 0)
			client := nativeDiscoveryClient(t, pinningRoundTripperFunc(func(*http.Request) (*http.Response, error) {
				now = now.Add(test.latency)
				return nativeDiscoveryResponse(200, body), nil
			}))
			client.nativeRouter.now = func() time.Time { return now }
			admission, err := client.nativeRouter.resolve(t.Context(), document.Scope.documentScope(), nativeDiscoveryTestKey(5), nativeRead)
			if test.expired {
				assertNativeRoutingCode(t, err, "discovery_expired")
				return
			}
			if err != nil {
				t.Fatal(err)
			}
			if admission.Lease.Deadline.Sub(now) != test.remaining {
				t.Fatalf("remaining lease=%v want%v", admission.Lease.Deadline.Sub(now), test.remaining)
			}
			now = admission.Lease.Deadline
			assertNativeRoutingCode(t, client.nativeRouter.assertCurrent(admission), "discovery_expired")
		})
	}
}

func TestNativeDiscoveryCredentialRotationRetainsObservedFloor(t *testing.T) {
	_, fixture := nativeDiscoveryFixture(t)
	fixture.Authority.Generation = 10
	fixture.Revision = 41
	now := time.Unix(fixture.PublishedAt, 0)
	calls := 0
	revision := DecimalU64(41)
	generation := DecimalU64(10)
	deployment := fixture.Authority.Deployment
	cell := fixture.Authority.Cell
	publisher := fixture.Authority.Publisher
	transport := pinningRoundTripperFunc(func(r *http.Request) (*http.Response, error) {
		calls++
		q := r.URL.Query()
		if calls == 1 && len(q) != 3 {
			t.Fatal("invented initial observed floor")
		}
		if calls > 1 && (q.Get("rev") != "41" || q.Get("observed_publisher_generation") != "10" || q.Get("observed_deployment_id") != fixture.Authority.Deployment || q.Get("observed_cell") != fixture.Authority.Cell) {
			t.Errorf("observed floor lost: %s", r.URL)
		}
		document := fixture
		document.Revision = revision
		document.Authority.Generation = generation
		document.Authority.Deployment = deployment
		document.Authority.Cell = cell
		document.Authority.Publisher = publisher
		document.PublishedAt = now.Unix()
		document.ExpiresAt = now.Unix() + 9
		body, _ := json.Marshal(document)
		return nativeDiscoveryResponse(200, body), nil
	})
	client := nativeDiscoveryClient(t, transport)
	client.nativeRouter.now = func() time.Time { return now }
	scope := fixture.Scope.documentScope()
	first, err := client.nativeRouter.resolve(t.Context(), scope, nativeDiscoveryTestKey(5), nativeRead)
	if err != nil {
		t.Fatal(err)
	}
	if _, err := client.nativeRouter.resolve(t.Context(), scope, nativeDiscoveryTestKey(5), nativeRead); err != nil || calls != 1 {
		t.Fatal("valid lease was not reused")
	}
	for _, change := range []func(){func() { revision = 40 }, func() { revision = 41; generation = 9 }, func() { generation = 10; cell = "fra" }, func() { cell = fixture.Authority.Cell; deployment = "foreign" }, func() { deployment = fixture.Authority.Deployment; publisher = "replacement" }} {
		change()
		_, err := client.nativeRouter.resolve(t.Context(), scope, nativeDiscoveryTestKey(byte(5+calls)), nativeRead)
		assertNativeRoutingCode(t, err, "routing_observation_regressed")
	}
	publisher = fixture.Authority.Publisher
	generation = 11
	revision = 42
	now = now.Add(11 * time.Second)
	newer, err := client.nativeRouter.resolve(t.Context(), scope, nativeDiscoveryTestKey(99), nativeRead)
	if err != nil {
		t.Fatal(err)
	}
	assertNativeRoutingCode(t, client.nativeRouter.assertCurrent(first), "discovery_expired")
	client.nativeRouter.mu.Lock()
	client.nativeRouter.leases = make(map[nativeDiscoveryKey]nativeDiscoveryLease)
	client.nativeRouter.mu.Unlock()
	if !newer.Lease.Deadline.After(now) || len(client.nativeRouter.floors) != 1 {
		t.Fatal("floor was evicted with leases")
	}
}

func TestNativeDiscoveryConcurrentRefreshAndCancellation(t *testing.T) {
	body, fixture := nativeDiscoveryFixture(t)
	entered := make(chan struct{})
	release := make(chan struct{})
	var calls atomic.Int32
	client := nativeDiscoveryClient(t, pinningRoundTripperFunc(func(r *http.Request) (*http.Response, error) {
		if calls.Add(1) == 1 {
			close(entered)
		}
		select {
		case <-release:
			return nativeDiscoveryResponse(200, body), nil
		case <-r.Context().Done():
			return nil, r.Context().Err()
		}
	}))
	scope := fixture.Scope.documentScope()
	var wg sync.WaitGroup
	errs := make(chan error, 20)
	wg.Add(1)
	go func() {
		defer wg.Done()
		_, err := client.nativeRouter.resolve(t.Context(), scope, nativeDiscoveryTestKey(5), nativeRead)
		errs <- err
	}()
	<-entered
	cancelled, cancel := context.WithCancel(t.Context())
	cancel()
	_, err := client.nativeRouter.resolve(cancelled, scope, nativeDiscoveryTestKey(5), nativeRead)
	assertNativeRoutingCode(t, err, "discovery_unavailable")
	for range 19 {
		wg.Add(1)
		go func() {
			defer wg.Done()
			_, err := client.nativeRouter.resolve(t.Context(), scope, nativeDiscoveryTestKey(5), nativeRead)
			errs <- err
		}()
	}
	close(release)
	wg.Wait()
	close(errs)
	for err := range errs {
		if err != nil {
			t.Fatal(err)
		}
	}
	if calls.Load() != 1 {
		t.Fatalf("refresh calls=%d", calls.Load())
	}
}

func TestNativeDiscoveryInitiatorCancellationDoesNotFailJoiners(t *testing.T) {
	body, fixture := nativeDiscoveryFixture(t)
	for _, stop := range []string{"cancel", "deadline"} {
		t.Run(stop, func(t *testing.T) {
			var initiator context.Context
			var cancel context.CancelFunc
			if stop == "deadline" {
				initiator, cancel = context.WithTimeout(t.Context(), time.Second)
			} else {
				initiator, cancel = context.WithCancel(t.Context())
			}
			t.Cleanup(cancel)
			entered := make(chan struct{})
			var calls atomic.Int32
			client := nativeDiscoveryClient(t, pinningRoundTripperFunc(func(r *http.Request) (*http.Response, error) {
				if calls.Add(1) == 1 {
					close(entered)
					<-r.Context().Done()
					return nil, r.Context().Err()
				}
				return nativeDiscoveryResponse(200, body), nil
			}))
			scope := fixture.Scope.documentScope()
			first := make(chan error, 1)
			go func() {
				_, err := client.nativeRouter.resolve(initiator, scope, nativeDiscoveryTestKey(5), nativeRead)
				first <- err
			}()
			<-entered
			const joiners = 3
			results := make(chan error, joiners)
			for range joiners {
				ctx, joined := nativeDiscoveryJoinContext(t.Context())
				go func() {
					_, err := client.nativeRouter.resolve(ctx, scope, nativeDiscoveryTestKey(5), nativeRead)
					results <- err
				}()
				<-joined
			}
			if stop == "cancel" {
				cancel()
			} else {
				<-initiator.Done()
			}
			if err := <-first; !errors.Is(err, initiator.Err()) {
				t.Fatalf("initiator lost its own cancellation: %v", err)
			}
			for range joiners {
				if err := <-results; err != nil {
					t.Fatalf("live joiner inherited initiator cancellation: %v", err)
				}
			}
			if calls.Load() != 2 || len(client.nativeRouter.calls) != 0 {
				t.Fatalf("retry was not one completed single-flight: calls=%d active=%d", calls.Load(), len(client.nativeRouter.calls))
			}
		})
	}
}

func TestNativeDiscoveryReplacementKeepsJoinedCallerDeadline(t *testing.T) {
	_, fixture := nativeDiscoveryFixture(t)
	entered, replacement := make(chan struct{}), make(chan struct{})
	var calls atomic.Int32
	var originalDeadline, replacementDeadline time.Time
	client := nativeDiscoveryClient(t, pinningRoundTripperFunc(func(r *http.Request) (*http.Response, error) {
		if calls.Add(1) == 1 {
			close(entered)
		} else {
			var ok bool
			replacementDeadline, ok = r.Context().Deadline()
			if !ok || !replacementDeadline.Equal(originalDeadline) {
				t.Error("replacement fetch renewed the joined caller's deadline")
			}
			close(replacement)
		}
		<-r.Context().Done()
		return nil, r.Context().Err()
	}))
	scope := fixture.Scope.documentScope()
	initiator, cancelInitiator := context.WithCancel(t.Context())
	t.Cleanup(cancelInitiator)
	first := make(chan error, 1)
	go func() {
		_, err := client.nativeRouter.resolve(initiator, scope, nativeDiscoveryTestKey(5), nativeRead)
		first <- err
	}()
	<-entered
	ctx, cancelWaiter := context.WithTimeout(t.Context(), time.Second)
	t.Cleanup(cancelWaiter)
	originalDeadline, _ = ctx.Deadline()
	waiter, joined := nativeDiscoveryJoinContext(ctx)
	second := make(chan error, 1)
	go func() {
		_, err := client.nativeRouter.resolve(waiter, scope, nativeDiscoveryTestKey(5), nativeRead)
		second <- err
	}()
	<-joined
	cancelInitiator()
	select {
	case <-replacement:
	case <-ctx.Done():
		select {
		case <-replacement:
		default:
			t.Fatal("joined caller did not start a replacement before its deadline")
		}
	}
	if err := <-first; !errors.Is(err, context.Canceled) {
		t.Fatalf("initiator lost its own cancellation: %v", err)
	}
	if err := <-second; !errors.Is(err, ctx.Err()) || !errors.Is(err, context.DeadlineExceeded) {
		t.Fatalf("replacement lost the joined caller's original deadline: %v", err)
	}
	if calls.Load() != 2 || !replacementDeadline.Equal(originalDeadline) || len(client.nativeRouter.calls) != 0 || len(client.nativeRouter.leases) != 0 || len(client.nativeRouter.floors) != 0 {
		t.Fatalf("expired replacement retained work or admission: calls=%d active=%d leases=%d floors=%d", calls.Load(), len(client.nativeRouter.calls), len(client.nativeRouter.leases), len(client.nativeRouter.floors))
	}
}

func TestNativeDiscoveryJoinedCallerCancellationDoesNotCancelFetch(t *testing.T) {
	body, fixture := nativeDiscoveryFixture(t)
	entered, release := make(chan struct{}), make(chan struct{})
	var calls atomic.Int32
	client := nativeDiscoveryClient(t, pinningRoundTripperFunc(func(r *http.Request) (*http.Response, error) {
		if calls.Add(1) == 1 {
			close(entered)
		}
		select {
		case <-release:
			return nativeDiscoveryResponse(200, body), nil
		case <-r.Context().Done():
			return nil, r.Context().Err()
		}
	}))
	scope := fixture.Scope.documentScope()
	first := make(chan error, 1)
	go func() {
		_, err := client.nativeRouter.resolve(t.Context(), scope, nativeDiscoveryTestKey(5), nativeRead)
		first <- err
	}()
	<-entered
	ctx, cancel := context.WithCancel(t.Context())
	t.Cleanup(cancel)
	waiter, joined := nativeDiscoveryJoinContext(ctx)
	second := make(chan error, 1)
	go func() {
		_, err := client.nativeRouter.resolve(waiter, scope, nativeDiscoveryTestKey(5), nativeRead)
		second <- err
	}()
	<-joined
	cancel()
	if err := <-second; !errors.Is(err, context.Canceled) {
		t.Fatalf("joined caller lost its own cancellation: %v", err)
	}
	close(release)
	if err := <-first; err != nil {
		t.Fatalf("joined caller canceled the initiating fetch: %v", err)
	}
	if calls.Load() != 1 || len(client.nativeRouter.calls) != 0 {
		t.Fatalf("joined cancellation changed the fetch: calls=%d active=%d", calls.Load(), len(client.nativeRouter.calls))
	}
}

func TestNativeDiscoveryInitiatorCancellationRetryRetainsFloor(t *testing.T) {
	_, fixture := nativeDiscoveryFixture(t)
	fixture.Authority.Generation, fixture.Revision = 10, 41
	body, err := json.Marshal(fixture)
	if err != nil {
		t.Fatal(err)
	}
	now := time.Unix(fixture.PublishedAt, 0)
	regressed := fixture
	regressed.Revision = 40
	regressed.PublishedAt, regressed.ExpiresAt = now.Unix()+11, now.Unix()+20
	regressedBody, err := json.Marshal(regressed)
	if err != nil {
		t.Fatal(err)
	}
	entered := make(chan struct{})
	var calls atomic.Int32
	client := nativeDiscoveryClient(t, pinningRoundTripperFunc(func(r *http.Request) (*http.Response, error) {
		call := calls.Add(1)
		if call == 1 {
			return nativeDiscoveryResponse(200, body), nil
		}
		if q := r.URL.Query(); q.Get("rev") != "41" || q.Get("observed_publisher_generation") != "10" || q.Get("observed_deployment_id") != fixture.Authority.Deployment || q.Get("observed_cell") != fixture.Authority.Cell {
			t.Error("canceled fetch or its replacement lost the retained floor")
		}
		if call == 2 {
			close(entered)
			<-r.Context().Done()
			return nil, r.Context().Err()
		}
		return nativeDiscoveryResponse(200, regressedBody), nil
	}))
	client.nativeRouter.now = func() time.Time { return now }
	scope := fixture.Scope.documentScope()
	if _, err := client.nativeRouter.resolve(t.Context(), scope, nativeDiscoveryTestKey(5), nativeRead); err != nil {
		t.Fatal(err)
	}
	now = now.Add(11 * time.Second)
	initiator, cancel := context.WithCancel(t.Context())
	t.Cleanup(cancel)
	first := make(chan error, 1)
	go func() {
		_, err := client.nativeRouter.resolve(initiator, scope, nativeDiscoveryTestKey(5), nativeRead)
		first <- err
	}()
	<-entered
	waiter, joined := nativeDiscoveryJoinContext(t.Context())
	second := make(chan error, 1)
	go func() {
		_, err := client.nativeRouter.resolve(waiter, scope, nativeDiscoveryTestKey(5), nativeRead)
		second <- err
	}()
	<-joined
	cancel()
	if err := <-first; !errors.Is(err, context.Canceled) {
		t.Fatalf("initiator lost its own cancellation: %v", err)
	}
	assertNativeRoutingCode(t, <-second, "routing_observation_regressed")
	if calls.Load() != 3 || client.nativeRouter.floors[scope].Revision != 41 || len(client.nativeRouter.calls) != 0 {
		t.Fatalf("retry changed the floor or retained a fetch: calls=%d floor=%d active=%d", calls.Load(), client.nativeRouter.floors[scope].Revision, len(client.nativeRouter.calls))
	}
}

func TestNativeDiscoveryTransportDeadlineIsNotCallerCancellation(t *testing.T) {
	_, fixture := nativeDiscoveryFixture(t)
	entered, release := make(chan struct{}), make(chan struct{})
	var calls atomic.Int32
	client := nativeDiscoveryClient(t, pinningRoundTripperFunc(func(r *http.Request) (*http.Response, error) {
		if calls.Add(1) == 1 {
			close(entered)
		}
		select {
		case <-release:
			return nil, context.DeadlineExceeded
		case <-r.Context().Done():
			return nil, r.Context().Err()
		}
	}))
	scope := fixture.Scope.documentScope()
	first := make(chan error, 1)
	go func() {
		_, err := client.nativeRouter.resolve(t.Context(), scope, nativeDiscoveryTestKey(5), nativeRead)
		first <- err
	}()
	<-entered
	waiter, joined := nativeDiscoveryJoinContext(t.Context())
	second := make(chan error, 1)
	go func() {
		_, err := client.nativeRouter.resolve(waiter, scope, nativeDiscoveryTestKey(5), nativeRead)
		second <- err
	}()
	<-joined
	close(release)
	for _, result := range []<-chan error{first, second} {
		if err := <-result; !errors.Is(err, context.DeadlineExceeded) {
			t.Fatalf("transport deadline changed: %v", err)
		}
	}
	if calls.Load() != 1 || len(client.nativeRouter.calls) != 0 || t.Context().Err() != nil {
		t.Fatalf("independent transport timeout retried: calls=%d active=%d", calls.Load(), len(client.nativeRouter.calls))
	}
}

func TestNativeDiscoveryFloorCapacityAndDispatchRevalidation(t *testing.T) {
	body, fixture := nativeDiscoveryFixture(t)
	scope := fixture.Scope.documentScope()
	calls := 0
	client := nativeDiscoveryClient(t, pinningRoundTripperFunc(func(*http.Request) (*http.Response, error) { calls++; return nativeDiscoveryResponse(200, body), nil }))
	admission, err := client.nativeRouter.resolve(t.Context(), scope, nativeDiscoveryTestKey(5), nativeRead)
	if err != nil {
		t.Fatal(err)
	}
	client.nativeRouter.mu.Lock()
	floor := client.nativeRouter.floors[scope]
	floor.Revision++
	client.nativeRouter.floors[scope] = floor
	client.nativeRouter.mu.Unlock()
	assertNativeRoutingCode(t, client.nativeRouter.assertCurrent(admission), "routing_observation_regressed")
	for i := range nativeDiscoveryCacheLimit - 1 {
		client.nativeRouter.floors[DocumentScope{Tenant: scope.Tenant, Database: scope.Database, Shard: DecimalU64(i)}] = floor
	}
	_, err = client.nativeRouter.resolve(t.Context(), DocumentScope{Tenant: scope.Tenant, Database: scope.Database, Shard: 1000}, nativeDiscoveryTestKey(5), nativeRead)
	assertNativeRoutingCode(t, err, "routing_capacity")
	if calls != 1 || len(client.nativeRouter.floors) != nativeDiscoveryCacheLimit {
		t.Fatal("floor capacity widened or evicted")
	}
}

func TestNativeDiscoveryHonorsActualErrorsAndNoRedirects(t *testing.T) {
	body, fixture := nativeDiscoveryFixture(t)
	forbidden, err := os.ReadFile("contracts/native/atlas/native-discovery-forbidden.json")
	if err != nil {
		t.Fatal(err)
	}
	for _, test := range []struct {
		name   string
		status int
		body   []byte
		media  string
		length int64
		code   string
	}{
		{"actual forbidden", 403, forbidden, "application/json", 0, "discovery_forbidden"},
		{"actual no eligible", 503, []byte(`{"error":{"code":"no_eligible_region"}}`), "application/json", 0, "no_eligible_region"},
		{"redirect", 307, nil, "application/json", 0, ""},
		{"wrong media", 200, body, "text/html", 0, "invalid_discovery"},
		{"declared limit", 200, body, "application/json", nativeDiscoveryBodyLimit + 1, "invalid_discovery"},
		{"actual limit", 200, bytes.Repeat([]byte{' '}, nativeDiscoveryBodyLimit+1), "application/json", -1, "invalid_discovery"},
	} {
		t.Run(test.name, func(t *testing.T) {
			calls := 0
			client := nativeDiscoveryClient(t, pinningRoundTripperFunc(func(r *http.Request) (*http.Response, error) {
				calls++
				if r.URL.Host != "atlas.terrascale.tech" {
					t.Fatal("denied discovery dispatched data")
				}
				response := nativeDiscoveryResponse(test.status, test.body)
				response.Header.Set("Content-Type", test.media)
				response.Header.Set("Location", "https://auth.terrascale.tech")
				response.ContentLength = test.length
				return response, nil
			}))
			_, err := client.Documents.Get(t.Context(), fixture.Scope.documentScope(), nativeTestCollection, nativeTestDocument)
			if err == nil || calls != 1 {
				t.Fatalf("discovery denial/redirect accepted: %v calls%d", err, calls)
			}
			if test.status != 200 {
				var actual *APIError
				if !errors.As(err, &actual) || actual.Status != test.status || test.code != "" && actual.Code != test.code {
					t.Fatalf("actual Atlas error lost: %v", err)
				}
			} else {
				assertNativeRoutingCode(t, err, test.code)
			}
		})
	}
}

func TestNativeDiscoveryOperationCapabilitiesAndSourcePriority(t *testing.T) {
	_, fixture := nativeDiscoveryFixture(t)
	scope := fixture.Scope.documentScope()
	first := fixture.Endpoints[0]
	first.Capabilities.Query = false
	first.Capabilities.Subscriptions = false
	first.Capabilities.Write = false
	second := fixture.Endpoints[0]
	second.Region = "XYZ-9"
	second.URL = "https://xyz-9.api.terrascale.tech"
	second.Priority = 2
	fixture.Endpoints = []nativeDiscoveryEndpoint{first, second}
	body, _ := json.Marshal(fixture)
	atlasCalls := 0
	var hosts []string
	var statusRequest TransactionStatusRequest
	client := nativeDiscoveryClient(t, pinningRoundTripperFunc(func(r *http.Request) (*http.Response, error) {
		if r.URL.Host == "atlas.terrascale.tech" {
			atlasCalls++
			return nativeDiscoveryResponse(200, body), nil
		}
		hosts = append(hosts, r.URL.Host)
		switch {
		case strings.HasSuffix(r.URL.Path, "/query"):
			return nativeDiscoveryResponse(200, []byte(`{"version":1,"rows":[]}`)), nil
		case strings.HasSuffix(r.URL.Path, "/subscriptions"):
			payload, _ := io.ReadAll(r.Body)
			if bytes.Contains(payload, []byte(`"phase":"poll"`)) {
				return nativeDiscoveryResponse(200, []byte(`{"version":1,"phase":"poll","groups":[],"poll_cursor":"YQ"}`)), nil
			}
			return nativeDiscoveryResponse(200, []byte(`{"version":1,"phase":"snapshot","rows":[],"poll_cursor":"YQ"}`)), nil
		case strings.HasSuffix(r.URL.Path, "/status"):
			payload, _ := io.ReadAll(r.Body)
			if err := decodeNativeJSON(payload, &statusRequest); err != nil {
				t.Error(err)
			}
			result := TransactionStatusResponse{Version: 1, State: TransactionUnknown, TransactionID: statusRequest.TransactionID, Authority: statusRequest.Authority, EffectDigest: statusRequest.EffectDigest}
			encoded, _ := json.Marshal(result)
			return nativeDiscoveryResponse(200, encoded), nil
		default:
			return nativeDiscoveryResponse(200, []byte(nativeAbsentPoint)), nil
		}
	}))
	if _, err := client.Documents.Get(t.Context(), scope, nativeTestCollection, nativeTestDocument); err != nil {
		t.Fatal(err)
	}
	predicate := QueryPredicate{Operation: QueryIsMissing, Path: []uint32{1}}
	if _, err := client.Documents.Query(t.Context(), scope, nativeTestCollection, QueryRequest{Version: 1, Predicate: predicate, Limit: 1}); err != nil {
		t.Fatal(err)
	}
	if _, err := client.Documents.SubscriptionSnapshot(t.Context(), scope, nativeTestCollection, SubscriptionSnapshotRequest{Version: 1, Phase: "snapshot", Predicate: predicate, Limit: 1}); err != nil {
		t.Fatal(err)
	}
	if _, err := client.Documents.SubscriptionPoll(t.Context(), scope, nativeTestCollection, SubscriptionPollRequest{Version: 1, Phase: "poll", Predicate: predicate, PollCursor: "YQ"}); err != nil {
		t.Fatal(err)
	}
	write := nativeTestWrite()
	request, err := NewCommitRequest(scope, nativeTestID, MutationAuthority{Mode: AuthorityLeaderless}, []DocumentWrite{write})
	if err != nil {
		t.Fatal(err)
	}
	operation, err := client.Documents.PrepareTransaction(scope, request)
	if err != nil {
		t.Fatal(err)
	}
	// Reconciliation requires WRITE, even when the original conditional
	// mutation is currently unavailable. It does not require the old flags.
	if result, err := operation.Status(t.Context()); err != nil || result.Definitive() {
		t.Fatalf("status reconciliation failed: %v", err)
	}
	if statusRequest.EffectDigest != request.EffectDigest || statusRequest.TransactionID != request.TransactionID {
		t.Fatal("status changed retained identity")
	}
	if atlasCalls != 1 || len(hosts) != 5 || hosts[0] != "gru.api.terrascale.tech" {
		t.Fatalf("wrong source priority/calls: %v %d", hosts, atlasCalls)
	}
	for _, host := range hosts[1:] {
		if host != "xyz-9.api.terrascale.tech" {
			t.Fatalf("capability-ineligible region selected: %s", host)
		}
	}
	before := len(hosts)
	_, err = operation.Commit(t.Context())
	assertNativeRoutingCode(t, err, "capability_unavailable")
	write.Condition.Kind = ConditionNone
	other := write
	other.Document = nativeTestCollection
	multi, err := NewCommitRequest(scope, nativeTestID, MutationAuthority{Mode: AuthorityLeaderless}, []DocumentWrite{other, write})
	if err != nil {
		t.Fatal(err)
	}
	_, err = client.Documents.Commit(t.Context(), scope, multi)
	assertNativeRoutingCode(t, err, "capability_unavailable")
	if len(hosts) != before {
		t.Fatal("advanced commit reached native HTTP")
	}
}

func TestNativeDiscoveryStatusRequiresWriteAndSubscriptionRequiresRead(t *testing.T) {
	_, fixture := nativeDiscoveryFixture(t)
	scope := fixture.Scope.documentScope()
	for _, test := range []struct {
		name      string
		caps      nativeDiscoveryCapabilities
		operation string
	}{
		{"read-only status", nativeDiscoveryCapabilities{ClientV1: true, Read: true, Query: true, Subscriptions: true}, "status"},
		{"write-only snapshot", nativeDiscoveryCapabilities{ClientV1: true, Write: true, Subscriptions: true}, "snapshot"},
		{"write-only point", nativeDiscoveryCapabilities{ClientV1: true, Write: true}, "point"},
	} {
		t.Run(test.name, func(t *testing.T) {
			document := fixture
			document.Endpoints = append([]nativeDiscoveryEndpoint(nil), fixture.Endpoints...)
			document.Endpoints[0].Capabilities = test.caps
			body, _ := json.Marshal(document)
			calls := 0
			client := nativeDiscoveryClient(t, pinningRoundTripperFunc(func(r *http.Request) (*http.Response, error) {
				calls++
				if r.URL.Host != "atlas.terrascale.tech" {
					t.Fatal("capability-ineligible dispatch")
				}
				return nativeDiscoveryResponse(200, body), nil
			}))
			var err error
			switch test.operation {
			case "status":
				request, _ := NewCommitRequest(scope, nativeTestID, MutationAuthority{Mode: AuthorityLeaderless}, []DocumentWrite{nativeTestWrite()})
				_, err = client.Documents.TransactionStatus(t.Context(), scope, request.StatusRequest())
			case "snapshot":
				_, err = client.Documents.SubscriptionSnapshot(t.Context(), scope, nativeTestCollection, SubscriptionSnapshotRequest{Version: 1, Phase: "snapshot", Predicate: QueryPredicate{Operation: QueryIsMissing, Path: []uint32{1}}, Limit: 1})
			default:
				_, err = client.Documents.Get(t.Context(), scope, nativeTestCollection, nativeTestDocument)
			}
			assertNativeRoutingCode(t, err, "capability_unavailable")
			if calls != 1 {
				t.Fatalf("unexpected calls%d", calls)
			}
		})
	}
}

func TestNativeDiscoveryCommitPreservesUncertaintyAndNeverReplays(t *testing.T) {
	body, fixture := nativeDiscoveryFixture(t)
	scope := fixture.Scope.documentScope()
	atlasCalls, dataCalls := 0, 0
	var payload []byte
	client := nativeDiscoveryClient(t, pinningRoundTripperFunc(func(r *http.Request) (*http.Response, error) {
		if r.URL.Host == "atlas.terrascale.tech" {
			atlasCalls++
			return nativeDiscoveryResponse(200, body), nil
		}
		dataCalls++
		data, _ := io.ReadAll(r.Body)
		if payload == nil {
			payload = bytes.Clone(data)
		} else if !bytes.Equal(payload, data) {
			t.Error("explicit retry changed frozen bytes")
		}
		return nativeDiscoveryResponse(503, []byte(`{"error":{"code":"commit_uncertain"}}`)), nil
	}))
	write := nativeTestWrite()
	write.Condition.Kind = ConditionNone
	request, err := NewCommitRequest(scope, nativeTestID, MutationAuthority{Mode: AuthorityLeaderless}, []DocumentWrite{write})
	if err != nil {
		t.Fatal(err)
	}
	_, err = client.Documents.Commit(t.Context(), scope, request)
	var uncertain *CommitError
	if !errors.As(err, &uncertain) || !uncertain.Uncertain || uncertain.Operation == nil {
		t.Fatalf("uncertainty lost: %v", err)
	}
	if atlasCalls != 1 || dataCalls != 1 {
		t.Fatal("automatic commit replay")
	}
	request.Writes[0].Value.Fields[0].Value = BoolValue(false)
	request.TransactionID.Nonce[0] ^= 1
	_, err = uncertain.Operation.Retry(t.Context())
	if !errors.As(err, &uncertain) || dataCalls != 2 || atlasCalls != 1 {
		t.Fatal("explicit frozen retry failed")
	}
	client.cfg.DataAccessToken = ""
	_, err = uncertain.Operation.Retry(t.Context())
	var routing *RoutingError
	if !errors.As(err, &routing) || errors.As(err, &uncertain) || dataCalls != 2 {
		t.Fatalf("pre-dispatch Atlas failure became uncertain: %v", err)
	}
}

func TestNativeDiscoveryLeaseEvictionPreservesFloor(t *testing.T) {
	body, fixture := nativeDiscoveryFixture(t)
	scope := fixture.Scope.documentScope()
	client := nativeDiscoveryClient(t, pinningRoundTripperFunc(func(*http.Request) (*http.Response, error) { return nativeDiscoveryResponse(200, body), nil }))
	admission, err := client.nativeRouter.resolve(t.Context(), scope, nativeDiscoveryTestKey(5), nativeRead)
	if err != nil {
		t.Fatal(err)
	}
	client.nativeRouter.mu.Lock()
	clear(client.nativeRouter.leases)
	var oldest nativeDiscoveryKey
	for i := range nativeDiscoveryCacheLimit {
		key := nativeDiscoveryKey{Scope: scope, Credential: sha256.Sum256([]byte{byte(i)})}
		lease := admission.Lease
		lease.Used = uint64(i + 1)
		client.nativeRouter.leases[key] = lease
		if i == 0 {
			oldest = key
		}
	}
	client.nativeRouter.serial = nativeDiscoveryCacheLimit
	client.nativeRouter.mu.Unlock()
	_, err = client.nativeRouter.resolve(t.Context(), scope, nativeDiscoveryTestKey(6), nativeRead)
	if err != nil {
		t.Fatal(err)
	}
	if len(client.nativeRouter.leases) != nativeDiscoveryCacheLimit || len(client.nativeRouter.floors) != 1 {
		t.Fatal("cache bounds/floor violated")
	}
	if _, exists := client.nativeRouter.leases[oldest]; exists {
		t.Fatal("lease eviction did not select oldest")
	}
}

func TestNativeCommitCanceledAfterAdmissionNeverDispatches(t *testing.T) {
	body, fixture := nativeDiscoveryFixture(t)
	scope := fixture.Scope.documentScope()
	calls := 0
	client := nativeDiscoveryClient(t, pinningRoundTripperFunc(func(r *http.Request) (*http.Response, error) {
		calls++
		if r.URL.Host != "atlas.terrascale.tech" {
			t.Fatal("canceled commit reached native transport")
		}
		return nativeDiscoveryResponse(200, body), nil
	}))
	if _, err := client.nativeRouter.resolve(t.Context(), scope, client.nativeToken(), nativeWrite); err != nil {
		t.Fatal(err)
	}
	ctx, cancel := context.WithCancel(t.Context())
	defer cancel()
	now := client.nativeRouter.now()
	clockReads := 0
	client.nativeRouter.now = func() time.Time {
		clockReads++
		if clockReads == 2 { // cached resolution, then the final admission check
			cancel()
		}
		return now
	}
	write := nativeTestWrite()
	write.Condition.Kind = ConditionNone
	request, err := NewCommitRequest(scope, nativeTestID, MutationAuthority{Mode: AuthorityLeaderless}, []DocumentWrite{write})
	if err != nil {
		t.Fatal(err)
	}
	_, err = client.Documents.Commit(ctx, scope, request)
	var commit *CommitError
	if !errors.Is(err, context.Canceled) || !errors.As(err, &commit) || commit.Uncertain || calls != 1 {
		t.Fatalf("pre-send cancellation lost certainty: %v calls=%d", err, calls)
	}
}

func TestNativeDiscoverySharedRequestTimeout(t *testing.T) {
	body, fixture := nativeDiscoveryFixture(t)
	var discoveryDeadline time.Time
	calls := 0
	client := nativeDiscoveryClient(t, pinningRoundTripperFunc(func(r *http.Request) (*http.Response, error) {
		calls++
		deadline, ok := r.Context().Deadline()
		if !ok {
			t.Fatal("request has no deadline")
		}
		if r.URL.Host == "atlas.terrascale.tech" {
			discoveryDeadline = deadline
			return nativeDiscoveryResponse(200, body), nil
		}
		if !deadline.Equal(discoveryDeadline) {
			t.Error("native dispatch renewed the discovery timeout")
		}
		<-r.Context().Done()
		return nil, r.Context().Err()
	}))
	client.cfg.RequestTimeout = 20 * time.Millisecond
	_, err := client.Documents.Get(t.Context(), fixture.Scope.documentScope(), nativeTestCollection, nativeTestDocument)
	if !errors.Is(err, context.DeadlineExceeded) || calls != 2 {
		t.Fatalf("timeout not shared: %v calls%d", err, calls)
	}
}

func TestNativeDiscoveryConcurrentOlderObservationCannotDispatch(t *testing.T) {
	_, fixture := nativeDiscoveryFixture(t)
	scope := fixture.Scope.documentScope()
	fixture.Authority.Generation = 10
	entered := make(chan struct{})
	release := make(chan struct{})
	result := make(chan error, 1)
	client := nativeDiscoveryClient(t, pinningRoundTripperFunc(func(r *http.Request) (*http.Response, error) {
		document := fixture
		if r.Header.Get("Authorization") == "Bearer "+nativeDiscoveryTestKey(5) {
			close(entered)
			<-release
		} else {
			document.Revision = 42
		}
		body, _ := json.Marshal(document)
		return nativeDiscoveryResponse(200, body), nil
	}))
	go func() {
		_, err := client.nativeRouter.resolve(t.Context(), scope, nativeDiscoveryTestKey(5), nativeRead)
		result <- err
	}()
	<-entered
	newer, err := client.nativeRouter.resolve(t.Context(), scope, nativeDiscoveryTestKey(6), nativeRead)
	if err != nil {
		t.Fatal(err)
	}
	close(release)
	assertNativeRoutingCode(t, <-result, "routing_observation_regressed")
	if err := client.nativeRouter.assertCurrent(newer); err != nil {
		t.Fatal(err)
	}
	if floor := client.nativeRouter.floors[scope]; floor.Revision != 42 {
		t.Fatal("late old response lowered floor")
	}
}

func TestNativeDiscoveryStatusAcceptsWriteOnlyCapabilityDownshift(t *testing.T) {
	_, fixture := nativeDiscoveryFixture(t)
	scope := fixture.Scope.documentScope()
	fixture.Endpoints[0].Capabilities = nativeDiscoveryCapabilities{ClientV1: true, Write: true}
	body, _ := json.Marshal(fixture)
	writes := []DocumentWrite{nativeTestWrite(), nativeTestWrite()}
	writes[1].Document = nativeTestCollection
	request, err := NewCommitRequest(scope, nativeTestID, MutationAuthority{Mode: AuthorityLeaderless}, writes)
	if err != nil {
		t.Fatal(err)
	}
	calls := 0
	client := nativeDiscoveryClient(t, pinningRoundTripperFunc(func(r *http.Request) (*http.Response, error) {
		calls++
		if r.URL.Host == "atlas.terrascale.tech" {
			return nativeDiscoveryResponse(200, body), nil
		}
		if !strings.HasSuffix(r.URL.Path, "/transactions/status") {
			t.Fatal("downshifted native mutation reached data")
		}
		result := TransactionStatusResponse{Version: 1, State: TransactionUnknown, TransactionID: request.TransactionID, Authority: request.Authority, EffectDigest: request.EffectDigest}
		encoded, _ := json.Marshal(result)
		return nativeDiscoveryResponse(200, encoded), nil
	}))
	operation, err := client.Documents.PrepareTransaction(scope, request)
	if err != nil {
		t.Fatal(err)
	}
	result, err := operation.Status(t.Context())
	if err != nil || result.Definitive() || calls != 2 {
		t.Fatalf("write-only status failed: %v", err)
	}
	_, err = operation.Commit(t.Context())
	assertNativeRoutingCode(t, err, "capability_unavailable")
	if calls != 2 {
		t.Fatal("downshifted commit reached data")
	}
}

func TestNativeDiscoveryGenericZeroRequestRetainsActualDenial(t *testing.T) {
	_, fixture := nativeDiscoveryFixture(t)
	scope := fixture.Scope.documentScope()
	scope.Shard = 0
	forbidden, err := os.ReadFile("contracts/native/atlas/native-discovery-forbidden.json")
	if err != nil {
		t.Fatal(err)
	}
	client := nativeDiscoveryClient(t, pinningRoundTripperFunc(func(r *http.Request) (*http.Response, error) {
		if r.URL.Query().Get("shard_id") != "0" {
			t.Fatal("generic shard zero changed")
		}
		return nativeDiscoveryResponse(403, forbidden), nil
	}))
	_, err = client.Documents.Get(t.Context(), scope, nativeTestCollection, nativeTestDocument)
	var actual *APIError
	if !errors.As(err, &actual) || actual.Status != 403 || actual.Code != "discovery_forbidden" {
		t.Fatalf("zero actual denial changed: %v", err)
	}
	fixture.Scope.Shard = 0
	body, _ := json.Marshal(fixture)
	client = nativeDiscoveryClient(t, pinningRoundTripperFunc(func(*http.Request) (*http.Response, error) { return nativeDiscoveryResponse(200, body), nil }))
	_, err = client.Documents.Get(t.Context(), scope, nativeTestCollection, nativeTestDocument)
	assertNativeRoutingCode(t, err, "invalid_discovery")
}

func TestNativeDiscoveryRejectsFutureAndOverlongPublications(t *testing.T) {
	_, fixture := nativeDiscoveryFixture(t)
	for _, test := range []struct {
		name            string
		publishedOffset int64
		expiresOffset   int64
		latency         time.Duration
		accepted        bool
	}{
		{name: "future", publishedOffset: 1, expiresOffset: 10},
		{name: "overlong", publishedOffset: -20, expiresOffset: 9},
		{name: "ten second interval", expiresOffset: 10, accepted: true},
		{name: "current at response decision", publishedOffset: 1, expiresOffset: 10, latency: 2 * time.Second, accepted: true},
	} {
		t.Run(test.name, func(t *testing.T) {
			document := fixture
			document.PublishedAt = fixture.PublishedAt + test.publishedOffset
			document.ExpiresAt = fixture.PublishedAt + test.expiresOffset
			body, err := json.Marshal(document)
			if err != nil {
				t.Fatal(err)
			}
			now := time.Unix(fixture.PublishedAt, 100_000_000)
			client := nativeDiscoveryClient(t, pinningRoundTripperFunc(func(r *http.Request) (*http.Response, error) {
				if r.URL.Host != "atlas.terrascale.tech" {
					t.Fatal("invalid publication reached native data")
				}
				now = now.Add(test.latency)
				return nativeDiscoveryResponse(200, body), nil
			}))
			client.nativeRouter.now = func() time.Time { return now }
			admission, err := client.nativeRouter.resolve(t.Context(), fixture.Scope.documentScope(), nativeDiscoveryTestKey(5), nativeRead)
			if !test.accepted {
				assertNativeRoutingCode(t, err, "invalid_discovery")
				if len(client.nativeRouter.floors) != 0 || len(client.nativeRouter.leases) != 0 {
					t.Fatal("invalid publication established a floor or lease")
				}
				return
			}
			if err != nil {
				t.Fatal(err)
			}
			// A wall-clock rollback cannot keep a publication that is now
			// in the future eligible, even within its monotonic lease.
			now = time.Unix(document.PublishedAt-1, 0)
			assertNativeRoutingCode(t, client.nativeRouter.assertCurrent(admission), "discovery_expired")
		})
	}
}

func TestNativeDiscoverySingleWriterStatusRequiresAdmittedFence(t *testing.T) {
	_, fixture := nativeDiscoveryFixture(t)
	scope := fixture.Scope.documentScope()
	request, err := NewCommitRequest(scope, nativeTestID, MutationAuthority{
		Mode: AuthoritySingleWriter, Generation: 1,
		Writer: &WriterIdentity{Replica: nativeTestCollection, Incarnation: Hex16{1}},
	}, []DocumentWrite{nativeTestWrite()})
	if err != nil {
		t.Fatal(err)
	}
	calls := 0
	client := nativeDiscoveryClient(t, pinningRoundTripperFunc(func(*http.Request) (*http.Response, error) {
		calls++
		return nativeDiscoveryResponse(503, []byte(`{"error":{"code":"authority_unavailable"}}`)), nil
	}))
	_, err = client.Documents.TransactionStatus(t.Context(), scope, request.StatusRequest())
	assertNativeRoutingCode(t, err, "capability_unavailable")
	operation, err := client.Documents.PrepareTransaction(scope, request)
	if err != nil {
		t.Fatal(err)
	}
	_, err = operation.Status(t.Context())
	assertNativeRoutingCode(t, err, "capability_unavailable")
	if calls != 0 {
		t.Fatal("SingleWriter status dispatched without discovered fenced writer authority")
	}
	// The explicit producer-preparation path keeps its prior server decision:
	// the current native producer rejects SingleWriter with this actual code.
	client.cfg.DataBaseURL = "https://gru.api.terrascale.tech"
	_, err = operation.Status(t.Context())
	var actual *APIError
	if !errors.As(err, &actual) || actual.Status != 503 || actual.Code != "authority_unavailable" || calls != 1 {
		t.Fatalf("explicit producer rejection changed: %v calls%d", err, calls)
	}
}
