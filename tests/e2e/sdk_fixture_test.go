package e2e

import (
	"bytes"
	"context"
	"crypto/subtle"
	"encoding/json/jsontext"
	json "encoding/json/v2"
	"fmt"
	"io"
	"net/http"
	"net/http/httptest"
	"slices"
	"strings"
	"sync"
	"testing"
	"time"

	"terrascale.tech/dashboard/internal/adapters/tsgo"
	"terrascale.tech/dashboard/internal/core"
	"terrascale.tech/dashboard/internal/features/apikeys"
	"terrascale.tech/dashboard/internal/features/auth"
	"terrascale.tech/dashboard/internal/features/databases"
	"terrascale.tech/dashboard/internal/features/onboarding"
	"terrascale.tech/dashboard/internal/platform/assets"
	"terrascale.tech/dashboard/internal/platform/csrf"
	"terrascale.tech/dashboard/internal/platform/render"
	"terrascale.tech/dashboard/internal/platform/session"
	sdk "terrascale.tech/sdk"
)

// sdkFixture runs the real dashboard account, database, and key handlers
// against a contract-shaped in-process public API. Accounts and onboarding
// stay in demo mode; databases and API keys go through the ts-go adapter.
type sdkFixture struct {
	dashboardURL string
	publicAPIURL string
}

// startSDKFixture starts both loopback servers; they close with the test.
func startSDKFixture(t *testing.T) sdkFixture {
	t.Helper()
	store := core.NewStore()
	sessions := session.New(store, session.Options{})
	guard := csrf.New(sessions, csrf.Options{})
	producer := newFixtureProducer()
	public := httptest.NewServer(producer.handler())
	t.Cleanup(public.Close)

	demo := core.Deps{Store: store, Sessions: sessions, CSRF: guard, Render: render.New(store, guard), Demo: true}
	live := demo
	live.Demo = false
	live.SDK = tsgo.Factory{APIBaseURL: public.URL, AccessToken: producer.managementToken}

	mux := http.NewServeMux()
	must(t, assets.Mount(mux, false))
	auth.Mount(mux, demo)
	onboarding.Mount(mux, demo)
	subscriptions.Mount(mux, demo)
	databases.Mount(mux, live)
	apikeys.MountWithIssuer(mux, live, fixtureIssuer{store: store, producer: producer})
	mux.HandleFunc("GET /", func(w http.ResponseWriter, r *http.Request) {
		http.Redirect(w, r, "/onboarding", http.StatusTemporaryRedirect)
	})
	dashboard := httptest.NewServer(guard.Protect(mux))
	t.Cleanup(dashboard.Close)
	return sdkFixture{dashboardURL: dashboard.URL, publicAPIURL: public.URL}
}

type fixtureScope struct{ tenant, project string }

type fixtureDatabase struct {
	documents map[string]map[string][]byte
	revision  uint64
}

type fixtureKey struct {
	id, secret, tenant, project, database, dashboardDatabaseID string
	name                                                       string
	scopes                                                     []string
	createdAt                                                  time.Time
	revoked                                                    bool
}

type fixtureProducer struct {
	mu              sync.Mutex
	managementToken string
	databases       map[fixtureScope]map[string]*fixtureDatabase
	keys            map[string]*fixtureKey
	keysByID        map[string]*fixtureKey
}

func newFixtureProducer() *fixtureProducer {
	return &fixtureProducer{
		managementToken: "mgmt_fixture_" + core.NewID(""),
		databases:       make(map[fixtureScope]map[string]*fixtureDatabase),
		keys:            make(map[string]*fixtureKey),
		keysByID:        make(map[string]*fixtureKey),
	}
}

func (p *fixtureProducer) handler() http.Handler {
	mux := http.NewServeMux()
	mux.HandleFunc("POST /api/v1/databases", p.createDatabase)
	const document = "/api/v1/databases/{database}/collections/{collection}/documents/{id}"
	for _, method := range []string{http.MethodGet, http.MethodPut, http.MethodDelete} {
		mux.HandleFunc(method+" "+document, p.document)
	}
	return mux
}

