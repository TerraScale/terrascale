package sdk

import (
	"encoding/json"
	"errors"
	"fmt"
	"io"
	"net/http"
	"net/http/httptest"
	"strconv"
	"strings"
	"sync"
	"sync/atomic"
	"testing"
	"time"
)

// discoveryEndpoint is "REGION|URL|comma,capabilities" so fixtures can point
// regions at loopback test servers.
func discoveryBody(database string, refreshSeconds int, endpoints ...string) []byte {
	type endpoint struct {
		Region       string   `json:"region"`
		URL          string   `json:"url"`
		Priority     int      `json:"priority"`
		Capabilities []string `json:"capabilities"`
	}
	entries := make([]endpoint, 0, len(endpoints))
	for i, raw := range endpoints {
		region, rest, _ := strings.Cut(raw, "|")
		url, capabilities, _ := strings.Cut(rest, "|")
		entries = append(entries, endpoint{
			Region:       region,
			URL:          url,
			Priority:     i + 1,
			Capabilities: strings.Split(capabilities, ","),
		})
	}
	doc := struct {
		V              int        `json:"v"`
		Database       string     `json:"database"`
		Branch         string     `json:"branch"`
		Revision       int        `json:"revision"`
		Updated        int64      `json:"updated"`
		Residency      any        `json:"residency"`
		Placement      string     `json:"placement"`
		RefreshSeconds int        `json:"refreshSeconds"`
		Endpoints      []endpoint `json:"endpoints"`
	}{V: 1, Database: database, Branch: "main", Revision: 7, Updated: 1725300000,
		Placement: "known", RefreshSeconds: refreshSeconds, Endpoints: entries}
	encoded, err := json.Marshal(doc)
	if err != nil {
		panic(err)
	}
	return encoded
}

// atlasServer is a loopback metadata origin recording the discovery and
// catalog requests it served.
type atlasServer struct {
	discoveryCalls atomic.Int32
	catalogCalls   atomic.Int32
	mu             sync.Mutex
	doc            []byte
	docStatus      int
	queries        []string
	rttHeaders     []string
}

func (a *atlasServer) setDocument(doc []byte, status int) {
	a.mu.Lock()
	a.doc, a.docStatus = doc, status
	a.mu.Unlock()
}

func (a *atlasServer) discoveryQueries() []string {
	a.mu.Lock()
	defer a.mu.Unlock()
	return append([]string(nil), a.queries...)
}

func (a *atlasServer) discoveryRttHeaders() []string {
	a.mu.Lock()
	defer a.mu.Unlock()
	return append([]string(nil), a.rttHeaders...)
}

func (a *atlasServer) server(t *testing.T) *httptest.Server {
	t.Helper()
	srv := httptest.NewServer(http.HandlerFunc(func(w http.ResponseWriter, r *http.Request) {
		switch r.URL.Path {
		case discoveryPath:
			a.discoveryCalls.Add(1)
			a.mu.Lock()
			a.queries = append(a.queries, r.URL.RawQuery)
			a.rttHeaders = append(a.rttHeaders, r.Header.Get(regionRttHeader))
			doc, status := a.doc, a.docStatus
			a.mu.Unlock()
			w.Header().Set("Content-Type", "application/json")
			w.WriteHeader(status)
			_, _ = w.Write(doc)
		case edgeRegionsPath:
			a.catalogCalls.Add(1)
			w.Header().Set("Content-Type", "application/json")
			_, _ = w.Write([]byte(`{"regions":[{"code":"GRU","name":"Sao Paulo","country":"BR","latitude":-23.5,"longitude":-46.6,"available":true}],"hosts":{"GRU":"https://gru.api.terrascale.tech"}}`))
		default:
			w.WriteHeader(http.StatusNotFound)
		}
	}))
	t.Cleanup(srv.Close)
	return srv
}

