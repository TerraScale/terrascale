package main

import (
	"bytes"
	"encoding/json"
	"errors"
	"io"
	"net/http"
	"strings"
	"testing"
)

func TestPlacementFlagsAreScopedAndHelpSkipsValues(t *testing.T) {
	t.Parallel()
	for _, args := range [][]string{{"-placement", "all", "db", "list"}, {"-replica-regions", "", "db", "create", "orders"},
		{"-placement", "all", "-replica-regions", "sjc", "db", "create", "orders"}} {
		if err := runWithInput(t.Context(), args, io.Discard, nil, io.Discard, false); !errors.Is(err, errUsage) {
			t.Errorf("placement parser = %v", err)
		}
	}
	var out bytes.Buffer
	if handled, err := requestedHelp([]string{"-home-region", "gru", "db", "create", "--help"}, &out); !handled || err != nil || !strings.Contains(out.String(), "-placement all") {
		t.Fatalf("placement help = %v, %q", err, out.String())
	}
	if _, structured, err := jsonArguments([]string{"-home-region", "--json", "db", "create", "orders"}); err != nil || structured {
		t.Fatal("placement value was interpreted as an output flag")
	}
}

func TestPlacementCreatePreservesNilEmptyAndAll(t *testing.T) {
	t.Parallel()
	for _, tc := range []struct {
		replicas, placement, want string
		set                       bool
	}{{"", "", "null", false}, {"none", "", "[]", true}, {"sjc,fra", "", `["sjc","fra"]`, true}, {"", "all", "null", false}} {
		t.Run(tc.want+tc.placement, func(t *testing.T) {
			t.Parallel()
			p, err := placementRequest("gru", tc.replicas, tc.placement, tc.set)
			if err != nil {
				t.Fatal(err)
			}
			c := managementTerraBase(t, func(w http.ResponseWriter, r *http.Request) {
				if r.Method != "POST" || r.URL.Path != "/v1/projects/"+managementProjectID+"/databases" {
					t.Errorf("unexpected create %s %s", r.Method, r.URL.Path)
				}
				var body map[string]json.RawMessage
				if err := json.NewDecoder(r.Body).Decode(&body); err != nil {
					t.Error(err)
				}
				if string(body["replicaRegions"]) != tc.want || string(body["homeRegion"]) != `"gru"` || string(body["name"]) != `"orders"` {
					t.Errorf("create body = %v", body)
				}
				if tc.placement == "all" && string(body["allRegions"]) != "true" {
					t.Error("all-regions intent missing")
				}
				io.WriteString(w, `{"id":"`+managementOrgID+`","projectId":"`+managementProjectID+`","name":"orders","homeRegion":"gru"}`)
			})
			var out bytes.Buffer
			if err := createPlacedDatabase(t.Context(), c.Database(managementProjectID, "orders"), p, &out); err != nil || !json.Valid(out.Bytes()) {
				t.Fatalf("placed create = %v, %q", err, out.String())
			}
		})
	}
}

func TestPlacementRejectsConflictsAndInvalidNames(t *testing.T) {
	t.Parallel()
	for _, tc := range []struct {
		home, replicas, mode string
		set                  bool
	}{{"gru", "sjc", "all", true}, {"gru", "gru", "", true}, {"gru", "sjc,sjc", "", true},
		{"gru", "", "", true}, {"unsafe value", "", "", false}, {"gru", "sjc", "invalid", true}} {
		if _, err := placementRequest(tc.home, tc.replicas, tc.mode, tc.set); !errors.Is(err, errUsage) {
			t.Errorf("invalid placement accepted: %v", err)
		}
	}
}

func TestPlacementDoesNotMaskServerPlacementRejection(t *testing.T) {
	t.Parallel()
	c := managementTerraBase(t, func(w http.ResponseWriter, _ *http.Request) {
		w.WriteHeader(http.StatusBadRequest)
		io.WriteString(w, `{"error":{"code":"placement_needs_two_regions","message":"two regions required"}}`)
	})
	p, err := placementRequest("gru", "none", "", true)
	if err != nil {
		t.Fatal(err)
	}
	var out bytes.Buffer
	if err := createPlacedDatabase(t.Context(), c.Database(managementProjectID, "orders"), p, &out); err == nil || out.Len() != 0 {
		t.Fatalf("placement rejection = %v, %q", err, out.String())
	}
}