func (p *fixtureProducer) createDatabase(w http.ResponseWriter, r *http.Request) {
	if !bearerEqual(r, p.managementToken) {
		http.Error(w, "unknown management credential", http.StatusUnauthorized)
		return
	}
	s := fixtureScope{tenant: r.Header.Get("X-TerraScale-Tenant-ID"), project: r.Header.Get("X-TerraScale-Project-ID")}
	if s.tenant == "" || s.project == "" {
		http.Error(w, "tenant and project scope required", http.StatusBadRequest)
		return
	}
	var input sdk.CreateDatabaseRequest
	r.Body = http.MaxBytesReader(w, r.Body, 16<<10)
	if err := json.UnmarshalRead(r.Body, &input); err != nil || input.Name == "" || input.OperationID == "" || len(input.Placement) == 0 {
		http.Error(w, "invalid database request", http.StatusBadRequest)
		return
	}
	p.mu.Lock()
	defer p.mu.Unlock()
	if p.databases[s] == nil {
		p.databases[s] = make(map[string]*fixtureDatabase)
	}
	if _, exists := p.databases[s][input.Name]; exists {
		http.Error(w, "database already exists", http.StatusConflict)
		return
	}
	p.databases[s][input.Name] = &fixtureDatabase{documents: make(map[string]map[string][]byte)}
	writeFixtureJSON(w, http.StatusCreated, sdk.DatabaseRecord{
		Name: input.Name, Placement: string(input.Placement[0]), CreatedAt: time.Now().UnixMilli(),
	})
}

func (p *fixtureProducer) document(w http.ResponseWriter, r *http.Request) {
	token := bearerToken(r)
	p.mu.Lock()
	defer p.mu.Unlock()
	key := p.keys[token]
	if key == nil || key.revoked {
		http.Error(w, "unknown credential", http.StatusUnauthorized)
		return
	}
	if key.tenant != r.Header.Get("X-TerraScale-Tenant-ID") || key.project != r.Header.Get("X-TerraScale-Project-ID") {
		http.Error(w, "credential scope denied", http.StatusForbidden)
		return
	}
	name := r.PathValue("database")
	if key.database != "" && key.database != name {
		http.Error(w, "database scope denied", http.StatusForbidden)
		return
	}
	capability := "database:read"
	if r.Method != http.MethodGet {
		capability = "database:write"
	}
	if !slices.Contains(key.scopes, capability) {
		http.Error(w, "capability denied", http.StatusForbidden)
		return
	}
	db := p.databases[fixtureScope{tenant: key.tenant, project: key.project}][name]
	if db == nil {
		http.Error(w, "database unknown", http.StatusForbidden)
		return
	}
	collection, id := r.PathValue("collection"), r.PathValue("id")
	if db.documents[collection] == nil {
		db.documents[collection] = make(map[string][]byte)
	}
	switch r.Method {
	case http.MethodGet:
		body, ok := db.documents[collection][id]
		if !ok {
			http.Error(w, "document unknown", http.StatusNotFound)
			return
		}
		w.Header().Set("Content-Type", "application/json")
		_, _ = w.Write(body)
	case http.MethodPut:
		r.Body = http.MaxBytesReader(w, r.Body, 1<<20)
		body, err := io.ReadAll(r.Body)
		if err != nil || len(body) == 0 || !jsontext.Value(body).IsValid() {
			http.Error(w, "invalid document", http.StatusBadRequest)
			return
		}
		db.documents[collection][id] = bytes.Clone(body)
		db.revision++
		w.Header().Set("ETag", fmt.Sprintf(`"%d"`, db.revision))
		writeFixtureJSON(w, http.StatusOK, struct {
			Revision string `json:"revision"`
		}{Revision: fmt.Sprint(db.revision)})
	case http.MethodDelete:
		delete(db.documents[collection], id)
		w.WriteHeader(http.StatusNoContent)
	}
}

// fixtureIssuer mints producer credentials for databases the principal can
// manage in the dashboard store.
type fixtureIssuer struct {
	store    *core.Store
	producer *fixtureProducer
}

