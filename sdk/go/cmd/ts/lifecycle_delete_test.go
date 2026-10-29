package main

import (
	"bytes"
	"encoding/json"
	"errors"
	"io"
	"net/http"
	"strings"
	"sync/atomic"
	"testing"

	"terrascale.tech/sdk/tsauth"
)

func TestLifecycleConsentUsesCanonicalIDAndRefusesOffline(t *testing.T) {
	t.Parallel()
	for _, name := range []string{"orgs", "projects"} {
		if err := runWithInput(t.Context(), []string{name, "delete", "selected"}, io.Discard, nil, io.Discard, false); err == nil || errors.Is(err, errUsage) {
			t.Fatalf("unattended %s deletion was not refused before sign-in: %v", name, err)
		}
	}
	for _, input := range []string{"wrong\n", managementProjectID + "\n"} {
		var prompt bytes.Buffer
		err := confirmManagementTarget("Delete project", "app", managementProjectID, false, strings.NewReader(input), &prompt, true)
		if (err == nil) != strings.HasPrefix(input, managementProjectID) || !strings.Contains(prompt.String(), managementProjectID) {
			t.Fatalf("typed consent = %v, %q", err, prompt.String())
		}
	}
	if err := confirmManagementTarget("Delete project", "app", managementProjectID, false, nil, managementBrokenWriter{}, true); !errors.Is(err, io.ErrClosedPipe) {
		t.Fatalf("prompt failure = %v", err)
	}
}

func TestProjectDeleteGuardsAndFailures(t *testing.T) {
	t.Parallel()
	for _, mode := range []string{"nonempty", "later-page-nonempty", "list-denied", "consent-denied", "delete-denied", "success", "pending", "writer-failed"} {
		t.Run(mode, func(t *testing.T) {
			t.Parallel()
			var deletes, prompts atomic.Int32
			u := managementUser(t, func(w http.ResponseWriter, r *http.Request) {
				if r.Method != "DELETE" || r.URL.Path != "/v1/terrascale/organizations/"+managementOrgID+"/projects/"+managementProjectID {
					t.Errorf("unexpected project deletion %s %s", r.Method, r.URL.Path)
				}
				deletes.Add(1)
				if mode == "delete-denied" {
					w.WriteHeader(http.StatusForbidden)
					io.WriteString(w, `{"error":{"code":"forbidden_role","message":"admin required"}}`)
					return
				}
				if mode == "pending" {
					w.WriteHeader(http.StatusAccepted)
					return
				}
				w.WriteHeader(http.StatusNoContent)
			})
			c := managementTerraBase(t, func(w http.ResponseWriter, r *http.Request) {
				if r.Method != "GET" || r.URL.Path != "/v1/projects/"+managementProjectID+"/databases" {
					t.Errorf("unexpected preflight %s %s", r.Method, r.URL.Path)
				}
				switch {
				case mode == "list-denied":
					w.WriteHeader(http.StatusForbidden)
					io.WriteString(w, `{"error":{"code":"forbidden","message":"read required"}}`)
				case mode == "later-page-nonempty" && r.URL.Query().Get("after") == "":
					io.WriteString(w, `{"items":[],"next":"second"}`)
				case mode == "nonempty" || mode == "later-page-nonempty":
					io.WriteString(w, `{"items":[{"id":"`+managementOrgID+`","projectId":"`+managementProjectID+`","name":"orders"}],"next":null}`)
				default:
					io.WriteString(w, `{"items":[],"next":null}`)
				}
			})
			refused := errors.New("consent refused")
			consent := func(kind, name, id string) error {
				prompts.Add(1)
				if kind != "project" || name != "app" || id != managementProjectID {
					t.Errorf("wrong confirmed identity %s %s %s", kind, name, id)
				}
				if mode == "consent-denied" {
					return refused
				}
				return nil
			}
			var out bytes.Buffer
			var writer io.Writer = &out
			if mode == "writer-failed" {
				writer = managementBrokenWriter{}
			}
			err := deleteProjectLifecycle(t.Context(), u, c, &tsauth.Project{ID: managementProjectID, OrganizationID: managementOrgID, Name: "app"}, consent, writer)
			switch mode {
			case "success", "pending":
				var result struct {
					Deleted bool `json:"deleted"`
					Pending bool `json:"pending"`
				}
				if decodeErr := json.Unmarshal(out.Bytes(), &result); decodeErr != nil || result.Deleted != (mode == "success") || result.Pending != (mode == "pending") {
					t.Fatalf("closure outcome = %q, %v", out.String(), decodeErr)
				}
				if err != nil || deletes.Load() != 1 || !json.Valid(out.Bytes()) {
					t.Fatalf("delete result = %v, calls %d, %q", err, deletes.Load(), out.String())
				}
			case "writer-failed", "delete-denied":
				if err == nil || deletes.Load() != 1 {
					t.Fatalf("post-request failure = %v, calls %d", err, deletes.Load())
				}
			default:
				if err == nil || deletes.Load() != 0 || out.Len() != 0 {
					t.Fatalf("unsafe deletion = %v, calls %d, output %q", err, deletes.Load(), out.String())
				}
			}
			if mode == "nonempty" || mode == "later-page-nonempty" || mode == "list-denied" {
				if prompts.Load() != 0 {
					t.Fatal("prompted before empty-project preflight succeeded")
				}
			}
		})
	}
}

func TestOrganizationDeleteRefusalNeverMutates(t *testing.T) {
	t.Parallel()
	u := managementUser(t, func(w http.ResponseWriter, r *http.Request) {
		if r.Method != "GET" {
			t.Error("organization mutated after refusal")
		}
		json.NewEncoder(w).Encode(tsauth.Page[tsauth.Organization]{Items: []tsauth.Organization{{ID: managementOrgID, CreatedBy: managementOrgID, Name: "workspace"}}})
	})
	refused := errors.New("refused")
	err := deleteOrganizationLifecycle(t.Context(), u, managementOrgID, func(_, _, _ string) error { return refused }, io.Discard)
	if !errors.Is(err, refused) {
		t.Fatalf("organization refusal = %v", err)
	}
}
