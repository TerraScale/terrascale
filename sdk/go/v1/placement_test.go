package v1_test

import (
	"encoding/json"
	"errors"
	"io"
	"net/http"
	"os"
	"reflect"
	"strings"
	"testing"

	tb "terrascale.tech/sdk/v1"
)

type placementFixture struct {
	Request struct {
		Method string
		Path   string
		Body   json.RawMessage
	}
	Response struct {
		Status int
		Body   json.RawMessage
	}
	header http.Header
}

func loadPlacement(t *testing.T, name string) placementFixture {
	t.Helper()
	data, err := os.ReadFile("../contracts/terrabase/placement/" + name + ".json")
	if err != nil {
		t.Fatal(err)
	}
	var f placementFixture
	if err := json.Unmarshal(data, &f); err != nil {
		t.Fatal(err)
	}
	return f
}

func placementClient(t *testing.T, fixtures ...placementFixture) *tb.Client {
	t.Helper()
	calls := 0
	hc := &http.Client{Transport: agentTransport(func(r *http.Request) (*http.Response, error) {
		if calls >= len(fixtures) {
			t.Errorf("unexpected request %s %s", r.Method, r.URL)
			return nil, errors.New("unexpected request")
		}
		f := fixtures[calls]
		calls++
		data, _ := io.ReadAll(r.Body)
		var got, want any
		if err := json.Unmarshal(data, &got); err != nil {
			t.Fatal(err)
		}
		if err := json.Unmarshal(f.Request.Body, &want); err != nil {
			t.Fatal(err)
		}
		if r.Method != f.Request.Method || r.URL.Path != f.Request.Path || !reflect.DeepEqual(got, want) {
			t.Errorf("request %s %s %s, want %s %s %s", r.Method, r.URL.Path, data, f.Request.Method, f.Request.Path, f.Request.Body)
		}
		header := http.Header{"Content-Type": {"application/json"}}
		for k, v := range f.header {
			header[k] = v
		}
		return &http.Response{StatusCode: f.Response.Status, Header: header, Body: io.NopCloser(strings.NewReader(string(f.Response.Body))), Request: r}, nil
	})}
	t.Cleanup(func() {
		if calls != len(fixtures) {
			t.Errorf("requests=%d, want %d", calls, len(fixtures))
		}
	})
	c, err := tb.New(tb.Config{Token: "fixture.jwt.sig", HTTPClient: hc})
	if err != nil {
		t.Fatal(err)
	}
	return c
}

const (
	placementProject  = "01JA0000000000000000000001"
	placementDatabase = "01JA0000000000000000000004"
)

func TestPlacementContract(t *testing.T) {
	unresolved := loadPlacement(t, "move-home")
	unresolved.Response.Status = http.StatusServiceUnavailable
	unresolved.Response.Body = json.RawMessage(`{"error":{"code":"move_unresolved","message":"retry"}}`)
	unresolved.header = http.Header{"Retry-After": {"0"}}
	c := placementClient(t, loadPlacement(t, "create-with-replicas"), loadPlacement(t, "create-all-regions"),
		loadPlacement(t, "set-replicas"), loadPlacement(t, "set-all-regions"), unresolved, loadPlacement(t, "move-home"))
	ctx := t.Context()
	db, err := c.CreateDatabase(ctx, placementProject, "orders", tb.CreateDatabaseOptions{HomeRegion: "gru", ReplicaRegions: []string{"sjc"}})
	if err != nil || *db.HomeRegion != "gru" || !reflect.DeepEqual(db.ReplicaRegions, []string{"sjc"}) || db.AllRegions {
		t.Fatalf("create = %+v, %v", db, err)
	}
	if db, err = c.CreateDatabase(ctx, placementProject, "orders", tb.CreateDatabaseOptions{HomeRegion: "gru", AllRegions: true}); err != nil || !db.AllRegions {
		t.Fatalf("create all regions = %+v, %v", db, err)
	}
	if db, err = c.SetDatabasePlacement(ctx, placementProject, placementDatabase, tb.DatabasePlacement{ReplicaRegions: []string{"fra", "sjc"}}); err != nil || db.AllRegions || len(db.ReplicaRegions) != 2 {
		t.Fatalf("set replicas = %+v, %v", db, err)
	}
	if db, err = c.SetDatabasePlacement(ctx, placementProject, placementDatabase, tb.DatabasePlacement{AllRegions: true}); err != nil || !db.AllRegions {
		t.Fatalf("set all regions = %+v, %v", db, err)
	}
	if db, err = c.MoveDatabaseHome(ctx, placementProject, placementDatabase, "sjc"); err != nil || *db.HomeRegion != "sjc" || !reflect.DeepEqual(db.ReplicaRegions, []string{"fra", "gru"}) {
		t.Fatalf("move home = %+v, %v", db, err)
	}
}

func TestMoveHomeRefusalIsNotRetried(t *testing.T) {
	c := placementClient(t, loadPlacement(t, "move-home-refused"))
	_, err := c.MoveDatabaseHome(t.Context(), placementProject, placementDatabase, "fra")
	var apiErr *tb.APIError
	if !errors.As(err, &apiErr) || apiErr.Status != http.StatusBadRequest || apiErr.Code != tb.ErrInvalidRequest {
		t.Fatalf("move home = %v, want 400 invalid_request", err)
	}
	if _, err := c.SetDatabasePlacement(t.Context(), placementProject, placementDatabase, tb.DatabasePlacement{}); err == nil {
		t.Fatal("placement without replicaRegions or allRegions was accepted")
	}
}
