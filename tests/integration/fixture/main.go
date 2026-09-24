// The fixture runs real dashboard account/database/key handlers against a
// contract-shaped in-process public API. It is test-only and is not wired into
// cmd/dashboard or deployed runtime configuration.
package main

import (
	"context"
	"crypto/subtle"
	"encoding/json/jsontext"
	json "encoding/json/v2"
	"errors"
	"fmt"
	"io"
	"io/fs"
	"net/http"
	"net/http/httptest"
	"os"
	"os/signal"
	"strings"
	"sync"
	"syscall"
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
	"terrascale.tech/dashboard/internal/ui"
	sdk "terrascale.tech/sdk"
)

type scope struct{ tenant, project string }

type database struct {
	scope     scope
	name      string
	documents map[string]map[string][]byte
	revision  uint64
}

type credential struct {
	id, secret, tenant, project, database, dashboardDatabaseID string
	name                                                       string
	scopes                                                     []string
	createdAt                                                  time.Time
	revoked                                                    bool
}

type producer struct {
	mu              sync.Mutex
	managementToken string
	databases       map[scope]map[string]*database
	keys            map[string]*credential
	keysByID        map[string]*credential
}

func newProducer() *producer {
	return &producer{managementToken: "mgmt_fixture_" + core.NewID(""), databases: make(map[scope]map[string]*database), keys: make(map[string]*credential), keysByID: make(map[string]*credential)}
}

func (p *producer) handler() http.Handler {
	mux := http.NewServeMux()
	mux.HandleFunc("POST /api/v1/databases", p.createDatabase)
	mux.HandleFunc("GET /api/v1/databases/{database}/collections/{collection}/documents/{id}", p.document)
	mux.HandleFunc("PUT /api/v1/databases/{database}/collections/{collection}/documents/{id}", p.document)
	mux.HandleFunc("DELETE /api/v1/databases/{database}/collections/{collection}/documents/{id}", p.document)
	return mux
}

func (p *producer) createDatabase(w http.ResponseWriter, r *http.Request) {
	if !bearerEqual(r, p.managementToken) {
		http.Error(w, "unknown management credential", http.StatusUnauthorized)
		return
	}
	s := scope{tenant: r.Header.Get("X-TerraScale-Tenant-ID"), project: r.Header.Get("X-TerraScale-Project-ID")}
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
		p.databases[s] = make(map[string]*database)
	}
	if _, exists := p.databases[s][input.Name]; exists {
		http.Error(w, "database already exists", http.StatusConflict)
		return
	}
	p.databases[s][input.Name] = &database{scope: s, name: input.Name, documents: make(map[string]map[string][]byte)}
	w.Header().Set("Content-Type", "application/json")
	w.WriteHeader(http.StatusCreated)
	_, _ = w.Write(mustJSON(sdk.DatabaseRecord{Name: input.Name, Placement: string(input.Placement[0]), CreatedAt: time.Now().UnixMilli()}))
}

func (p *producer) document(w http.ResponseWriter, r *http.Request) {
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
	allowed := false
	for _, scope := range key.scopes {
		if scope == capability {
			allowed = true
			break
		}
	}
	if !allowed {
		http.Error(w, "capability denied", http.StatusForbidden)
		return
	}
	db := p.databases[scope{tenant: key.tenant, project: key.project}][name]
	if db == nil {
		http.Error(w, "database unknown", http.StatusForbidden)
		return
	}
	collection, id := r.PathValue("collection"), r.PathValue("id")
	if db.documents[collection] == nil {
		db.documents[collection] = make(map[string][]byte)
	}
	w.Header().Set("Content-Type", "application/json")
	switch r.Method {
	case http.MethodGet:
		body, ok := db.documents[collection][id]
		if !ok {
			http.Error(w, "document unknown", http.StatusNotFound)
			return
		}
		_, _ = w.Write(body)
	case http.MethodPut:
		r.Body = http.MaxBytesReader(w, r.Body, 1<<20)
		body, err := io.ReadAll(r.Body)
		if err != nil || len(body) == 0 || !jsontext.Value(body).IsValid() {
			http.Error(w, "invalid document", http.StatusBadRequest)
			return
		}
		db.documents[collection][id] = append([]byte(nil), body...)
		db.revision++
		w.Header().Set("ETag", fmt.Sprintf("\"%d\"", db.revision))
		_, _ = w.Write(mustJSON(struct {
			Revision string `json:"revision"`
		}{Revision: fmt.Sprint(db.revision)}))
	case http.MethodDelete:
		delete(db.documents[collection], id)
		w.WriteHeader(http.StatusNoContent)
	}
}

type issuer struct {
	store    *core.Store
	producer *producer
}

