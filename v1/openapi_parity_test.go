package v1_test

// Helpers for the pinned OpenAPI, testdata/openapi.json. TestIntegration
// checks the real traffic of the client against its routes; see
// integration_test.go for how it is pinned.

import (
	"encoding/json"
	"os"
	"slices"
	"strings"
	"testing"

	v1 "terrascale.tech/sdk/v1"
)

type openAPIDoc struct {
	Paths      map[string]map[string]json.RawMessage `json:"paths"`
	Components struct {
		Schemas map[string]struct {
			Enum       []string `json:"enum"`
			Properties map[string]struct {
				Enum []string `json:"enum"`
			} `json:"properties"`
		} `json:"schemas"`
	} `json:"components"`
}

func loadOpenAPI(t *testing.T) openAPIDoc {
	t.Helper()
	data, err := os.ReadFile("testdata/openapi.json")
	if err != nil {
		t.Fatal(err)
	}
	var doc openAPIDoc
	if err := json.Unmarshal(data, &doc); err != nil {
		t.Fatalf("testdata/openapi.json: %v", err)
	}
	return doc
}

// pinnedRoutes returns the "METHOD /template" data and Management routes.
func pinnedRoutes(doc openAPIDoc) []string {
	var routes []string
	for path, ops := range doc.Paths {
		if !strings.HasPrefix(path, "/v1/projects/") && !strings.HasPrefix(path, "/v1/management/") {
			continue
		}
		for method := range ops {
			switch m := strings.ToUpper(method); m {
			case "GET", "PUT", "POST", "PATCH", "DELETE":
				routes = append(routes, m+" "+path)
			}
		}
	}
	slices.Sort(routes)
	return routes
}

// notInClient lists the pinned routes the client does not call yet; TS-590
// adds them group by group.
var notInClient = []string{
	"DELETE /v1/management/organizations/{organization}/projects/{project}/saved-queries/{query}",
	"GET /v1/management/commits/{id}",
	"GET /v1/management/documents/{collection}/{id}",
	"GET /v1/management/organizations/{organization}/projects/{project}/databases/{database}/backup-policy",
	"GET /v1/management/organizations/{organization}/projects/{project}/databases/{database}/backups",
	"GET /v1/management/organizations/{organization}/projects/{project}/databases/{database}/collections/{collection}/schema",
	"GET /v1/management/organizations/{organization}/projects/{project}/databases/{database}/index-recommendations",
	"GET /v1/management/organizations/{organization}/projects/{project}/saved-queries",
	"GET /v1/management/organizations/{organization}/projects/{project}/saved-queries/{query}",
	"GET /v1/management/versions",
	"GET /v1/projects/{project}/databases/{database}/collections/{collection}/shape",
	"POST /v1/management/documents/{collection}/query",
	"POST /v1/management/invitations/lookup",
	"POST /v1/management/organizations/{organization}/projects/{project}/databases/{database}/backups",
	"POST /v1/management/organizations/{organization}/projects/{project}/databases/{database}/backups/{backup}/restore",
	"POST /v1/management/organizations/{organization}/projects/{project}/databases/{database}/index-recommendations/{recommendation}/apply",
	"POST /v1/management/organizations/{organization}/projects/{project}/databases/{database}/sql/check",
	"POST /v1/management/organizations/{organization}/projects/{project}/saved-queries",
	"PUT /v1/management/organizations/{organization}/projects/{project}/databases/{database}/backup-policy",
	"PUT /v1/management/organizations/{organization}/projects/{project}/saved-queries/{query}",
}

// adminRoutes are served only on the admin listener, so the OpenAPI of the
// client listener leaves them out.
var adminRoutes = []string{
	"PUT /v1/management/organizations/{organization}/members/{user}",
}

// routeMatches reports whether "METHOD /path" matches "METHOD /template",
// where a {param} segment matches any non-empty segment.
func routeMatches(route, request string) bool {
	rm, rp, _ := strings.Cut(route, " ")
	qm, qp, _ := strings.Cut(request, " ")
	rs, qs := strings.Split(rp, "/"), strings.Split(qp, "/")
	if rm != qm || len(rs) != len(qs) {
		return false
	}
	for i, s := range rs {
		if strings.HasPrefix(s, "{") && strings.HasSuffix(s, "}") {
			if qs[i] == "" {
				return false
			}
		} else if s != qs[i] {
			return false
		}
	}
	return true
}

// TestOpenAPIParityEnums checks the client's constants against the enums of
// the pinned OpenAPI: every error code enumerated on a "code" property, and
// the organization plans and member roles.
func TestOpenAPIParityEnums(t *testing.T) {
	doc := loadOpenAPI(t)
	known := map[string]bool{}
	for _, c := range []v1.ErrorCode{
		v1.ErrNotModified, v1.ErrInvalidRequest, v1.ErrInvalidQuery, v1.ErrInvalidCursor, v1.ErrInvalidPatch,
		v1.ErrSortRequiresIndex, v1.ErrAssertRequiresIndex, v1.ErrInvalidKey, v1.ErrForbiddenScope, v1.ErrNotFound,
		v1.ErrUnknownDatabase, v1.ErrUnknownCollection, v1.ErrUnknownRoute, v1.ErrMethodNotAllowed, v1.ErrNameTaken,
		v1.ErrPatchConflict, v1.ErrPreconditionFailed, v1.ErrPayloadTooLarge, v1.ErrUnsupportedMediaType,
		v1.ErrRateLimited, v1.ErrInternalError, v1.ErrCapacity, v1.ErrStorageUnavailable, v1.ErrStoredCorruption,
		v1.ErrCommitUncertain, v1.ErrDependenciesUnavail, v1.ErrDatabaseReplicated, v1.ErrIndexExists, v1.ErrWrongRegion,
	} {
		known[string(c)] = true
	}
	for name, s := range doc.Components.Schemas {
		for _, code := range s.Properties["code"].Enum {
			if !known[code] {
				t.Errorf("schema %s enumerates error code %q, which has no v1.ErrorCode", name, code)
			}
		}
	}

	for schema, want := range map[string][]string{
		"PlanTier":         {string(v1.PlanHobby), string(v1.PlanPro), string(v1.PlanEnterprise)},
		"OrganizationRole": {string(v1.RoleViewer), string(v1.RoleMember), string(v1.RoleAdmin), string(v1.RoleOwner)},
		"InvitationStatus": {string(v1.InvitationPending), string(v1.InvitationAccepted), string(v1.InvitationDeclined), string(v1.InvitationRevoked)},
	} {
		got := slices.Sorted(slices.Values(doc.Components.Schemas[schema].Enum))
		if slices.Sort(want); !slices.Equal(got, want) {
			t.Errorf("schema %s enum = %q, client has %q", schema, got, want)
		}
	}
}
