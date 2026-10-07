package databases

import (
	"context"
	"encoding/json"
	"errors"
	"net/http"
	"net/http/httptest"
	"slices"
	"strings"
	"sync"
	"testing"
	"time"

	"terrascale.tech/dashboard/internal/adapters/tsgo"
	"terrascale.tech/dashboard/internal/core"
	"terrascale.tech/dashboard/internal/core/memstore"
	v1 "terrascale.tech/sdk/v1"
)

// contractRegion is one region's TerraBase as the TS-643 placement contract
// v1 (Ku TS-669) describes it, reduced to the routes placement uses. The
// pinned TerraBase release does not serve placement yet.
type contractRegion struct {
	id       string
	mu       sync.Mutex
	known    map[string]bool // organization and project ids
	held     map[string]v1.ManagedDatabase
	requests []string
}

func (r *contractRegion) ServeHTTP(w http.ResponseWriter, req *http.Request) {
	r.mu.Lock()
	defer r.mu.Unlock()
	if req.Header.Get("Authorization") != "Bearer user-token" {
		http.Error(w, `{"error":{"code":"invalid_key","message":"no token"}}`, http.StatusUnauthorized)
		return
	}
	var body struct {
		Name           string           `json:"name"`
		HomeRegion     string           `json:"homeRegion"`
		ReplicaRegions []string         `json:"replicaRegions"`
		Ops            []map[string]any `json:"ops"`
	}
	_ = json.NewDecoder(req.Body).Decode(&body)
	path := strings.TrimPrefix(req.URL.Path, "/v1/management/")
	parts := strings.Split(path, "/")
	r.requests = append(r.requests, req.Method+" "+path)
	answer := func(status int, v any) {
		w.Header().Set("Content-Type", "application/json")
		w.WriteHeader(status)
		_ = json.NewEncoder(w).Encode(v)
	}
	refuse := func(status int, code string) {
		answer(status, map[string]any{"error": map[string]string{"code": code, "message": code}})
	}
	switch {
	case req.Method == http.MethodGet && (len(parts) == 2 || len(parts) == 4):
		if !r.known[parts[len(parts)-1]] {
			refuse(http.StatusNotFound, "unknown_route")
			return
		}
		answer(http.StatusOK, map[string]string{"id": parts[len(parts)-1], "name": "registered"})
	case req.Method == http.MethodPost && path == "commits":
		for _, op := range body.Ops {
			r.known[op["id"].(string)] = true
		}
		answer(http.StatusOK, map[string]any{"id": "c", "offset": "1", "results": []any{}, "versions": map[string]any{}})
	case req.Method == http.MethodPost && len(parts) == 5:
		if !r.known[parts[3]] {
			refuse(http.StatusNotFound, "unknown_route")
			return
		}
		if body.HomeRegion != r.id {
			refuse(http.StatusMisdirectedRequest, "wrong_region")
			return
		}
		db := v1.ManagedDatabase{ID: "0199d000-0000-7000-8000-00000000000" + string(rune('0'+len(r.held))), ProjectID: parts[3], Name: body.Name,
			CreatedAt: time.Now().UTC(), Region: r.id, HomeRegion: r.id, ReplicaRegions: append([]string{}, body.ReplicaRegions...)}
		r.held[db.ID] = db
		answer(http.StatusCreated, db)
	case len(parts) == 6:
		db, ok := r.held[parts[5]]
		if !ok {
			w.Header().Set("X-TerraBase-Database-Home", "gru-1")
			w.Header().Set("X-TerraBase-Database-Regions", "gru-1,sfo-1")
			refuse(http.StatusMisdirectedRequest, "wrong_region")
			return
		}
		switch req.Method {
		case http.MethodPatch:
			db.ReplicaRegions = body.ReplicaRegions
			r.held[db.ID] = db
			answer(http.StatusOK, db)
		case http.MethodDelete:
			if len(db.ReplicaRegions) > 0 {
				refuse(http.StatusConflict, "database_replicated")
				return
			}
			delete(r.held, db.ID)
			w.WriteHeader(http.StatusNoContent)
		}
	default:
		refuse(http.StatusNotFound, "unknown_route")
	}
}

func (r *contractRegion) take() []string {
	r.mu.Lock()
	defer r.mu.Unlock()
	got := r.requests
	r.requests = nil
	return got
}