func (i issuer) Create(_ context.Context, principal core.Principal, input sdk.CreateProjectAPIKeyRequest) (*sdk.ProjectAPIKeySecret, error) {
	databaseName, err := i.authorize(principal, input.DatabaseID)
	if err != nil {
		return nil, err
	}
	secret := "tsk_fixture_" + strings.TrimPrefix(core.NewID(""), "_")
	key := &credential{id: core.NewID("key"), secret: secret, tenant: principal.OrganizationID, project: principal.ProjectID, database: databaseName, dashboardDatabaseID: input.DatabaseID, name: input.Name, scopes: append([]string(nil), input.Scopes...), createdAt: time.Now().UTC()}
	i.producer.mu.Lock()
	i.producer.keys[secret] = key
	i.producer.keysByID[key.id] = key
	i.producer.mu.Unlock()
	return key.response(), nil
}

func (i issuer) Rotate(_ context.Context, principal core.Principal, keyID string) (*sdk.ProjectAPIKeySecret, error) {
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
	key.secret = "tsk_fixture_" + strings.TrimPrefix(core.NewID(""), "_")
	i.producer.keys[key.secret] = key
	return key.response(), nil
}

func (i issuer) Revoke(_ context.Context, principal core.Principal, keyID string) (*sdk.ProjectAPIKeyRevocation, error) {
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

func (i issuer) authorize(principal core.Principal, databaseID string) (string, error) {
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

func (c *credential) response() *sdk.ProjectAPIKeySecret {
	return &sdk.ProjectAPIKeySecret{APIKey: sdk.ProjectAPIKey{ID: c.id, ProjectID: c.project, Name: c.name, Prefix: c.secret[:min(len(c.secret), 16)], LastFour: c.secret[len(c.secret)-4:], Scopes: append([]string(nil), c.scopes...), DatabaseID: c.dashboardDatabaseID, CreatedAt: c.createdAt.Format(time.RFC3339), Active: !c.revoked}, Secret: c.secret}
}

func bearerToken(r *http.Request) string {
	scheme, token, ok := strings.Cut(r.Header.Get("Authorization"), " ")
	if !ok || !strings.EqualFold(scheme, "Bearer") {
		return ""
	}
	return token
}

func bearerEqual(r *http.Request, expected string) bool {
	actual := bearerToken(r)
	return len(actual) == len(expected) && subtle.ConstantTimeCompare([]byte(actual), []byte(expected)) == 1
}

func mustJSON(value any) []byte {
	encoded, err := json.Marshal(value)
	if err != nil {
		panic(err)
	}
	return encoded
}

func run(ctx context.Context) error {
	store := core.NewStore()
	sessions := session.New(store, session.Options{})
	guard := csrf.New(sessions, csrf.Options{})
	layouts, err := fs.Sub(ui.Assets, "layouts")
	if err != nil {
		return err
	}
	renderer, err := render.New(layouts, store, guard)
	if err != nil {
		return err
	}
	producer := newProducer()
	publicServer := httptest.NewServer(producer.handler())
	defer publicServer.Close()
	demo := core.Deps{Store: store, Sessions: sessions, CSRF: guard, Render: renderer, Demo: true}
	live := demo
	live.Demo = false
	live.SDK = tsgo.Factory{APIBaseURL: publicServer.URL, AccessToken: producer.managementToken}
	mux := http.NewServeMux()
	if err := assets.Mount(mux, false); err != nil {
		return err
	}
	auth.Mount(mux, demo)
	onboarding.Mount(mux, demo)
	databases.Mount(mux, live)
	apikeys.MountWithIssuer(mux, live, issuer{store: store, producer: producer})
	mux.HandleFunc("GET /", func(w http.ResponseWriter, r *http.Request) {
		http.Redirect(w, r, "/onboarding", http.StatusTemporaryRedirect)
	})
	dashboardServer := httptest.NewServer(guard.Protect(mux))
	defer dashboardServer.Close()
	line := mustJSON(struct {
		DashboardURL string `json:"dashboardUrl"`
		PublicAPIURL string `json:"publicApiUrl"`
	}{DashboardURL: dashboardServer.URL, PublicAPIURL: publicServer.URL})
	if _, err := fmt.Fprintln(os.Stdout, string(line)); err != nil {
		return err
	}
	<-ctx.Done()
	return nil
}

func main() {
	ctx, stop := signal.NotifyContext(context.Background(), os.Interrupt, syscall.SIGTERM)
	defer stop()
	if err := run(ctx); err != nil && !errors.Is(err, context.Canceled) {
		fmt.Fprintln(os.Stderr, err)
		os.Exit(1)
	}
}