func TestParseRoutingDocumentValidatesStrictly(t *testing.T) {
	valid := discoveryBody("orders", 300, "FRA|https://fra.api.terrascale.tech|read,write,sql,reactive")
	if _, err := parseRoutingDocument(valid, "orders", "main", false); err != nil {
		t.Fatalf("valid document rejected: %v", err)
	}
	replacements := map[string][2]string{
		"version":         {`"v":1`, `"v":2`},
		"identity":        {`"orders"`, `"other"`},
		"unknown-field":   {`{`, `{"extra":1,`},
		"hostile-url":     {`"https://fra.api.terrascale.tech"`, `"https://evil.example"`},
		"query-steer":     {`"https://fra.api.terrascale.tech"`, `"https://fra.api.terrascale.tech/?x=1"`},
		"bad-capability":  {`"read","write","sql","reactive"`, `"read","admin"`},
		"no-capability":   {`["read","write","sql","reactive"]`, `[]`},
		"priority":        {`"priority":1`, `"priority":0`},
		"no-first-choice": {`"priority":1`, `"priority":2`},
		"placement":       {`"known"`, `"guess"`},
		"refresh":         {`"refreshSeconds":300`, `"refreshSeconds":0`},
		"revision":        {`"revision":7`, `"revision":-1`},
		"residency":       {`"residency":null`, `"residency":{"wide":true}`},
	}
	for name, replacement := range replacements {
		t.Run(name, func(t *testing.T) {
			mutated := strings.Replace(string(valid), replacement[0], replacement[1], 1)
			if _, err := parseRoutingDocument([]byte(mutated), "orders", "main", false); err == nil {
				t.Fatal("malformed discovery document accepted")
			}
		})
	}
	for name, body := range map[string]string{
		"trailing-data": string(valid) + `{}`,
		"duplicate": string(discoveryBody("orders", 300,
			"FRA|https://fra.api.terrascale.tech|read",
			"FRA|https://fra.api.terrascale.tech|read")),
	} {
		t.Run(name, func(t *testing.T) {
			if _, err := parseRoutingDocument([]byte(body), "orders", "main", false); err == nil {
				t.Fatal("malformed discovery document accepted")
			}
		})
	}
}

func TestParseRoutingDocumentClampsRefreshAndGatesDevOrigins(t *testing.T) {
	low := discoveryBody("orders", 1, "GRU|http://127.0.0.1:9911|read")
	doc, err := parseRoutingDocument(low, "orders", "main", true)
	if err != nil {
		t.Fatal(err)
	}
	if doc.RefreshSeconds != minRefreshSeconds {
		t.Fatalf("refresh = %d, want clamped %d", doc.RefreshSeconds, minRefreshSeconds)
	}
	high := discoveryBody("orders", 7200, "GRU|http://localhost:9911|read")
	if doc, err = parseRoutingDocument(high, "orders", "main", true); err != nil {
		t.Fatal(err)
	}
	if doc.RefreshSeconds != maxRefreshSeconds {
		t.Fatalf("refresh = %d, want clamped %d", doc.RefreshSeconds, maxRefreshSeconds)
	}
	if _, err := parseRoutingDocument(low, "orders", "main", false); err == nil {
		t.Fatal("dev http endpoint accepted without a loopback discovery origin")
	}
}

func TestRouteClassificationTable(t *testing.T) {
	for path, want := range map[string]string{
		"/api/v1/databases/orders/documents/o-1": "member",
		"/api/v1/databases/orders":               "member",
		"/api/v1/databases":                      "member",
		"/api/v1/reactive/views":                 "member",
		"/api/v1/health":                         "member",
		"/api/v1/organizations":                  "product",
		"/api/v1/organizations/org-1/projects":   "product",
		"/api/v1/projects":                       "product",
		"/api/v1/regions":                        "catalog",
		"/api/v1/regions/FRA":                    "product",
		"/api/v1/regions/FRA/health":             "product",
		"/api/v1/databases/UPPER/collections":    "member",
	} {
		got := "member"
		if isRegionCatalogRoute(path) {
			got = "catalog"
		} else if isProductRoute(path) {
			got = "product"
		}
		if got != want {
			t.Errorf("%s = %s, want %s", path, got, want)
		}
	}
	if db, ok := parseRoutingDatabase("/api/v1/databases/orders/collections/c"); !ok || db != "orders" {
		t.Fatalf("parseRoutingDatabase = %q, %v", db, ok)
	}
	if db, ok := parseRoutingDatabase("/api/v1/databases/Upper"); ok || db != "" {
		t.Fatalf("invalid database name discovered: %q", db)
	}
	if db, ok := parseRoutingDatabase("/api/v1/databases"); ok || db != "" {
		t.Fatalf("database-less list route discovered: %q", db)
	}
}

