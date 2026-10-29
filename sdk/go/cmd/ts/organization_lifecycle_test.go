package main

import (
	"bytes"
	"encoding/json"
	"errors"
	"io"
	"net/http"
	"strings"
	"testing"

	"terrascale.tech/sdk/tsauth"
)

const managementOrgID = "01M5N9000000000000000000AB"

func TestProjectInventoryRequiresUniqueOrganizationAndHonorsID(t *testing.T) {
	t.Parallel()
	u := managementUser(t, func(w http.ResponseWriter, r *http.Request) {
		if r.URL.Path == "/v1/terrascale/organizations" {
			json.NewEncoder(w).Encode(tsauth.Page[tsauth.Organization]{Items: []tsauth.Organization{
				{ID: managementOrgID, Name: "duplicate", CreatedBy: managementOrgID},
				{ID: managementProjectID, Name: "duplicate", CreatedBy: managementOrgID},
			}})
			return
		}
		if r.URL.Path != "/v1/terrascale/organizations/"+managementOrgID+"/projects" {
			t.Error("project lookup escaped explicit organization")
		}
		json.NewEncoder(w).Encode(tsauth.Page[tsauth.Project]{Items: []tsauth.Project{{ID: managementProjectID, OrganizationID: managementOrgID, CreatedBy: managementOrgID}}})
	})
	for _, err := range projects(t.Context(), u, "duplicate") {
		if err == nil {
			t.Fatal("ambiguous organization accepted")
		}
	}
	var out bytes.Buffer
	if err := listProjects(t.Context(), u, managementOrgID, &out); err != nil || !strings.Contains(out.String(), managementProjectID) {
		t.Fatalf("scoped inventory = %v, %q", err, out.String())
	}
}

func TestOrganizationAndProjectLifecycleSDKRoutes(t *testing.T) {
	t.Parallel()
	for _, command := range []string{"orgs create", "orgs info", "orgs rename", "projects info", "projects rename"} {
		t.Run(command, func(t *testing.T) {
			t.Parallel()
			org := tsauth.Organization{ID: managementOrgID, Name: "workspace", CreatedBy: managementOrgID}
			project := tsauth.Project{ID: managementProjectID, OrganizationID: managementOrgID, Name: "app", CreatedBy: managementOrgID}
			u := managementUser(t, func(w http.ResponseWriter, r *http.Request) {
				base := "/v1/terrascale/organizations"
				if r.Method == "GET" && r.URL.Path == base {
					json.NewEncoder(w).Encode(tsauth.Page[tsauth.Organization]{Items: []tsauth.Organization{org}})
					return
				}
				if r.Method == "GET" && r.URL.Path == base+"/"+managementOrgID+"/projects" {
					json.NewEncoder(w).Encode(tsauth.Page[tsauth.Project]{Items: []tsauth.Project{project}})
					return
				}
				method, path := "GET", base+"/"+managementOrgID
				if strings.HasPrefix(command, "projects ") {
					path += "/projects/" + managementProjectID
				}
				if command == "orgs create" {
					method, path = "POST", base
				} else if strings.HasSuffix(command, "rename") {
					method = "PATCH"
				}
				if r.Method != method || r.URL.Path != path {
					t.Errorf("unexpected lifecycle request %s %s", r.Method, r.URL.Path)
				}
				if method != "GET" {
					var body struct{ Name string }
					if err := json.NewDecoder(r.Body).Decode(&body); err != nil || body.Name != "renamed" {
						t.Errorf("name body = %+v, %v", body, err)
					}
				}
				if strings.HasPrefix(command, "orgs ") {
					json.NewEncoder(w).Encode(org)
				} else {
					json.NewEncoder(w).Encode(project)
				}
			})
			var out bytes.Buffer
			var err error
			if strings.HasPrefix(command, "orgs ") {
				args := []string{managementOrgID, "renamed"}
				if command == "orgs create" {
					args = []string{"renamed"}
				}
				err = organizationLifecycle(t.Context(), u, command, args, &out)
			} else {
				err = projectLifecycle(t.Context(), u, managementOrgID, command, []string{managementProjectID, "renamed"}, &out)
			}
			if err != nil || !json.Valid(out.Bytes()) {
				t.Fatalf("lifecycle output = %q, %v", out.String(), err)
			}
		})
	}
}

func TestOrganizationLifecycleDoesNotHideDeniedMutation(t *testing.T) {
	t.Parallel()
	u := managementUser(t, func(w http.ResponseWriter, _ *http.Request) {
		w.WriteHeader(http.StatusForbidden)
		io.WriteString(w, `{"error":{"code":"forbidden_role","message":"admin required"}}`)
	})
	var out bytes.Buffer
	if err := organizationLifecycle(t.Context(), u, "orgs create", []string{"workspace"}, &out); !errors.Is(err, tsauth.ErrForbiddenRole) || out.Len() != 0 {
		t.Fatalf("denied creation = %v, %q", err, out.String())
	}
}

func TestOrganizationLifecyclePropagatesWriterFailure(t *testing.T) {
	t.Parallel()
	u := managementUser(t, func(w http.ResponseWriter, _ *http.Request) {
		json.NewEncoder(w).Encode(tsauth.Organization{ID: managementOrgID, CreatedBy: managementOrgID})
	})
	if err := organizationLifecycle(t.Context(), u, "orgs create", []string{"workspace"}, managementBrokenWriter{}); !errors.Is(err, io.ErrClosedPipe) {
		t.Fatalf("creation writer failure = %v", err)
	}
}