func TestTerraBasePlacementFollowsTheContract(t *testing.T) {
	gru := &contractRegion{id: "gru-1", known: map[string]bool{}, held: map[string]v1.ManagedDatabase{}}
	sfo := &contractRegion{id: "sfo-1", known: map[string]bool{}, held: map[string]v1.ManagedDatabase{}}
	gruServer, sfoServer := httptest.NewServer(gru), httptest.NewServer(sfo)
	defer gruServer.Close()
	defer sfoServer.Close()
	catalog := []core.Region{{Code: "GRU", ID: "gru-1", API: gruServer.URL}, {Code: "SFO", ID: "sfo-1", API: sfoServer.URL}}

	newService := func() (Service, core.Principal) {
		store := memstore.New()
		account, err := store.CreateAccount(t.Context(), core.User{Name: "Owner", Email: core.NewID("placement") + "@example.test", PasswordHash: "unused"})
		if err != nil {
			t.Fatal(err)
		}
		regions := func(context.Context) ([]core.Region, error) { return catalog, nil }
		// Neither demo nor DocumentsOnly: resources live in TerraBase.
		return Service{deps: core.Deps{Store: store, Regions: regions, SDK: tsgo.Factory{}}}, account.Principal()
	}
	ctx := core.WithUserToken(t.Context(), func() (string, bool) { return "user-token", true })
	// paths are the TerraBase paths of p's organization and project, whose
	// IDs derive from the dashboard's.
	paths := func(p core.Principal) (string, string) {
		org, proj := core.Project{ID: p.ProjectID}.TerraBaseIDs(p.OrganizationID)
		return "organizations/" + org, "organizations/" + org + "/projects/" + proj
	}
	// terraBaseID is the TerraBase ID the dashboard recorded for a database.
	terraBaseID := func(service Service, p core.Principal, id string) string {
		a, err := service.deps.Store.SnapshotAs(t.Context(), p)
		if err != nil {
			t.Fatal(err)
		}
		return a.Organizations[p.OrganizationID].Projects[p.ProjectID].Databases[id].TerraBaseID
	}

	// GRU only: registered and created in gru alone.
	service, p := newService()
	org, project := paths(p)
	db, err := service.Create(ctx, p, "orders", "GRU")
	if err != nil || !slices.Equal(db.Placement, []string{"GRU"}) || !strings.HasPrefix(db.ID, "db_") || !strings.HasPrefix(terraBaseID(service, p, db.ID), "0199d000") {
		t.Fatalf("create in GRU = %+v, %v", db, err)
	}
	if got, want := gru.take(), []string{"GET " + org, "GET " + project, "POST commits", "POST " + project + "/databases"}; !slices.Equal(got, want) {
		t.Fatalf("gru requests %v, want %v", got, want)
	}
	if got := sfo.take(); len(got) != 0 {
		t.Fatalf("sfo requests %v, want none", got)
	}

	// Both: registered in each region, created in the home with sfo-1 as replica.
	service, p = newService()
	_, project = paths(p)
	db, err = service.Create(ctx, p, "orders", "GRU", "SFO")
	if err != nil || !slices.Equal(db.Placement, []string{"GRU", "SFO"}) {
		t.Fatalf("create in both = %+v, %v", db, err)
	}
	held := terraBaseID(service, p, db.ID)
	if held := gru.held[held]; !slices.Equal(held.ReplicaRegions, []string{"sfo-1"}) {
		t.Fatalf("gru holds %+v, want sfo-1 as its replica", held)
	}
	if got := sfo.take(); !slices.Contains(got, "POST commits") || slices.Contains(got, "POST "+project+"/databases") {
		t.Fatalf("sfo requests %v, want a registration and no create", got)
	}
	gru.take()

	// Back to GRU only through the home region; GRU cannot be dropped.
	moved, err := service.SetRegions(ctx, p, db.ID, "GRU")
	if err != nil || !slices.Equal(moved.Placement, []string{"GRU"}) || len(gru.held[held].ReplicaRegions) != 0 {
		t.Fatalf("set GRU only = %+v, %v (held %+v)", moved, err, gru.held[held])
	}
	if got := gru.take(); !slices.Equal(got, []string{"PATCH " + project + "/databases/" + held}) {
		t.Fatalf("gru requests %v, want one PATCH", got)
	}
	if _, err := service.SetRegions(ctx, p, db.ID, "SFO"); !errors.Is(err, errHomeRegion) {
		t.Fatalf("move off the home = %v, want refused", err)
	}

	// A region that does not hold the database answers 421 with where it is.
	delete(gru.held, held)
	if _, err := service.SetRegions(ctx, p, db.ID, "GRU", "SFO"); !errors.Is(err, errWrongRegion) || !strings.Contains(err.Error(), "it lives in GRU, SFO") {
		t.Fatalf("set regions where the database is not held = %v, want wrong region", err)
	}
	if status, code := failure(placementError(&v1.APIError{Status: 409, Code: v1.ErrDatabaseReplicated}), ErrInvalidName); status != 409 || code != ErrReplicated {
		t.Fatalf("replicated delete failure = %d %s", status, code)
	}

	// SFO only, then the plan's one database is used up: the second create
	// never reaches TerraBase.
	service, p = newService()
	if db, err = service.Create(ctx, p, "orders", "SFO"); err != nil || db.Region != "SFO" || len(sfo.held) != 1 {
		t.Fatalf("create in SFO = %+v, %v", db, err)
	}
	before := len(gru.held)
	if _, err := service.Create(ctx, p, "events", "GRU", "SFO"); !errors.Is(err, core.ErrDatabaseLimit) || len(gru.held) != before {
		t.Fatalf("create over the plan limit = %v, gru holds %d (had %d)", err, len(gru.held), before)
	}

	// MCP callers have no ts-auth token to act with.
	service, p = newService()
	if _, err := service.Create(t.Context(), p, "other", "GRU"); !errors.Is(err, core.ErrNoUserToken) {
		t.Fatalf("create without a user token = %v", err)
	}
}