func TestDiscoveryRoutesDatabaseRequestsToRegionalEndpoints(t *testing.T) {
	var memberCalls atomic.Int32
	member := httptest.NewServer(http.HandlerFunc(func(w http.ResponseWriter, r *http.Request) {
		memberCalls.Add(1)
		if r.URL.Path != "/api/v1/databases/orders/collections/c/documents/o-1" {
			t.Errorf("member path = %q", r.URL.Path)
		}
		w.Header().Set("Content-Type", "application/json")
		_, _ = w.Write([]byte(`{}`))
	}))
	defer member.Close()

	atlas := &atlasServer{}
	atlas.setDocument(discoveryBody("orders", 300, "FRA|"+member.URL+"|read,write,sql,reactive"), http.StatusOK)
	metadata := atlas.server(t)

	c, err := NewClient(Config{APIBaseURL: "https://api.terrascale.tech", DiscoveryBaseURL: metadata.URL, AccessToken: "token"})
	if err != nil {
		t.Fatal(err)
	}
	for i := 0; i < 2; i++ {
		if err := c.do(t.Context(), http.MethodGet, "/api/v1/databases/orders/collections/c/documents/o-1", nil, nil, nil, "documents.get"); err != nil {
			t.Fatal(err)
		}
	}
	if got := atlas.discoveryCalls.Load(); got != 1 {
		t.Fatalf("discovery calls = %d, want 1 (cached per scope)", got)
	}
	if got := memberCalls.Load(); got != 2 {
		t.Fatalf("member calls = %d, want 2", got)
	}
}

func TestDiscoverySelectsWriteCapableEndpoint(t *testing.T) {
	var readOnlyCalls, writableCalls atomic.Int32
	readOnly := httptest.NewServer(http.HandlerFunc(func(w http.ResponseWriter, r *http.Request) {
		readOnlyCalls.Add(1)
		w.Header().Set("Content-Type", "application/json")
		_, _ = w.Write([]byte(`{}`))
	}))
	defer readOnly.Close()
	writable := httptest.NewServer(http.HandlerFunc(func(w http.ResponseWriter, r *http.Request) {
		writableCalls.Add(1)
		w.Header().Set("Content-Type", "application/json")
		_, _ = w.Write([]byte(`{}`))
	}))
	defer writable.Close()

	atlas := &atlasServer{}
	atlas.setDocument(discoveryBody("orders", 300,
		"GRU|"+readOnly.URL+"|read,sql,reactive",
		"FRA|"+writable.URL+"|read,write,sql,reactive"), http.StatusOK)
	metadata := atlas.server(t)

	c, err := NewClient(Config{APIBaseURL: "https://api.terrascale.tech", DiscoveryBaseURL: metadata.URL, AccessToken: "token"})
	if err != nil {
		t.Fatal(err)
	}
	// A read selects the first read-capable endpoint; a mutation selects the
	// first write-capable endpoint (ADR 0050 §5.3).
	if err := c.do(t.Context(), http.MethodGet, "/api/v1/databases/orders/collections/c/documents/o-1", nil, nil, nil, "documents.get"); err != nil {
		t.Fatal(err)
	}
	if err := c.do(t.Context(), http.MethodPost, "/api/v1/databases/orders/collections/c/documents/o-1", nil, map[string]string{"v": "1"}, nil, "documents.put", WithIdempotencyKey("k1")); err != nil {
		t.Fatal(err)
	}
	if got := readOnlyCalls.Load(); got != 1 {
		t.Fatalf("read-only endpoint calls = %d, want 1", got)
	}
	if got := writableCalls.Load(); got != 1 {
		t.Fatalf("writable endpoint calls = %d, want 1", got)
	}
}

