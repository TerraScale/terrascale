package v1_test

import (
	"bytes"
	"encoding/json"
	"errors"
	"io"
	"net/http"
	"net/http/httptest"
	"sync/atomic"
	"testing"

	"terrascale.tech/sdk/v1"
)

func TestProjectRegistrationRetriesTheCapturedCommit(t *testing.T) {
	for _, outcome := range []string{"lost response", "commit_uncertain"} {
		t.Run(outcome, func(t *testing.T) {
			var reads, commits atomic.Int64
			bodies := make(chan []byte, 2)
			peer := httptest.NewTLSServer(http.HandlerFunc(func(w http.ResponseWriter, r *http.Request) {
				w.Header().Set("Content-Type", "application/json")
				if r.Method == http.MethodGet {
					reads.Add(1)
					w.WriteHeader(http.StatusNotFound)
					io.WriteString(w, `{"error":{"code":"not_found"}}`)
					return
				}
				if r.Method != http.MethodPost || r.URL.Path != "/v1/management/commits" {
					t.Errorf("unexpected request: %s %s", r.Method, r.URL.Path)
				}
				body, err := io.ReadAll(r.Body)
				if err != nil {
					t.Error(err)
				}
				select {
				case bodies <- body:
				default:
					t.Error("unexpected extra commit")
				}
				if commits.Add(1) == 1 {
					if outcome == "lost response" {
						panic(http.ErrAbortHandler)
					}
					w.WriteHeader(http.StatusServiceUnavailable)
					io.WriteString(w, `{"error":{"code":"commit_uncertain"}}`)
					return
				}
				io.WriteString(w, `{}`)
			}))
			t.Cleanup(peer.Close)
			m, err := v1.NewManagement(v1.Config{BaseURL: peer.URL, HTTPClient: peer.Client()})
			if err != nil {
				t.Fatal(err)
			}
			org := v1.Organization{ID: testOrg, Name: "<Org>\u2028"}
			project := v1.Project{ID: testProject, Name: "Orders"}
			registration, err := m.WithToken("test-access-token").PrepareProjectRegistration(t.Context(), org, project)
			if err != nil {
				t.Fatal(err)
			}
			if reads.Load() != 2 || commits.Load() != 0 {
				t.Fatal("preparation must only check the two identities")
			}
			if err := registration.Commit(t.Context()); !errors.Is(err, v1.ErrCommitUncertain) {
				t.Fatalf("first commit: %v", err)
			}
			if commits.Load() != 1 {
				t.Fatal("uncertain mutation was automatically retried")
			}
			org.Name, project.Name = "Changed", "Changed"
			if err := registration.Commit(t.Context()); err != nil {
				t.Fatalf("retry: %v", err)
			}
			if reads.Load() != 2 || commits.Load() != 2 {
				t.Fatal("retry must resend the commit without new existence checks")
			}
			first, retry := <-bodies, <-bodies
			if !bytes.Equal(first, retry) {
				t.Fatal("retry changed the commit body")
			}
			var commit struct {
				ID  string `json:"id"`
				Ops []struct {
					Op, ID, Organization, Name string
				} `json:"ops"`
			}
			if err := json.Unmarshal(first, &commit); err != nil {
				t.Fatal(err)
			}
			if v1.ValidateID(commit.ID) != nil || len(commit.Ops) != 2 {
				t.Fatal("expected one canonical commit ID and two creation operations")
			}
			if commit.Ops[0].Op != "create_organization" || commit.Ops[0].ID != testOrg || commit.Ops[0].Name != "<Org>\u2028" || commit.Ops[1].Op != "create_project" || commit.Ops[1].ID != testProject || commit.Ops[1].Organization != testOrg || commit.Ops[1].Name != "Orders" {
				t.Fatal("registration did not capture the requested identities")
			}
		})
	}
}

func TestProjectRegistrationAlreadyExists(t *testing.T) {
	transport := &scriptTransport{responses: []scripted{
		{status: http.StatusOK, body: `{"id":"` + testOrg + `","createdBy":"` + testProject + `"}`},
		{status: http.StatusOK, body: `{"id":"` + testProject + `","organizationId":"` + testOrg + `"}`},
	}}
	m, err := v1.NewManagement(v1.Config{BaseURL: "https://tb.example", HTTPClient: &http.Client{Transport: transport}})
	if err != nil {
		t.Fatal(err)
	}
	registration, err := m.WithToken("test-access-token").PrepareProjectRegistration(t.Context(), v1.Organization{ID: testOrg}, v1.Project{ID: testProject})
	if err != nil {
		t.Fatal(err)
	}
	for range 2 {
		if err := registration.Commit(t.Context()); err != nil {
			t.Fatal(err)
		}
	}
	if len(transport.requests) != 2 {
		t.Fatal("existing registration must not submit a commit")
	}
}
