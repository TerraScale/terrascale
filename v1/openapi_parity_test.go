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
)

type openAPIDoc struct {
	Paths map[string]map[string]json.RawMessage `json:"paths"`
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

// pinnedRoutes returns the public "METHOD /template" project routes.
func pinnedRoutes(doc openAPIDoc) []string {
	var routes []string
	for path, ops := range doc.Paths {
		if !strings.HasPrefix(path, "/v1/projects/") {
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

// notInJourney lists public project-token routes not exercised by this journey.
// Management-user calls use the separate /v1/management endpoints below.
var notInJourney = []string{
	"DELETE /v1/projects/{project}/backup-destination",
	"DELETE /v1/projects/{project}/databases/{database}",
	"DELETE /v1/projects/{project}/databases/{database}/collections/{collection}/schema",
	"DELETE /v1/projects/{project}/databases/{database}/indexes/{index}",
	"DELETE /v1/projects/{project}/saved-queries/{query}",
	"GET /v1/projects/{project}/analytics",
	"GET /v1/projects/{project}/backup-destination",
	"GET /v1/projects/{project}/databases",
	"GET /v1/projects/{project}/databases/{database}",
	"GET /v1/projects/{project}/databases/{database}/analytics",
	"GET /v1/projects/{project}/databases/{database}/auto-index/{collection}",
	"GET /v1/projects/{project}/databases/{database}/backup-policy",
	"GET /v1/projects/{project}/databases/{database}/backups",
	"GET /v1/projects/{project}/databases/{database}/collections/{collection}/changes",
	"GET /v1/projects/{project}/databases/{database}/collections/{collection}/schema",
	"GET /v1/projects/{project}/databases/{database}/collections/{collection}/shape",
	"GET /v1/projects/{project}/databases/{database}/collections/{collection}/subscribe",
	"GET /v1/projects/{project}/databases/{database}/index-recommendations",
	"GET /v1/projects/{project}/databases/{database}/indexes",
	"GET /v1/projects/{project}/databases/{database}/point-in-time",
	"GET /v1/projects/{project}/databases/{database}/query-shapes",
	"GET /v1/projects/{project}/databases/{database}/replication",
	"GET /v1/projects/{project}/databases/{database}/schema",
	"GET /v1/projects/{project}/databases/{database}/schema/{collection}",
	"GET /v1/projects/{project}/databases/{database}/usage",
	"GET /v1/projects/{project}/replication",
	"GET /v1/projects/{project}/saved-queries",
	"GET /v1/projects/{project}/saved-queries/{query}",
	"GET /v1/projects/{project}/usage",
	"PATCH /v1/projects/{project}/databases/{database}",
	"POST /v1/projects/{project}/databases",
	"POST /v1/projects/{project}/databases/{database}/backups",
	"POST /v1/projects/{project}/databases/{database}/backups/{backup}/restore",
	"POST /v1/projects/{project}/databases/{database}/branches",
	"POST /v1/projects/{project}/databases/{database}/index-recommendations/{recommendation}/apply",
	"POST /v1/projects/{project}/databases/{database}/indexes",
	"POST /v1/projects/{project}/databases/{database}/point-in-time/plan",
	"POST /v1/projects/{project}/databases/{database}/point-in-time/restore",
	"POST /v1/projects/{project}/databases/{database}/sql",
	"POST /v1/projects/{project}/databases/{database}/sql/check",
	"POST /v1/projects/{project}/databases/{database}/sql/explain",
	"POST /v1/projects/{project}/saved-queries",
	"PUT /v1/projects/{project}/backup-destination",
	"PUT /v1/projects/{project}/databases/{database}/auto-index/{collection}",
	"PUT /v1/projects/{project}/databases/{database}/backup-policy",
	"PUT /v1/projects/{project}/databases/{database}/collections/{collection}/schema",
	"PUT /v1/projects/{project}/databases/{database}/schema/{collection}",
	"PUT /v1/projects/{project}/saved-queries/{query}",
}

// TerraBase deliberately omits management-user routes and database PUT from
// public OpenAPI; keep the SDK journey coverage explicit for those endpoints.
var managementRoutes = []string{
	"DELETE /v1/management/organizations/{organization}",
	"DELETE /v1/management/organizations/{organization}/invitations/{invitation}",
	"DELETE /v1/management/organizations/{organization}/members/{user}",
	"DELETE /v1/management/organizations/{organization}/ownership-transfer",
	"DELETE /v1/management/organizations/{organization}/projects/{project}",
	"DELETE /v1/management/organizations/{organization}/projects/{project}/api-keys/{key}",
	"DELETE /v1/management/organizations/{organization}/projects/{project}/databases/{database}",
	"DELETE /v1/management/organizations/{organization}/projects/{project}/databases/{database}/indexes/{index}",
	"GET /v1/management/current-organization",
	"GET /v1/management/organizations",
	"GET /v1/management/organizations/{organization}",
	"GET /v1/management/organizations/{organization}/audit-events",
	"GET /v1/management/organizations/{organization}/audit-retention",
	"GET /v1/management/organizations/{organization}/invitations",
	"GET /v1/management/organizations/{organization}/members",
	"GET /v1/management/organizations/{organization}/ownership-transfer",
	"GET /v1/management/organizations/{organization}/projects",
	"GET /v1/management/organizations/{organization}/projects/{project}",
	"GET /v1/management/organizations/{organization}/projects/{project}/api-keys",
	"GET /v1/management/organizations/{organization}/projects/{project}/databases",
	"GET /v1/management/organizations/{organization}/projects/{project}/databases/{database}",
	"GET /v1/management/organizations/{organization}/projects/{project}/databases/{database}/collections",
	"GET /v1/management/organizations/{organization}/projects/{project}/databases/{database}/indexes",
	"GET /v1/management/organizations/{organization}/projects/{project}/databases/{database}/usage",
	"GET /v1/management/organizations/{organization}/projects/{project}/usage",
	"PATCH /v1/management/organizations/{organization}",
	"PATCH /v1/management/organizations/{organization}/projects/{project}",
	"POST /v1/management/commits",
	"POST /v1/management/invitations/accept",
	"POST /v1/management/organizations",
	"POST /v1/management/organizations/{organization}/invitations",
	"POST /v1/management/organizations/{organization}/leave",
	"POST /v1/management/organizations/{organization}/ownership-transfer",
	"POST /v1/management/organizations/{organization}/ownership-transfer/confirm",
	"POST /v1/management/organizations/{organization}/projects",
	"POST /v1/management/organizations/{organization}/projects/{project}/api-keys",
	"POST /v1/management/organizations/{organization}/projects/{project}/databases",
	"POST /v1/management/organizations/{organization}/projects/{project}/databases/{database}/branches",
	"POST /v1/management/organizations/{organization}/projects/{project}/databases/{database}/indexes",
	"POST /v1/management/organizations/{organization}/projects/{project}/databases/{database}/sql",
	"PUT /v1/management/current-organization",
	"PUT /v1/management/organizations/{organization}/audit-retention",
	"PUT /v1/management/organizations/{organization}/members/{user}/role",
	"PUT /v1/management/organizations/{organization}/plan",
	"PUT /v1/projects/{project}/databases/{database}",
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