func TestDiscoveryFailsOverToNextEndpointOnOriginFailure(t *testing.T) {
	var downCalls, healthyCalls atomic.Int32
	down := httptest.NewServer(http.HandlerFunc(func(w http.ResponseWriter, r *http.Request) {
		downCalls.Add(1)
		w.WriteHeader(521)
	}))
	defer down.Close()
	healthy := httptest.NewServer(http.HandlerFunc(func(w http.ResponseWriter, r *http.Request) {
		healthyCalls.Add(1)
		w.Header().Set("Content-Type", "application/json")
		_, _ = w.Write([]byte(`{}`))
	}))
	defer healthy.Close()

	atlas := &atlasServer{}
	atlas.setDocument(discoveryBody("orders", 300,
		"GRU|"+down.URL+"|read,write",
		"FRA|"+healthy.URL+"|read,write"), http.StatusOK)
	metadata := atlas.server(t)

	c, err := NewClient(Config{APIBaseURL: "https://api.terrascale.tech", DiscoveryBaseURL: metadata.URL, AccessToken: "token"})
	if err != nil {
		t.Fatal(err)
	}
	// The first endpoint's 521 (Cloudflare origin-down class) demotes it and
	// the bounded read failover moves to the next endpoint in the document
	// (ADR 0050 §5.3); the demotion sticks for the cached document.
	for i := 0; i < 2; i++ {
		if err := c.do(t.Context(), http.MethodGet, "/api/v1/databases/orders", nil, nil, nil, "get_database"); err != nil {
			t.Fatal(err)
		}
	}
	if got := downCalls.Load(); got != 1 {
		t.Fatalf("failed endpoint calls = %d, want 1", got)
	}
	if got := healthyCalls.Load(); got != 2 {
		t.Fatalf("healthy endpoint calls = %d, want 2", got)
	}
}

func TestDiscoveryFailureDegradesToCatalogSelection(t *testing.T) {
	var member atomic.Int32
	memberServer := httptest.NewServer(http.HandlerFunc(func(w http.ResponseWriter, r *http.Request) {
		member.Add(1)
		w.Header().Set("Content-Type", "application/json")
		_, _ = w.Write([]byte(`{}`))
	}))
	defer memberServer.Close()

	atlas := &atlasServer{}
	atlas.setDocument([]byte(`{}`), http.StatusInternalServerError)
	metadata := httptest.NewServer(http.HandlerFunc(func(w http.ResponseWriter, r *http.Request) {
		switch r.URL.Path {
		case discoveryPath:
			atlas.discoveryCalls.Add(1)
			w.WriteHeader(http.StatusInternalServerError)
		case edgeRegionsPath:
			atlas.catalogCalls.Add(1)
			w.Header().Set("Content-Type", "application/json")
			_, _ = w.Write([]byte(fmt.Sprintf(`{"regions":[{"code":"GRU","name":"Sao Paulo","country":"BR","latitude":-23.5,"longitude":-46.6,"available":true}],"hosts":{"GRU":%q}}`, memberServer.URL)))
		default:
			w.WriteHeader(http.StatusNotFound)
		}
	}))
	defer metadata.Close()

	c, err := NewClient(Config{APIBaseURL: "https://api.terrascale.tech", DiscoveryBaseURL: metadata.URL, AccessToken: "token"})
	if err != nil {
		t.Fatal(err)
	}
	// Database-scoped request: discovery 500 -> the catalog-selected host.
	if err := c.do(t.Context(), http.MethodGet, "/api/v1/databases/orders", nil, nil, nil, "get_database"); err != nil {
		t.Fatal(err)
	}
	// Database-less member route: catalog selection directly.
	if _, err := c.Databases.ListDatabases(t.Context(), PageRequest{}); err != nil {
		t.Fatal(err)
	}
	if got := member.Load(); got != 2 {
		t.Fatalf("member calls = %d, want 2", got)
	}
	if got := atlas.catalogCalls.Load(); got != 1 {
		t.Fatalf("catalog fetches = %d, want 1 (five-minute cache)", got)
	}
	// The catalog route itself stays on the atlas origin.
	regions, err := c.Regions.List(t.Context())
	if err != nil {
		t.Fatal(err)
	}
	if len(regions.Regions) != 1 || regions.Regions[0].Code != RegionGRU {
		t.Fatalf("catalog = %+v", regions.Regions)
	}
	if got := atlas.catalogCalls.Load(); got != 2 {
		t.Fatalf("catalog requests after Regions.List = %d, want 2", got)
	}
}

