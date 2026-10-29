package v1_test

import (
	"encoding/json"
	"errors"
	"io"
	"net/http"
	"net/http/httptest"
	"reflect"
	"testing"

	tb "terrascale.tech/sdk/v1"
)

const parityProject = "01ARZ3NDEKTSV4RRFFQ69G5FAV"
const parityID = "01ARZ3NDEKTSV4RRFFQ69G5FAW"
const parityDatabase = "/v1/projects/" + parityProject + "/databases/main"
const parityDatabaseBody = `{"id":"` + parityID + `","projectId":"` + parityProject + `","name":"main","branchedFrom":null,"createdAt":"2026-10-22T00:00:00Z","region":"gru","homeRegion":"gru","replicaRegions":["sjc"]}`

// These fixtures assert the producer's wire contract without running TerraBase.
func parityClient(t *testing.T, method, path, query, body, response string, status int) *tb.Client {
	t.Helper()
	calls := 0
	server := httptest.NewServer(http.HandlerFunc(func(w http.ResponseWriter, r *http.Request) {
		calls++
		if r.Method != method || r.URL.Path != "/sdk-test"+path || r.URL.RawQuery != query {
			t.Errorf("request = %s %s; want %s %s?%s", r.Method, r.URL, method, path, query)
		}
		if r.Header.Get("Authorization") != "Bearer header.project.signature" || r.Header.Get("X-Terrabase-User-Id") != "" {
			t.Error("incorrect public authorization")
		}
		raw, err := io.ReadAll(r.Body)
		if err != nil {
			t.Error(err)
		}
		if body == "" {
			if len(raw) != 0 {
				t.Error("unexpected request body")
			}
		} else {
			var got, want any
			if json.Unmarshal(raw, &got) != nil || json.Unmarshal([]byte(body), &want) != nil || !reflect.DeepEqual(got, want) {
				t.Errorf("request body = %s; want %s", raw, body)
			}
			if r.Header.Get("Content-Type") != "application/json" {
				t.Error("incorrect JSON content type")
			}
		}
		w.Header().Set("Content-Type", "application/json")
		w.WriteHeader(status)
		io.WriteString(w, response)
	}))
	t.Cleanup(server.Close)
	t.Cleanup(func() {
		if calls != 1 {
			t.Errorf("requests = %d; want 1", calls)
		}
	})
	c, err := tb.New(tb.Config{BaseURL: server.URL + "/sdk-test", Token: "header.project.signature", HTTPClient: server.Client()})
	if err != nil {
		t.Fatal(err)
	}
	return c
}

func TestPublicDatabasePlacementAndBranch(t *testing.T) {
	for _, name := range []string{"create", "clear replicas", "branch"} {
		t.Run(name, func(t *testing.T) {
			method, path, body := http.MethodPost, parityDatabase+"/branches", `{"name":"preview"}`
			switch name {
			case "create":
				path, body = "/v1/projects/"+parityProject+"/databases", `{"name":"main","homeRegion":"gru","replicaRegions":["sjc"]}`
			case "clear replicas":
				method, path, body = http.MethodPatch, parityDatabase, `{"replicaRegions":[]}`
			}
			status := http.StatusCreated
			if name == "clear replicas" {
				status = http.StatusOK
			}
			c := parityClient(t, method, path, "", body, parityDatabaseBody, status)
			d := c.Database(parityProject, "main")
			var out *tb.ManagedDatabase
			var err error
			switch name {
			case "create":
				out, err = d.CreateIn(t.Context(), tb.Placement{HomeRegion: "gru", ReplicaRegions: []string{"sjc"}})
			case "clear replicas":
				out, err = d.SetRegions(t.Context(), nil)
			default:
				out, err = d.Branch(t.Context(), "preview")
			}
			if err != nil || out.ID != parityID || out.ProjectID != parityProject || out.Name != "main" || out.BranchedFrom != nil || out.Region != "gru" || out.HomeRegion == nil || *out.HomeRegion != "gru" || len(out.ReplicaRegions) != 1 || out.CreatedAt.IsZero() {
				t.Fatalf("response = %+v, %v", out, err)
			}
		})
	}
}

func TestPublicBranchPropagatesPlacementDenial(t *testing.T) {
	c := parityClient(t, http.MethodPost, parityDatabase+"/branches", "", `{"name":"preview"}`, `{"error":{"code":"wrong_region","message":"use home region"}}`, http.StatusMisdirectedRequest)
	_, err := c.Database(parityProject, "main").Branch(t.Context(), "preview")
	if !errors.Is(err, tb.ErrWrongRegion) {
		t.Fatalf("error = %v", err)
	}
}