func (i fixtureIssuer) Create(_ context.Context, principal core.Principal, input sdk.CreateProjectAPIKeyRequest) (*sdk.ProjectAPIKeySecret, error) {
	databaseName, err := i.authorize(principal, input.DatabaseID)
	if err != nil {
		return nil, err
	}
	key := &fixtureKey{
		id: core.NewID("key"), secret: newFixtureSecret(), tenant: principal.OrganizationID, project: principal.ProjectID,
		database: databaseName, dashboardDatabaseID: input.DatabaseID, name: input.Name,
		scopes: slices.Clone(input.Scopes), createdAt: time.Now().UTC(),
	}
	i.producer.mu.Lock()
	defer i.producer.mu.Unlock()
	i.producer.keys[key.secret] = key
	i.producer.keysByID[key.id] = key
	return key.response(), nil
}

func (i fixtureIssuer) Rotate(_ context.Context, principal core.Principal, keyID string) (*sdk.ProjectAPIKeySecret, error) {
	if _, err := i.authorize(principal, ""); err != nil {
		return nil, err
	}
	i.producer.mu.Lock()
	defer i.producer.mu.Unlock()
	key := i.producer.keysByID[keyID]
	if key == nil || key.project != principal.ProjectID || key.tenant != principal.OrganizationID || key.revoked {
		return nil, core.ErrNotFound
	}
	delete(i.producer.keys, key.secret)
	key.secret = newFixtureSecret()
	i.producer.keys[key.secret] = key
	return key.response(), nil
}

func (i fixtureIssuer) Revoke(_ context.Context, principal core.Principal, keyID string) (*sdk.ProjectAPIKeyRevocation, error) {
	if _, err := i.authorize(principal, ""); err != nil {
		return nil, err
	}
	i.producer.mu.Lock()
	defer i.producer.mu.Unlock()
	key := i.producer.keysByID[keyID]
	if key == nil || key.project != principal.ProjectID || key.tenant != principal.OrganizationID {
		return nil, core.ErrNotFound
	}
	key.revoked = true
	delete(i.producer.keys, key.secret)
	return &sdk.ProjectAPIKeyRevocation{KeyID: keyID, Revoked: true}, nil
}

// authorize returns the producer database name behind a dashboard database
// id, or "" when no database is requested.
func (i fixtureIssuer) authorize(principal core.Principal, databaseID string) (string, error) {
	account, err := i.store.SnapshotAs(principal)
	if err != nil {
		return "", err
	}
	project, err := account.ProjectFor(principal, principal.OrganizationID, principal.ProjectID)
	if err != nil {
		return "", err
	}
	if !account.Organizations[principal.OrganizationID].CanManage(principal.UserID) {
		return "", core.ErrForbidden
	}
	if databaseID == "" {
		return "", nil
	}
	db, ok := project.Databases[databaseID]
	if !ok || db.ProjectID != project.ID {
		return "", core.ErrNotFound
	}
	return db.Name, nil
}

func (k *fixtureKey) response() *sdk.ProjectAPIKeySecret {
	return &sdk.ProjectAPIKeySecret{
		APIKey: sdk.ProjectAPIKey{
			ID: k.id, ProjectID: k.project, Name: k.name,
			Prefix: k.secret[:min(len(k.secret), 16)], LastFour: k.secret[len(k.secret)-4:],
			Scopes: slices.Clone(k.scopes), DatabaseID: k.dashboardDatabaseID,
			CreatedAt: k.createdAt.Format(time.RFC3339), Active: !k.revoked,
		},
		Secret: k.secret,
	}
}

func newFixtureSecret() string {
	return "tsk_fixture_" + strings.TrimPrefix(core.NewID(""), "_")
}

func bearerToken(r *http.Request) string {
	scheme, token, ok := strings.Cut(r.Header.Get("Authorization"), " ")
	if !ok || !strings.EqualFold(scheme, "Bearer") {
		return ""
	}
	return token
}

func bearerEqual(r *http.Request, expected string) bool {
	return subtle.ConstantTimeCompare([]byte(bearerToken(r)), []byte(expected)) == 1
}

func writeFixtureJSON(w http.ResponseWriter, status int, value any) {
	w.Header().Set("Content-Type", "application/json")
	w.WriteHeader(status)
	_ = json.MarshalWrite(w, value)
}