func TestMemberRoutesFailWithTypeRoutingErrorWhenMetadataIsDown(t *testing.T) {
	metadata := httptest.NewServer(http.HandlerFunc(func(w http.ResponseWriter, r *http.Request) {
		w.WriteHeader(http.StatusServiceUnavailable)
	}))
	defer metadata.Close()
	var productCalls atomic.Int32
	product := httptest.NewServer(http.HandlerFunc(func(w http.ResponseWriter, r *http.Request) {
		productCalls.Add(1)
		w.Header().Set("Content-Type", "application/json")
		_, _ = w.Write([]byte(`{}`))
	}))
	defer product.Close()

	c, err := NewClient(Config{APIBaseURL: product.URL, DiscoveryBaseURL: metadata.URL, AccessToken: "token"})
	if err != nil {
		t.Fatal(err)
	}
	err = c.do(t.Context(), http.MethodGet, "/api/v1/databases/orders", nil, nil, nil, "get_database")
	var routingErr *RoutingError
	if !errors.As(err, &routingErr) {
		t.Fatalf("member route error = %T %v, want RoutingError", err, err)
	}
	// Product routes still target the product origin.
	if err := c.do(t.Context(), http.MethodGet, "/api/v1/organizations", nil, nil, nil, "organizations.list"); err != nil {
		t.Fatal(err)
	}
	if got := productCalls.Load(); got != 1 {
		t.Fatalf("product calls = %d, want 1", got)
	}
}

func TestValidateEndpointURLTrustBoundary(t *testing.T) {
	for _, test := range []struct {
		raw      string
		region   string
		allowDev bool
		want     string
	}{
		{raw: "https://fra.api.terrascale.tech", region: "FRA", want: "https://fra.api.terrascale.tech"},
		{raw: "https://FRA.api.terrascale.tech", region: "fra", want: "https://fra.api.terrascale.tech"},
		// A port is canonicalized away, mirroring the reference SDK: the
		// regional family hostname is the trust boundary, not the port.
		{raw: "https://fra.api.terrascale.tech:8443", region: "FRA", want: "https://fra.api.terrascale.tech"},
		{raw: "http://127.0.0.1:9911", region: "FRA", allowDev: true, want: "http://127.0.0.1:9911"},
		{raw: "http://localhost:9911", region: "FRA", allowDev: true, want: "http://localhost:9911"},
		{raw: "https://gru.api.terrascale.tech", region: "FRA"},
		{raw: "http://fra.api.terrascale.tech", region: "FRA"},
		{raw: "http://127.0.0.1:9911", region: "FRA"},
		{raw: "https://user@fra.api.terrascale.tech", region: "FRA"},
		{raw: "https://fra.api.terrascale.tech/x#y", region: "FRA"},
		{raw: "https://evil.example", region: "FRA", allowDev: true},
		{raw: "", region: "FRA", allowDev: true},
		{raw: strings.Repeat("h", 2049), region: "FRA", allowDev: true},
	} {
		got, ok := validateEndpointURL(test.raw, test.region, test.allowDev)
		if ok != (test.want != "") || got != test.want {
			t.Errorf("validateEndpointURL(%q, %q, %v) = %q, %v; want %q", test.raw, test.region, test.allowDev, got, ok, test.want)
		}
	}
}

func TestDiscoveryIsSingleFlightPerScope(t *testing.T) {
	var discoveryCalls atomic.Int32
	release := make(chan struct{})
	member := httptest.NewServer(http.HandlerFunc(func(w http.ResponseWriter, r *http.Request) {
		w.Header().Set("Content-Type", "application/json")
		_, _ = w.Write([]byte(`{}`))
	}))
	defer member.Close()
	metadata := httptest.NewServer(http.HandlerFunc(func(w http.ResponseWriter, r *http.Request) {
		switch r.URL.Path {
		case discoveryPath:
			discoveryCalls.Add(1)
			<-release
			w.Header().Set("Content-Type", "application/json")
			_, _ = w.Write(discoveryBody("orders", 300, "FRA|"+member.URL+"|read,write"))
		default:
			w.WriteHeader(http.StatusNotFound)
		}
	}))
	defer metadata.Close()

	c, err := NewClient(Config{APIBaseURL: "https://api.terrascale.tech", DiscoveryBaseURL: metadata.URL, AccessToken: "token"})
	if err != nil {
		t.Fatal(err)
	}
	const concurrent = 8
	var group sync.WaitGroup
	errs := make([]error, concurrent)
	for i := 0; i < concurrent; i++ {
		group.Add(1)
		go func(i int) {
			defer group.Done()
			errs[i] = c.do(t.Context(), http.MethodGet, "/api/v1/databases/orders", nil, nil, nil, "get_database")
		}(i)
	}
	// Wait until the shared fetch is in flight, then let every joiner land.
	time.Sleep(50 * time.Millisecond)
	close(release)
	group.Wait()
	for i, err := range errs {
		if err != nil {
			t.Fatalf("request %d: %v", i, err)
		}
	}
	if got := discoveryCalls.Load(); got != 1 {
		t.Fatalf("discovery calls = %d, want 1 (single flight)", got)
	}
	c.router.mu.Lock()
	inflight := len(c.router.inflight)
	c.router.mu.Unlock()
	if inflight != 0 {
		t.Fatalf("inflight map retains %d entries", inflight)
	}
}

func TestClient421AdoptsPlacementAndSchedulesCorrectiveRediscovery(t *testing.T) {
	var gruCalls, sfoCalls atomic.Int32
	var queries sync.Map
	transport := pinningRoundTripperFunc(func(r *http.Request) (*http.Response, error) {
		respond := func(status int, header http.Header, body string) (*http.Response, error) {
			return &http.Response{
				StatusCode: status,
				Header:     header,
				Body:       io.NopCloser(strings.NewReader(body)),
				Request:    r,
			}, nil
		}
		jsonHeader := http.Header{"Content-Type": {"application/json"}}
		switch r.URL.Host {
		case "127.0.0.1:9":
			if r.URL.Path == discoveryPath {
				queries.Store(r.URL.RawQuery, true)
				return respond(http.StatusOK, jsonHeader, string(discoveryBody("orders", minRefreshSeconds,
					"GRU|https://gru.api.terrascale.tech|read,write")))
			}
			return respond(http.StatusNotFound, nil, "")
		case "gru.api.terrascale.tech":
			if gruCalls.Add(1) == 1 {
				return respond(http.StatusMisdirectedRequest,
					http.Header{"X-TerraScale-Placement": {"SFO"}}, "")
			}
			return respond(http.StatusOK, jsonHeader, `{}`)
		default:
			sfoCalls.Add(1)
			return respond(http.StatusOK, jsonHeader, `{}`)
		}
	})
	c, err := NewClient(Config{
		APIBaseURL:       "https://api.terrascale.tech",
		DiscoveryBaseURL: "http://127.0.0.1:9", // intercepted by the transport
		HTTPClient:       &http.Client{Transport: transport},
		AccessToken:      "token",
	})
	if err != nil {
		t.Fatal(err)
	}
	// A GET 421 repairs once from the placement header: the member's
	// placement is adopted into the routing cache as a synthesized document
	// and the request replays against its first region (ADR 0050 §5.4).
	if err := c.do(t.Context(), http.MethodGet, "/api/v1/databases/orders", nil, nil, nil, "get_database"); err != nil {
		t.Fatal(err)
	}
	if got := gruCalls.Load(); got != 1 {
		t.Fatalf("GRU calls = %d, want 1", got)
	}
	if got := sfoCalls.Load(); got != 1 {
		t.Fatalf("SFO replay calls = %d, want 1", got)
	}
	doc, ok := c.router.cachedDocument(scopeKey(c.router.scopeFor("orders")))
	if !ok || len(doc.Endpoints) != 1 || doc.Endpoints[0].Region != "SFO" || doc.Endpoints[0].URL != "https://sfo.api.terrascale.tech" {
		t.Fatalf("placement not adopted: %+v", doc)
	}
	// The synthesized document is provisional: once it expires, the next
	// lookup is corrective (stale=1).
	clock := time.Now()
	c.router.now = func() time.Time { return clock.Add(2 * minRefreshSeconds * time.Second) }
	if err := c.do(t.Context(), http.MethodGet, "/api/v1/databases/orders", nil, nil, nil, "get_database"); err != nil {
		t.Fatal(err)
	}
	corrective := false
	queries.Range(func(query, _ any) bool {
		if query.(string) == "branch=main&database=orders&stale=1" {
			corrective = true
		}
		return true
	})
	if !corrective {
		t.Fatal("corrective rediscovery with stale=1 missing")
	}
}

func TestReactiveAttachUsesDiscoveredEndpoint(t *testing.T) {
	var attachCalls atomic.Int32
	member := httptest.NewServer(http.HandlerFunc(func(w http.ResponseWriter, r *http.Request) {
		attachCalls.Add(1)
		w.Header().Set("Content-Type", "application/json")
		_, _ = w.Write([]byte(`{"viewId":"view-1","generation":1,"checkpoint":"Y3A=","region":"FRA","capability":"cap-token","logUrl":"/api/v1/reactive/views/view-1/1/log","snapshot":[]}`))
	}))
	defer member.Close()
	metadata := httptest.NewServer(http.HandlerFunc(func(w http.ResponseWriter, r *http.Request) {
		w.Header().Set("Content-Type", "application/json")
		_, _ = w.Write(discoveryBody("orders", 300, "FRA|"+member.URL+"|read,reactive"))
	}))
	defer metadata.Close()

	c, err := NewClient(Config{APIBaseURL: "https://api.terrascale.tech", DiscoveryBaseURL: metadata.URL, AccessToken: "token"})
	if err != nil {
		t.Fatal(err)
	}
	if _, err := c.Execute(t.Context(), "orders", "SELECT * FROM users", nil); err != nil {
		t.Fatal(err)
	}
	if got := attachCalls.Load(); got != 1 {
		t.Fatalf("attach calls = %d, want 1 on the regional endpoint", got)
	}
	if got := memberOriginForAttach("https://api.terrascale.tech", http.Header{"X-TerraScale-Region": {" sfo "}}, ""); got != "https://sfo.api.terrascale.tech" {
		t.Fatalf("member origin = %q", got)
	}
	if got := memberOriginForAttach("http://127.0.0.1:9", nil, ""); got != "http://127.0.0.1:9" {
		t.Fatalf("custom member origin rewritten: %q", got)
	}
}

func TestDiscoveryNeverLeaksCredentialsToMetadataOrigin(t *testing.T) {
	var authorization atomic.Value
	member := httptest.NewServer(http.HandlerFunc(func(w http.ResponseWriter, r *http.Request) {
		w.Header().Set("Content-Type", "application/json")
		_, _ = w.Write([]byte(`{}`))
	}))
	defer member.Close()
	metadata := httptest.NewServer(http.HandlerFunc(func(w http.ResponseWriter, r *http.Request) {
		authorization.Store(r.Header.Get("Authorization"))
		w.Header().Set("Content-Type", "application/json")
		_, _ = w.Write(discoveryBody("orders", 300, "FRA|"+member.URL+"|read,write"))
	}))
	defer metadata.Close()

	c, err := NewClient(Config{APIBaseURL: "https://api.terrascale.tech", DiscoveryBaseURL: metadata.URL, AccessToken: "secret-token"})
	if err != nil {
		t.Fatal(err)
	}
	if err := c.do(t.Context(), http.MethodGet, "/api/v1/databases/orders", nil, nil, nil, "get_database"); err != nil {
		t.Fatal(err)
	}
	if got, _ := authorization.Load().(string); got != "" {
		t.Fatalf("discovery request carried authorization %q", got)
	}
}

func TestRegionRttTrackerEWMABoundsAndOrder(t *testing.T) {
	base := time.Unix(0, 0)
	tracker := newRegionRttTracker()
	estimate := func(region string) float64 {
		t.Helper()
		tracker.mu.Lock()
		defer tracker.mu.Unlock()
		sample, ok := tracker.samples[region]
		if !ok {
			t.Fatalf("region %s has no sample", region)
		}
		return sample.valueMs
	}

	// The first sample seeds the estimate; one halflife later each new sample
	// contributes exactly half, so 100 -> 200 ms converges to 150 then 175.
	tracker.observe("FRA", 100*time.Millisecond, base)
	tracker.observe("FRA", 200*time.Millisecond, base.Add(rttHalflife))
	if got := estimate("FRA"); got != 150 {
		t.Fatalf("EWMA after one halflife = %v ms, want 150", got)
	}
	tracker.observe("FRA", 200*time.Millisecond, base.Add(2*rttHalflife))
	if got := estimate("FRA"); got != 175 {
		t.Fatalf("EWMA after two halflives = %v ms, want 175", got)
	}
	// A sample whose timestamp does not advance past the region's last sample
	// is ignored, so out-of-order observations cannot skew the average.
	tracker.observe("FRA", time.Millisecond, base.Add(rttHalflife))
	if got := estimate("FRA"); got != 175 {
		t.Fatalf("out-of-order sample moved the EWMA to %v ms", got)
	}
	// Round trips outside the 0..60000 ms contract bound never land, and
	// unknown regions are never tracked.
	tracker.observe("SFO", -time.Millisecond, base)
	tracker.observe("SFO", (maxRegionRttMs+1)*time.Millisecond, base)
	tracker.observe("XXX", time.Second, base)
	// The header renders whole milliseconds for ascending regions, skipping
	// estimates below the 1 ms floor.
	tracker.observe("SFO", 203*time.Millisecond, base)
	tracker.observe("GRU", 400*time.Microsecond, base)
	if got, want := tracker.measuredRttHeader(), "FRA=175,SFO=203"; got != want {
		t.Fatalf("region rtt header = %q, want %q", got, want)
	}
}

func TestDiscoveryRefreshCarriesMeasuredRttAndRevision(t *testing.T) {
	var memberRttLeaks atomic.Int32
	member := httptest.NewServer(http.HandlerFunc(func(w http.ResponseWriter, r *http.Request) {
		if r.Header.Get(regionRttHeader) != "" {
			memberRttLeaks.Add(1)
		}
		// The sleep keeps the measured round trip above the header's 1 ms
		// floor deterministically.
		time.Sleep(20 * time.Millisecond)
		w.Header().Set("Content-Type", "application/json")
		_, _ = w.Write([]byte(`{}`))
	}))
	defer member.Close()

	atlas := &atlasServer{}
	atlas.setDocument(discoveryBody("orders", minRefreshSeconds, "FRA|"+member.URL+"|read,write"), http.StatusOK)
	metadata := atlas.server(t)

	c, err := NewClient(Config{APIBaseURL: "https://api.terrascale.tech", DiscoveryBaseURL: metadata.URL, AccessToken: "token"})
	if err != nil {
		t.Fatal(err)
	}
	// First lookup: no measurements exist and nothing is cached yet, so the
	// discovery GET carries neither the region-rtt header nor rev.
	if err := c.do(t.Context(), http.MethodGet, "/api/v1/databases/orders", nil, nil, nil, "get_database"); err != nil {
		t.Fatal(err)
	}
	// Expire the cached document; the successful regional request above fed
	// the per-region latency tracker (ADR 0050 §2).
	clock := time.Now()
	c.router.now = func() time.Time { return clock.Add(3 * minRefreshSeconds * time.Second) }
	if err := c.do(t.Context(), http.MethodGet, "/api/v1/databases/orders", nil, nil, nil, "get_database"); err != nil {
		t.Fatal(err)
	}

	if got := atlas.discoveryCalls.Load(); got != 2 {
		t.Fatalf("discovery calls = %d, want 2 (refresh after expiry)", got)
	}
	queries := atlas.discoveryQueries()
	rttHeaders := atlas.discoveryRttHeaders()
	if len(queries) != 2 || len(rttHeaders) != 2 {
		t.Fatalf("recorded %d discovery queries and %d rtt headers, want 2 each", len(queries), len(rttHeaders))
	}
	if queries[0] != "branch=main&database=orders" {
		t.Fatalf("initial discovery query = %q, want no rev", queries[0])
	}
	if queries[1] != "branch=main&database=orders&rev=7" {
		t.Fatalf("refresh discovery query = %q, want the cached revision", queries[1])
	}
	if rttHeaders[0] != "" {
		t.Fatalf("initial discovery carried region rtt %q before any measurement", rttHeaders[0])
	}
	value, ok := strings.CutPrefix(rttHeaders[1], "FRA=")
	if !ok {
		t.Fatalf("refresh discovery rtt header = %q, want an FRA measurement", rttHeaders[1])
	}
	ms, err := strconv.Atoi(value)
	if err != nil || ms < 20 || ms > maxRegionRttMs {
		t.Fatalf("refresh discovery rtt header = %q, want FRA milliseconds in [20, %d]", rttHeaders[1], maxRegionRttMs)
	}
	if got := memberRttLeaks.Load(); got != 0 {
		t.Fatalf("member requests carrying the region rtt header = %d", got)
	}
}
